using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactorySupplementalInspectionTests
{
    public static IEnumerable<object[]> HistoryCases() =>
        from history in new[] { "receipts-and-proofs", "receipts-only", "proofs-only" }
        from action in new[] { "status", "resume", "supplemental", "verify" }
        select new object[] { history, action };

    [Theory]
    [MemberData(nameof(HistoryCases))]
    public async Task Root_and_pin_loss_cannot_erase_selected_failure_on_reconstructed_workers(string history, string action)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm) { ChangeProof = proof =>
        {
            proof["state"] = "Blocked"; proof["reason"] = "MissingSecret"; proof["nativeSourceRevision"] = null;
        } };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preparation = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        Assert.Equal(1, preparation.Exit);
        Assert.Equal("MissingSecret", (string?)preparation.Result["supplemental"]!["checks"]![0]!["reason"]);
        var historyKeys = JsonNode.Parse(arm.Product.ListJson())!.AsArray().Select(r => (string)r!["key"]!)
            .Where(key => key.StartsWith("dsf/factory/supplemental-", StringComparison.Ordinal)).ToArray();
        Assert.Contains(historyKeys, key => key.StartsWith("dsf/factory/supplemental-operations/", StringComparison.Ordinal));
        Assert.Contains(historyKeys, key => key.StartsWith("dsf/factory/supplemental-proofs/", StringComparison.Ordinal));
        foreach (var key in historyKeys)
            if (history == "receipts-only" && key.Contains("/supplemental-proofs/", StringComparison.Ordinal)
                || history == "proofs-only" && key.Contains("/supplemental-operations/", StringComparison.Ordinal))
                arm.Product.Remove(key);
        arm.Product.Remove(ProductConfigurationKeys.FactorySupplemental);
        arm.Product.Change(ProductConfigurationKeys.FactoryManifest, value =>
        {
            var manifest = JsonNode.Parse(value)!;
            manifest.AsObject().Remove("supplementalOperationId");
            manifest.AsObject().Remove("supplementalApprovalFingerprint");
            return manifest.ToJsonString();
        });
        var productBefore = arm.Product.Export();
        var ownerBefore = machine.Store.Export();
        arm.Product = ReservationTestStore.Restore(productBefore);
        var worker = new ReservationMachine(ReservationTestStore.Restore(ownerBefore));
        worker.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await InspectAsync(worker, action);

        AssertUnknown(result);
        Assert.Equal(productBefore, arm.Product.Export());
        Assert.Equal(ownerBefore, worker.Store.Export());
        Assert.Empty(arm.Product.Writes);
        Assert.Empty(worker.Store.Writes);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(1, transport.Proofs);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("resume")]
    [InlineData("supplemental")]
    public async Task Truly_pristine_azure_only_inspection_remains_readonly_without_optional_service_calls(string action)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        var before = arm.Product.Export();
        arm.Product = ReservationTestStore.Restore(before);
        var worker = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        worker.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await InspectAsync(worker, action);

        Assert.Equal(0, result.Exit);
        Assert.Equal("Reserved", (string?)result.Result["state"]);
        if (action == "supplemental") Assert.Equal("NotSelected", (string?)result.Result["supplemental"]!["state"]);
        else Assert.Null(result.Result["supplemental"]);
        Assert.Equal(before, arm.Product.Export());
        Assert.Empty(arm.Product.Writes);
        Assert.Empty(worker.Store.Writes);
        Assert.Empty(transport.Writes);
        Assert.Empty(transport.Grants);
        Assert.Empty(transport.Resources);
        Assert.Equal(0, transport.Proofs);
    }

    public static IEnumerable<object[]> MetadataCases() =>
        from failure in new[] { "denied", "malformed", "partial", "transport" }
        from action in new[] { "status", "resume", "supplemental", "verify" }
        select new object[] { failure, action };

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public async Task Unconfirmed_namespace_metadata_is_unknown_without_error_disclosure_or_writes(string failure, string action)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        using var metadata = new MetadataFailure(transport, failure);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(metadata);
        var productBefore = arm.Product.Export();
        var ownerBefore = machine.Store.Export();
        var writes = arm.Product.Writes.Count;

        var result = await InspectAsync(machine, action);

        AssertUnknown(result);
        Assert.Equal(1, metadata.Requests);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + result.Terminal.Error);
        Assert.Equal(productBefore, arm.Product.Export());
        Assert.Equal(ownerBefore, machine.Store.Export());
        Assert.Equal(writes, arm.Product.Writes.Count);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.Proofs);
    }

    private static Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> InspectAsync(ReservationMachine worker, string action) =>
        action == "verify" ? worker.InvokeAsync("supplemental", "--verify-only") : worker.InvokeAsync(action);

    private static void AssertUnknown((int Exit, JsonNode Result, ScriptedTerminal Terminal) result)
    {
        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Null(result.Result["supplemental"]);
        Assert.DoesNotContain("NotSelected", result.Terminal.Output);
        Assert.Empty(result.Terminal.Error);
    }

    private sealed class MetadataFailure(HttpMessageHandler next, string failure) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker prior = new(next, false);
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/kv"
                || QueryHelpers.ParseQuery(request.RequestUri.Query)["key"] != ProductConfigurationKeys.FactorySupplemental + "*")
                return prior.SendAsync(request, token);
            Requests++;
            if (failure == "transport") throw new HttpRequestException("secret-provider-detail");
            var response = new HttpResponseMessage(failure == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent(failure == "malformed"
                    ? """{"items":null,"error":"secret-provider-detail"}""" : """{"items":[],"error":"secret-provider-detail"}""",
                    Encoding.UTF8, "application/json"),
            };
            if (failure == "partial") response.Headers.Add("Link", "<https://invalid.example/secret-provider-detail>; rel=\"next\"");
            return Task.FromResult(response);
        }
    }
}
