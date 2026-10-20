using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryTelemetryCapacityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Actual_store_accounts_for_exact_DTO_UTF8_and_record_metadata_at_10KiB_edge(int excess)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);
        var authority = JsonSerializer.Deserialize<FactoryTelemetry>(preview.Result["telemetry"]!.ToJsonString(), FactoryGitHubJson.Options)!;
        const string key = "dsf/factory/telemetry";
        var initial = JsonSerializer.Serialize(authority, FactoryGitHubJson.Options);
        var value = JsonSerializer.Serialize(authority with
        { Detail = authority.Detail + new string('x', 10240 - TotalBytes(key, initial) + excess) }, FactoryGitHubJson.Options);
        Assert.Equal(10240 + excess, TotalBytes(key, value));
        using var http = new HttpClient(arm);
        var store = new OwnerReservationStore(authority.Configuration.ProductEndpoint, BoundaryPlanFixture.Tenant,
            BoundaryPlanFixture.Subscription, machine.Boundary, http, BoundaryPlanFixture.Principal);
        var before = arm.Product.Writes.Count;

        if (excess == 0)
        {
            var saved = await store.WriteAsync(key, value, null, CancellationToken.None);
            Assert.Equal(value, saved.Value);
            Assert.Equal(before + 1, arm.Product.Writes.Count);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ReservationStoreException>(() => store.WriteAsync(key, value, null, CancellationToken.None));
            Assert.Equal(FactoryReservationState.Failed, error.State);
            Assert.Equal(before, arm.Product.Writes.Count);
        }
    }

    [Fact]
    public async Task Full_lifecycle_at_admission_edge_fits_later_maximum_proof_receipts_and_historical_references()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var baseline = await machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);
        var config = JsonSerializer.Deserialize<FactoryTelemetryConfiguration>(
            baseline.Result["telemetry"]!["configuration"]!.ToJsonString(), FactoryGitHubJson.Options)!;
        var padding = 8643 - Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(config, FactoryGitHubJson.Options));
        var oversized = await machine.InvokeAsync("telemetry", "--runtime-image", Image(padding), "--auth-audience", FactoryTelemetryWorkflowTests.Audience);
        var maximum = int.Parse(Regex.Match((string)oversized.Result["detail"]!, @"requires (\d+) bytes").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        padding -= maximum - 10240;
        var edge = await machine.InvokeAsync("telemetry", "--runtime-image", Image(padding), "--auth-audience", FactoryTelemetryWorkflowTests.Audience);
        var overEdge = await machine.InvokeAsync("telemetry", "--runtime-image", Image(padding + 1), "--auth-audience", FactoryTelemetryWorkflowTests.Audience);
        Assert.Equal("Partial", (string?)edge.Result["state"]);
        Assert.Equal("Failed", (string?)overEdge.Result["state"]);
        Assert.Contains("requires 10241 bytes", overEdge.Terminal.Output);
        var failure = false;
        var fqdn = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 39)) + ".azurecontainerapps.io";
        Assert.Equal(253, fqdn.Length);
        using var transport = new TelemetryWorkflowTransport(arm)
        {
            Fqdn = fqdn, LongProviderOperationIds = true,
            ChangeProof = proof =>
            {
                proof["detail"] = new string('x', 1024);
                proof["records"]![0]!["itemId"] = new string('\uffff', 128);
                proof["records"]![0]!["observedRecords"] = 499999;
                proof["queries"]![0]!["records"] = 499999;
                proof["queries"]![0]!["latestRecord"] = proof["records"]![0]!.DeepClone();
                if (failure) { proof["state"] = "Blocked"; proof["reason"] = "NetworkUnavailable"; }
            },
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var first = await machine.InvokeAsync("telemetry", "--runtime-image", Image(padding), "--auth-audience",
            FactoryTelemetryWorkflowTests.Audience, "--approve", (string)edge.Result["telemetry"]!["approvalFingerprint"]!);
        Assert.True(first.Exit == 0, first.Terminal.Output);
        failure = true;
        var second = await machine.InvokeAsync("telemetry", "--verify-only");
        var status = await machine.InvokeAsync("status");
        Assert.Equal("Blocked", (string?)second.Result["telemetry"]!["state"]);
        Assert.Equal(first.Result["telemetry"]!["verifiedProofReference"]!.ToJsonString(),
            status.Result["telemetry"]!["verifiedProofReference"]!.ToJsonString());
        Assert.NotEqual(status.Result["telemetry"]!["lastCheckReference"]!.ToJsonString(),
            status.Result["telemetry"]!["verifiedProofReference"]!.ToJsonString());
        Assert.Equal(2, transport.Proofs);
        Assert.All(arm.Product.Writes, w => Assert.True(TotalBytes(w.Key, w.Value) <= 10240, w.Key));

        static string Image(int padding) => FactoryTelemetryWorkflowTests.Image.Replace("@sha256:", new string('x', padding) + "@sha256:", StringComparison.Ordinal);
    }

    private static int TotalBytes(string key, string value) => Encoding.UTF8.GetByteCount(key) + 1
        + Encoding.UTF8.GetByteCount(value) + Encoding.UTF8.GetByteCount("application/vnd.dsf.reservation+json;version=1") + 1024;

    [Theory]
    [InlineData("oversized")]
    [InlineData("newline")]
    public async Task Provider_endpoint_outside_its_bounded_ASCII_shape_is_not_persisted(string damage)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);
        using var transport = new TelemetryWorkflowTransport(arm)
        {
            Fqdn = damage == "oversized"
                ? string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 40)) + ".azurecontainerapps.io"
                : "telemetry.private.azurecontainerapps.io\n",
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await machine.InvokeAsync("telemetry", [.. FactoryTelemetryWorkflowTests.Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal(0, transport.Proofs);
        Assert.DoesNotContain(arm.Product.Writes, w => w.Key == "dsf/factory/telemetry"
            && JsonNode.Parse(w.Value)!["hostEndpoint"] is not null);
    }

    [Theory]
    [InlineData("detail", "Ready")]
    [InlineData("detail", "Blocked")]
    [InlineData("escaped-detail", "Ready")]
    [InlineData("escaped-detail", "Blocked")]
    [InlineData("null-detail", "Ready")]
    [InlineData("null-detail", "Blocked")]
    [InlineData("request-id", "Ready")]
    [InlineData("request-id", "Blocked")]
    [InlineData("record-id", "Ready")]
    [InlineData("latest-record-id", "Blocked")]
    [InlineData("duplicate-record", "Blocked")]
    [InlineData("duplicate-query", "Blocked")]
    public async Task Proof_growth_outside_reviewed_bounds_is_rejected_without_persisting_unbounded_payload(string damage, string state)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);
        using var transport = new TelemetryWorkflowTransport(arm)
        {
            ChangeProof = proof =>
            {
                proof["state"] = state;
                if (damage == "detail") proof["detail"] = new string('x', 1025);
                if (damage == "escaped-detail") proof["detail"] = new string('\uffff', 171);
                if (damage == "null-detail") proof["detail"] = null;
                if (damage == "request-id") proof["queries"]![0]!["providerRequestId"] = new string('x', 1000);
                if (damage == "record-id") proof["records"]![0]!["itemId"] = new string('x', 129);
                if (damage == "latest-record-id") proof["queries"]![0]!["latestRecord"]!["itemId"] = new string('x', 129);
                if (damage == "duplicate-record") proof["records"]!.AsArray().Add(proof["records"]![0]!.DeepClone());
                if (damage == "duplicate-query") proof["queries"]!.AsArray().Add(proof["queries"]![0]!.DeepClone());
            },
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await machine.InvokeAsync("telemetry", [.. FactoryTelemetryWorkflowTests.Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.DoesNotContain(arm.Product.Writes, w => w.Key.StartsWith("dsf/factory/telemetry-proofs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_backends_with_five_tables_are_rejected_before_create_once_intent_or_effects()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync(configure: boundary =>
        {
            string[] tables = ["AppRequests", "AppDependencies", "AppExceptions", "AppTraces", "AppEvents"];
            var workspace = BoundaryPlanFixture.Logs.ToLowerInvariant() + "-second";
            var component = FactoryTelemetryWorkflowTests.Component + "-second";
            var native = BoundaryPlanFixture.Resource(workspace, "Microsoft.OperationalInsights/workspaces");
            native["properties"]!["customerId"] = "55555555-5555-5555-5555-555555555555";
            boundary.Resources[workspace] = native;
            var app = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
            app["properties"]!["WorkspaceResourceId"] = workspace;
            boundary.Resources[component] = app;
            boundary.Input["applicationResourceIds"]!.AsArray().Add(component);
            boundary.Input["evidenceBackends"]![0]!["tables"] = JsonSerializer.SerializeToNode(tables);
            boundary.Input["evidenceBackends"]!.AsArray().Add(new JsonObject
            { ["resourceId"] = workspace, ["tables"] = JsonSerializer.SerializeToNode(tables) });
            foreach (var id in new[] { BoundaryPlanFixture.Logs.ToLowerInvariant(), workspace })
                boundary.Overrides["https://management.azure.com" + id + "/tables?api-version=2022-10-01"] =
                    new(0, JsonSerializer.Serialize(new { value = tables.Select(name => new { name, properties = new { plan = "Analytics" } }) }), "");
        });
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var before = arm.Product.Export();

        var result = await machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);

        Assert.Equal("Failed", (string?)result.Result["state"]);
        Assert.Contains("lifecycle", result.Terminal.Output);
        Assert.Contains("telemetry-proofs/", result.Terminal.Output);
        Assert.Equal(before, arm.Product.Export());
        Assert.Empty(transport.Grants);
        Assert.Empty(transport.Deployments);
    }

    [Fact]
    public async Task Configuration_fitting_alone_cannot_strand_an_oversized_initial_authority()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);
        var config = JsonSerializer.Deserialize<FactoryTelemetryConfiguration>(
            preview.Result["telemetry"]!["configuration"]!.ToJsonString(), FactoryGitHubJson.Options)!;
        var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(config, FactoryGitHubJson.Options));
        var image = FactoryTelemetryWorkflowTests.Image.Replace("@sha256:", new string('x', 8643 - size) + "@sha256:", StringComparison.Ordinal);
        var selected = config with { RuntimeImage = image };
        Assert.Equal(8643, Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(selected, FactoryGitHubJson.Options)));
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var before = arm.Product.Export();

        var result = await machine.InvokeAsync("telemetry", "--runtime-image", image,
            "--auth-audience", FactoryTelemetryWorkflowTests.Audience, "--approve", FactoryTelemetryValidation.Hash(selected));

        Assert.Equal("Failed", (string?)result.Result["state"]);
        Assert.Contains("lifecycle", result.Terminal.Output);
        Assert.Equal(before, arm.Product.Export());
        Assert.Empty(transport.Grants);
        Assert.Empty(transport.Deployments);
    }
}
