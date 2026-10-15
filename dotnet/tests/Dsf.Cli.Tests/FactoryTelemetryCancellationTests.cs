using System.Text.Json.Nodes;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryTelemetryCancellationTests
{
    [Theory]
    [InlineData("pre-command", "Ready", "Canceled")]
    [InlineData("pre-check", "Ready", "Canceled")]
    [InlineData("initial-root-read", "Ready", "Canceled")]
    [InlineData("initial-profile-read", "Ready", "Canceled")]
    [InlineData("hold-write-before", "Ready", "Unknown")]
    [InlineData("hold-write-after", "Partial", "Unknown")]
    [InlineData("confirmed-hold", "Partial", "Canceled")]
    [InlineData("changed-after-confirmation", "Ready", "Canceled")]
    public async Task Cancellation_reports_observed_hold_provenance_not_an_assumed_current_hold(string phase, string storedState, string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync(cancellation.Token);
        var preview = await machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("telemetry", [.. FactoryTelemetryWorkflowTests.Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!])).Exit);
        const string rootKey = "dsf/factory/telemetry";
        var previous = ReadRoot(arm.Product).ToJsonString();
        var writes = arm.Product.Writes.Count;
        var inspected = false;
        var holdWritten = false;
        var calls = 0;
        var callsAfterInspection = 0;
        machine.Boundary.Repository.ReservationClock = new InspectionClock(() =>
        {
            inspected = true;
            if (phase == "pre-check") cancellation.Cancel();
        });
        arm.Product.AfterPut = (key, value) =>
        {
            if (key != rootKey || (string?)JsonNode.Parse(value)!["stage"] != "checking") return;
            holdWritten = true;
            if (phase == "hold-write-after") cancellation.Cancel();
        };
        transport.OnRequest = request =>
        {
            calls++;
            if (!inspected) return;
            callsAfterInspection++;
            var path = request.RequestUri!.AbsolutePath;
            var key = path.StartsWith("/kv/", StringComparison.Ordinal) ? Uri.UnescapeDataString(path[4..]) : "";
            var cancel = phase == "initial-root-read" && request.Method == HttpMethod.Get && key == rootKey
                || phase == "initial-profile-read" && request.Method == HttpMethod.Get && key == "dsf/factory/profile"
                || phase == "hold-write-before" && request.Method == HttpMethod.Put && key == rootKey
                || phase is "confirmed-hold" or "changed-after-confirmation" && holdWritten && request.RequestUri.Host == "management.azure.com";
            if (!cancel) return;
            if (phase == "changed-after-confirmation") arm.Product.Change(rootKey, _ => previous);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        if (phase == "pre-command") cancellation.Cancel();

        var result = await machine.InvokeAsync("telemetry", "--verify-only");

        Assert.Equal(outcome == "Canceled" ? 130 : 1, result.Exit);
        if (phase == "pre-command")
        {
            Assert.Empty(result.Terminal.Output);
            Assert.Equal(0, calls);
            Assert.Equal(writes, arm.Product.Writes.Count);
            Assert.Equal("Ready", (string?)ReadRoot(arm.Product)["state"]);
            return;
        }
        Assert.Equal(outcome, (string?)result.Result["state"]);
        if (result.Result["telemetry"] is { } telemetry) Assert.Equal(outcome, (string?)telemetry["state"]);
        Assert.Equal(storedState, (string?)ReadRoot(arm.Product)["state"]);
        Assert.Equal(1, transport.Proofs);
        Assert.DoesNotContain("confirmed readiness hold remains", result.Terminal.Output);
        if (phase is "confirmed-hold" or "changed-after-confirmation")
        {
            Assert.Contains("hold was confirmed", result.Terminal.Output);
            Assert.Contains("current authority may have changed", result.Terminal.Output);
        }
        else if (phase.StartsWith("hold-write", StringComparison.Ordinal))
            Assert.Contains("Hold not confirmed", result.Terminal.Output);
        else
        {
            Assert.Contains("No readiness hold confirmed", result.Terminal.Output);
            Assert.Contains("no revocation asserted", result.Terminal.Output);
            Assert.Equal(writes, arm.Product.Writes.Count);
        }
        if (phase == "pre-check") Assert.Equal(0, callsAfterInspection);
    }

    private static JsonNode ReadRoot(ReservationTestStore store) => JsonNode.Parse((string)JsonNode.Parse(store.ListJson())!.AsArray()
        .Single(r => (string?)r!["key"] == "dsf/factory/telemetry")!["value"]!)!;

    private sealed class InspectionClock(Action onInspection) : TimeProvider
    {
        private bool inspected;
        public override DateTimeOffset GetUtcNow()
        {
            if (!inspected) { inspected = true; onInspection(); }
            return new ReservationClock().GetUtcNow();
        }
    }
}
