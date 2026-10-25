using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactorySupplementalSafetyTests
{
    [Theory]
    [InlineData(false, "--input")]
    [InlineData(true, "--input")]
    [InlineData(false, "--approve")]
    [InlineData(true, "--approve")]
    public async Task Explicit_blank_options_never_mean_azure_only_or_ignore_approved_selection(bool selected, string option)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        if (selected) Assert.Equal(0, (await FactorySupplementalWorkflowTests.PrepareWebAsync(machine)).Exit);
        var before = arm.Product.Export();
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, FactorySupplementalWorkflowTests.Selection().ToJsonString());
            var result = await machine.InvokeAsync("supplemental",
                option == "--input" ? ["--input", " "] : ["--input", file, "--approve", " "]);
            Assert.Equal(1, result.Exit);
            Assert.Equal("Failed", (string?)result.Result["state"]);
            Assert.Equal(before, arm.Product.Export());
            Assert.Equal(selected ? 1 : 0, transport.Proofs);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("final-grant")]
    public async Task Final_readiness_rechecks_durable_receipts_and_native_grants_after_reading_proofs(string scenario)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        transport.OnProof = () =>
        {
            if (scenario == "receipt")
            {
                var operation = (string)transport.Record(ProductConfigurationKeys.FactorySupplemental)["operationId"]!;
                arm.Product.Change($"dsf/factory/supplemental-operations/{operation}/0000", value =>
                {
                    var receipt = JsonNode.Parse(value)!;
                    receipt["providerRequestId"] = Guid.NewGuid().ToString();
                    return receipt.ToJsonString();
                });
            }
        };
        arm.Product.ModifyResponse = (method, record) =>
        {
            if (scenario == "final-grant" && method == HttpMethod.Get
                && ((string?)record["key"])?.StartsWith("dsf/factory/supplemental-proofs/", StringComparison.Ordinal) == true)
                transport.Resources.Remove((string)transport.Grants[0]["id"]!);
        };
        var result = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        Assert.Equal(1, result.Exit);
        Assert.NotEqual("ReadySupplemental", (string?)result.Result["supplemental"]!["state"]);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(1, transport.Proofs);
    }

    [Fact]
    public async Task One_failing_source_cannot_disappear_behind_plentiful_other_supplemental_evidence()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm) { ChangeProof = proof =>
        {
            if ((string?)proof["kind"] == "webiq")
            {
                proof["state"] = "Blocked"; proof["reason"] = "MissingSecret"; proof["nativeSourceRevision"] = null;
            }
            else
            {
                proof["reason"] = "None";
                proof["records"] = new JsonArray(new JsonObject { ["reference"] = "foundryiq:approved-source/document",
                    ["contentSha256"] = new string('a', 64) });
            }
        } };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var selection = FactorySupplementalWorkflowTests.Selection();
        selection["sources"]!.AsArray().Add(FactorySupplementalWorkflowTests.FoundrySelection());
        var result = await FactorySupplementalWorkflowTests.PrepareSelectionAsync(machine, selection);
        Assert.Equal(1, result.Exit);
        var checks = result.Result["supplemental"]!["checks"]!.AsArray();
        Assert.Equal(2, checks.Count);
        Assert.Equal("MissingSecret", (string?)checks[0]!["reason"]);
        Assert.Equal("ReadySupplemental", (string?)checks[1]!["state"]);
        Assert.Equal(2, transport.Proofs);
        Assert.Null(result.Result["telemetry"]);
        Assert.False((bool)result.Result["isActivated"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_host_proof_is_persisted_as_malformed_or_capacity_failure_not_empty_or_network_success(bool oversized)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm) { ChangeProof = proof =>
        {
            if (oversized) proof["coverage"] = new string('x', 8193);
            else proof["records"] = new JsonArray((JsonNode?)null);
        } };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        Assert.Equal(1, result.Exit);
        Assert.Equal(oversized ? "CapacityExceeded" : "MalformedResponse",
            (string?)result.Result["supplemental"]!["checks"]![0]!["reason"]);
        var status = await machine.InvokeAsync("supplemental");
        Assert.Equal("Blocked", (string?)status.Result["supplemental"]!["state"]);
    }

    [Fact]
    public async Task Small_input_that_exceeds_eventual_record_capacity_is_rejected_before_authority_or_native_effects()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var input = FactorySupplementalWorkflowTests.Selection();
        input["sources"]![0]!["query"] = new string('<', 1000);
        var contents = input.ToJsonString();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(contents) <= 8192);
        var before = arm.Product.Export();
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, contents);
            var result = await machine.InvokeAsync("supplemental", "--input", file);
            Assert.Equal(1, result.Exit);
            Assert.Null(result.Result["supplemental"]);
            Assert.Contains("10 KB", result.Terminal.Output);
            Assert.Equal(before, arm.Product.Export());
            Assert.Empty(transport.Writes);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("null-source")]
    [InlineData("unapproved-query")]
    [InlineData("empty-selection")]
    [InlineData("oversized")]
    [InlineData("metadata-data-role")]
    [InlineData("metadata-secret-role")]
    public async Task Invalid_or_oversized_opt_in_is_explicit_failed_input_without_effects(string scenario)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var input = FactorySupplementalWorkflowTests.Selection();
        if (scenario == "null-source") input["sources"] = new JsonArray((JsonNode?)null);
        if (scenario == "empty-selection") input["sources"] = new JsonArray();
        if (scenario == "unapproved-query") input["sources"]![0]!["publicQueryApproved"] = false;
        if (scenario == "oversized") input["sources"]![0]!["query"] = new string('x', 8193);
        if (scenario.StartsWith("metadata-", StringComparison.Ordinal))
        {
            var source = FactorySupplementalWorkflowTests.FoundrySelection();
            source["metadataRoleDefinitionId"] = BoundaryPlanFixture.Root.ToLowerInvariant() + "/providers/microsoft.authorization/roledefinitions/"
                + (scenario == "metadata-data-role" ? "1407120a-92aa-4202-b7e9-c0e197c71c8f" : "4633458b-17de-408a-b874-0445c86b69e6");
            input["sources"] = new JsonArray(source);
        }
        var before = arm.Product.Export();
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, input.ToJsonString());
            var result = await machine.InvokeAsync("supplemental", "--input", file);
            Assert.Equal(1, result.Exit);
            Assert.Equal("Failed", (string?)result.Result["state"]);
            Assert.Equal(before, arm.Product.Export());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Cancellation_before_hold_never_returns_success_or_asserts_previous_readiness_revoked()
    {
        using var canceled = new CancellationTokenSource();
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync(canceled.Token);
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await FactorySupplementalWorkflowTests.PrepareWebAsync(machine)).Exit);
        var before = arm.Product.Export();
        var reads = 0;
        arm.Product.ModifyResponse = (method, record) =>
        {
            if (method == HttpMethod.Get && (string?)record["key"] == ProductConfigurationKeys.FactorySupplemental
                && ++reads == 2) canceled.Cancel();
        };
        var result = await machine.InvokeAsync("supplemental", "--verify-only");
        Assert.Equal(130, result.Exit);
        Assert.Equal("Canceled", (string?)result.Result["state"]);
        Assert.Equal(before, arm.Product.Export());
        Assert.DoesNotContain("revoked authority", result.Terminal.Output);
        Assert.Equal(1, transport.Proofs);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("role")]
    [InlineData("inherited")]
    [InlineData("host")]
    [InlineData("governance")]
    public async Task Revocation_or_native_drift_during_observation_prevents_readiness_promotion(string scenario)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        transport.OnProof = () =>
        {
            if (scenario == "grant") transport.Resources.Remove((string)transport.Grants[0]["id"]!);
            if (scenario == "role") transport.BroaderRole = true;
            if (scenario == "inherited") transport.ExternalAssignment = new JsonObject { ["id"] = "/foreign/assignment",
                ["properties"] = new JsonObject { ["principalType"] = "Group" } };
            if (scenario == "host") transport.Resources.Single(p => p.Key.Contains("/containerapps/", StringComparison.Ordinal)
                && !p.Key.EndsWith("/current", StringComparison.Ordinal)).Value["properties"]!["template"]!["containers"]![0]!["image"] = "foreign";
            if (scenario == "governance") arm.Product.Remove(ProductConfigurationKeys.FactoryProfile);
        };
        var result = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        Assert.Equal(1, result.Exit);
        Assert.NotEqual("ReadySupplemental", (string?)result.Result["supplemental"]!["state"]);
        Assert.Empty(transport.Record(ProductConfigurationKeys.FactorySupplemental)["checks"]!.AsArray());
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(1, transport.Proofs);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("stage")]
    [InlineData("root")]
    [InlineData("pin")]
    [InlineData("root-and-pin")]
    public async Task Approved_source_cannot_disappear_or_be_changed_into_azure_only(string scenario)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await FactorySupplementalWorkflowTests.PrepareWebAsync(machine)).Exit);
        if (scenario == "root" || scenario == "root-and-pin") arm.Product.Remove(ProductConfigurationKeys.FactorySupplemental);
        if (scenario == "pin" || scenario == "root-and-pin")
            arm.Product.Change(ProductConfigurationKeys.FactoryManifest, value =>
            {
                var manifest = JsonNode.Parse(value)!;
                manifest.AsObject().Remove("supplementalOperationId");
                manifest.AsObject().Remove("supplementalApprovalFingerprint");
                return manifest.ToJsonString();
            });
        if (scenario == "selection") arm.Product.Change(ProductConfigurationKeys.FactorySupplemental, value =>
        {
            var root = JsonNode.Parse(value)!;
            root["configuration"]!["selection"]!["sources"] = new JsonArray();
            return root.ToJsonString();
        });
        if (scenario == "stage") arm.Product.Change(ProductConfigurationKeys.FactorySupplemental, value =>
        {
            var root = JsonNode.Parse(value)!;
            root["stage"] = "unproven";
            return root.ToJsonString();
        });
        var before = arm.Product.Export();
        var result = await machine.InvokeAsync("supplemental");
        Assert.Equal(1, result.Exit);
        Assert.DoesNotContain("NotSelected", result.Terminal.Output);
        Assert.Equal(before, arm.Product.Export());
        Assert.Equal(3, transport.Writes.Count);
    }

    [Fact]
    public async Task Lost_identity_creation_receipt_survives_serialization_without_recreation()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm) { LoseIdentityResponse = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        arm.Product = ReservationTestStore.Restore(arm.Product.Export());
        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        observer.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var status = await observer.InvokeAsync("supplemental");
        var verify = await observer.InvokeAsync("supplemental", "--verify-only");
        await observer.InvokeAsync("resume");
        Assert.Equal("Unknown", (string?)status.Result["supplemental"]!["state"]);
        Assert.Equal("Unknown", (string?)verify.Result["supplemental"]!["state"]);
        Assert.Single(transport.Writes);
        Assert.Empty(transport.Grants);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + arm.Product.Export());
    }

    [Fact]
    public async Task Independent_serialized_workers_cannot_promote_a_replaced_check_or_claim_a_failed_hold_revoked_authority()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await FactorySupplementalWorkflowTests.PrepareWebAsync(machine)).Exit);
        arm.Product = ReservationTestStore.Restore(arm.Product.Export());
        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        observer.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        transport.BeforeProofAsync = async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
        };
        var first = machine.InvokeAsync("supplemental", "--verify-only");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = await observer.InvokeAsync("supplemental", "--verify-only");
        release.SetResult();
        var stale = await first.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, second.Exit);
        Assert.Equal(1, stale.Exit);
        Assert.Contains("unconfirmed", stale.Terminal.Output);
        var status = await observer.InvokeAsync("supplemental");
        Assert.Equal("ReadySupplemental", (string?)status.Result["supplemental"]!["state"]);
        Assert.Equal((string?)second.Result["supplemental"]!["checkId"], (string?)status.Result["supplemental"]!["checkId"]);
        Assert.Equal(3, transport.Writes.Count);
    }

    [Fact]
    public async Task Failing_selected_source_is_retained_with_its_empty_and_error_distinctions()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm) { ChangeProof = proof =>
        {
            proof["state"] = "Blocked"; proof["reason"] = "MissingSecret"; proof["nativeSourceRevision"] = null;
        } };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        Assert.Equal(1, result.Exit);
        Assert.Equal("MissingSecret", (string?)result.Result["supplemental"]!["checks"]![0]!["reason"]);
        var before = arm.Product.Export();
        var status = await machine.InvokeAsync("supplemental");
        Assert.Equal("webiq", (string?)status.Result["supplemental"]!["configuration"]!["selection"]!["sources"]![0]!["kind"]);
        Assert.Equal(before, arm.Product.Export());
        Assert.Null(status.Result["telemetry"]);
    }
}
