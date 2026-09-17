using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewPreparationFreshnessTests
{
    [Fact]
    public async Task Expiring_telemetry_during_native_preparation_requires_fresh_observation_before_configuration()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var telemetry = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryTelemetry])!;
        var proof = JsonNode.Parse(transport.Values()[(string)telemetry["verifiedProofReference"]!])!;
        transport.Scenario.Source.Source!.Clock.Now = proof["records"]![0]!["recordTime"]!.GetValue<DateTimeOffset>()
            .AddSeconds((int)telemetry["configuration"]!["freshnessSeconds"]! - 60);
        var models = await machine.InvokeAsync("models", "--verify-only");
        Assert.True(models.Exit == 0, models.Terminal.Output);
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(plan.Exit == 0, plan.Terminal.Output);
        var approval = (string)plan.Result["approvalFingerprint"]!;
        transport.AfterNativeWrite = _ =>
        {
            if (transport.NativeWrites.Count == 1) transport.Scenario.Source.Source.Clock.Now += TimeSpan.FromSeconds(121);
        };
        var prepared = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.True(prepared.Exit == 0, prepared.Terminal.Output);
        var writes = transport.NativeWrites.Count;
        Assert.NotEqual(0, (await machine.InvokeAsync("preview", "--configure")).Exit);
        transport.Restart();
        var refreshed = await machine.InvokeAsync("telemetry", "--verify-only");
        Assert.True(refreshed.Exit == 0, refreshed.Terminal.Output);
        await ConfigureAsync(transport);
        Assert.Equal(writes, transport.NativeWrites.Count);
    }

    [Fact]
    public async Task Expired_models_can_refresh_during_released_native_preparation_without_redeploying()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var models = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryModels])!;
        transport.Scenario.Source.Source!.Clock.Now = models["verifiedUntil"]!.GetValue<DateTimeOffset>().AddSeconds(-60);
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(plan.Exit == 0, plan.Terminal.Output);
        var approval = (string)plan.Result["approvalFingerprint"]!;
        transport.AfterNativeWrite = _ =>
        {
            if (transport.NativeWrites.Count == 1)
                transport.Scenario.Source.Source!.Clock.Now += TimeSpan.FromSeconds(121);
        };
        var partial = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.NotEqual(0, partial.Exit);
        Assert.Single(transport.NativeWrites);
        Assert.Equal("Confirmed", (string?)partial.Result["effects"]![0]!["state"]);
        transport.Restart();
        var refresh = await machine.InvokeAsync("models", "--verify-only");
        Assert.True(refresh.Exit == 0, refresh.Terminal.Output);
        var resumed = await machine.InvokeAsync("preview", "--resume", "--approve", approval);
        Assert.True(resumed.Exit == 0, resumed.Terminal.Output);
        Assert.Equal(approval, (string?)resumed.Result["approvalFingerprint"]);
        Assert.Equal("ConfirmedNotReady", (string?)resumed.Result["state"]);
        Assert.Equal(1, transport.NativeWrites.Count(w => w == (string)plan.Result["plan"]!["steps"]![0]!["target"]!));
        await ConfigureAsync(transport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiring_selected_proof_during_native_deployment_can_refresh_and_configure_original_preparation(bool foundry)
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        if (foundry) await transport.SelectFoundryAsync();
        else await transport.SelectWebAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(plan.Exit == 0, plan.Terminal.Output);
        var approval = (string)plan.Result["approvalFingerprint"]!;
        transport.AfterNativeWrite = _ =>
        {
            if (transport.NativeWrites.Count == 1)
                transport.Scenario.Source.Source!.Clock.Now += TimeSpan.FromSeconds(121);
        };
        var prepared = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.True(prepared.Exit == 0, prepared.Terminal.Output);
        Assert.Equal("ConfirmedNotReady", (string?)prepared.Result["state"]);
        Assert.Equal(approval, (string?)prepared.Result["approvalFingerprint"]);
        var receipts = prepared.Result["receipts"]!.DeepClone();
        var writes = transport.NativeWrites.Count;
        var stale = await machine.InvokeAsync("preview", "--configure");
        Assert.NotEqual(0, stale.Exit);
        Assert.False((bool)stale.Result["currentReady"]!);
        Assert.Null(transport.Manifest.PreviewOperationId);
        transport.Restart();
        var refreshed = await machine.InvokeAsync("supplemental", "--verify-only");
        Assert.True(refreshed.Exit == 0, refreshed.Terminal.Output);
        var resumed = await machine.InvokeAsync("preview", "--resume", "--approve", approval);
        Assert.True(resumed.Exit == 0, (string?)resumed.Result["detail"] + "\n" + string.Join("\n", transport.Requests.TakeLast(10))
            + "\n" + string.Join("\n", transport.HostFailures));
        Assert.Equal(approval, (string?)resumed.Result["approvalFingerprint"]);
        Assert.True(JsonNode.DeepEquals(receipts, resumed.Result["receipts"]));
        await ConfigureAsync(transport);
        Assert.Equal(writes, transport.NativeWrites.Count);
    }

    private static async Task ConfigureAsync(PreviewPreparationTransport transport)
    {
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("preview", "--configure");
        Assert.True(review.Exit == 0, review.Terminal.Output);
        var seeded = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(seeded.Exit == 0, seeded.Terminal.Output + string.Join("\n", transport.HostFailures));
        var configured = await machine.InvokeAsync("preview", "--configure", "--approve", (string)seeded.Result["approvalFingerprint"]!);
        Assert.True(configured.Exit == 0, configured.Terminal.Output);
        Assert.Equal("ConfiguredAwaitingEvaluation", (string?)configured.Result["state"]);
        Assert.Single(transport.SeedDocuments);
        Assert.Empty(transport.EvaluationInference);
        Assert.NotNull(transport.Values()[ProductConfigurationKeys.FactoryPreview]);
    }
}
