using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewEvaluationContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_checkpoint_with_lost_response_never_replays_saved_native_judgments(bool canceled)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var pins = PreviewEvaluationTests.Pins(transport);
        var lost = false;
        transport.AfterEvaluationWrite = document =>
        {
            if (lost || document["dispatches"] is not JsonArray dispatches
                || !dispatches.Any(d => (string?)d?["request"]?["route"] == "juror-2" && (string?)d?["state"] == "Confirmed")) return;
            lost = true;
            if (canceled) throw new OperationCanceledException("private-checkpoint-acknowledgment");
            throw new HttpRequestException("private-checkpoint-acknowledgment");
        };
        var start = transport.Requests.Count;
        var first = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.True(lost);
        Assert.False((bool)first.Result["currentReady"]!);
        Assert.DoesNotContain("private-checkpoint", first.Terminal.Output);
        var calls = transport.EvaluationInference.Count;
        transport.AfterEvaluationWrite = null;
        transport.Restart();
        var resumed = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
        Assert.True(resumed.Exit == 0, resumed.Terminal.Output);
        Assert.Equal("ReadyAwaitingActivation", (string?)resumed.Result["readiness"]);
        Assert.Single(resumed.Result["evaluation"]!["proof"]!["candidates"]!.AsArray());
        Assert.Equal(1, transport.EvaluationInference.Count(n => n == "juror-2"));
        Assert.InRange(transport.EvaluationInference.Count - calls, 0, 1);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("grant")]
    public async Task Authority_changed_during_real_evaluation_withholds_final_readiness(string changed)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var pins = PreviewEvaluationTests.Pins(transport);
        var altered = false;
        transport.AlterCompletion = (prompt, text) =>
        {
            if (!altered && prompt.Contains("As juror 'juror-3'", StringComparison.Ordinal))
            {
                altered = true;
                if (changed == "intent") transport.Scenario.Arm.Product.Remove(ProductConfigurationKeys.CharterAdopted);
                else transport.Resources[transport.NativeWrites[^1]]["properties"]!["condition"] = "unreviewed-condition";
            }
            return text;
        };
        var start = transport.Requests.Count;
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.True(altered);
        Assert.False((bool)result.Result["currentReady"]!);
        Assert.Null(result.Result["evaluation"]?["proof"]);
        var calls = transport.EvaluationInference.Count;
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        transport.Restart();
        var inspected = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.False((bool)inspected.Result["currentReady"]!);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(native, transport.SeedDocuments);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }
}
