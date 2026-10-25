using Xunit;
using System.Net;
using System.Text.Json.Nodes;

namespace Dsf.Cli.Tests;

public sealed class PreviewIngressTests
{
    [Theory]
    [InlineData("GO,GO,GO", "Proceed")]
    [InlineData("GO,GO,NO-GO", "Escalate")]
    [InlineData("NO-GO,NO-GO,NO-GO", "Kill")]
    public async Task Full_native_evaluation_outlives_ingress_without_canceling_or_repeating_judgments(string votes, string disposition)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        transport.IngressLimit = TimeSpan.FromSeconds(240);
        transport.EvaluationModelDelay = TimeSpan.FromSeconds(20);
        var jury = votes.Split(',');
        transport.AlterCompletion = (prompt, text) =>
        {
            for (var i = 0; i < 3; i++)
                if (prompt.Contains($"As juror 'juror-{i + 1}'", StringComparison.Ordinal))
                    return jury[i] + ": Independently evaluated the actual scoped request and bounded proposal.";
            return text;
        };
        var started = transport.Scenario.Source.Source!.Clock.Now;
        var pins = PreviewEvaluationTests.Pins(transport);
        var effects = transport.Requests.Count;
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.True(result.Exit == 0, $"Exit {result.Exit}; elapsed {transport.Scenario.Source.Source.Clock.Now - started}; calls {transport.EvaluationInference.Count}\n"
            + result.Terminal.Error + "\n" + result.Terminal.Output + "\n" + result.Result.ToJsonString() + "\n" + string.Join("\n", transport.HostFailures));
        Assert.Equal("ReadyAwaitingActivation", (string?)result.Result["readiness"]);
        Assert.Equal(16, transport.EvaluationInference.Count);
        Assert.Equal(disposition, (string?)result.Result["evaluation"]!["proof"]!["candidates"]![0]!["disposition"]);
        Assert.True(transport.Scenario.Source.Source.Clock.Now - started >= TimeSpan.FromSeconds(320));
        Assert.All(transport.IngressResponses, response => Assert.True(response.Elapsed <= TimeSpan.FromSeconds(180)
            && response.WallElapsed < TimeSpan.FromSeconds(180), response.ToString()));
        var retained = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        transport.Restart();
        var status = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.Equal("ReadyAwaitingActivation", (string?)status.Result["readiness"]);
        var resumed = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
        Assert.Equal("ReadyAwaitingActivation", (string?)resumed.Result["readiness"]);
        Assert.Equal(retained, transport.SeedDocuments);
        Assert.Equal(16, transport.EvaluationInference.Count);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_or_canceled_admission_response_does_not_cancel_accepted_work_or_duplicate_judgments(bool canceled)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeEvaluationResponse = async (_, token) =>
        {
            if (entered.TrySetResult()) await release.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
        };
        var lost = false;
        transport.AfterIngressResponse = (path, status) =>
        {
            if (path == "/evaluate" && status == HttpStatusCode.Accepted && !lost)
            {
                lost = true;
                if (canceled) throw new OperationCanceledException();
                throw new HttpRequestException("Lost accepted ingress response.");
            }
            return Task.CompletedTask;
        };
        var pins = PreviewEvaluationTests.Pins(transport);
        var start = transport.Requests.Count;
        try
        {
            var admitted = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
            Assert.Equal("Unknown", (string?)admitted.Result["readiness"]);
            Assert.True(lost, admitted.Result.ToJsonString() + "\n" + string.Join("\n", transport.HostFailures));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(180));
            var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
            var status = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
            Assert.Equal("Running", (string?)status.Result["readiness"]);
            Assert.Equal(native, transport.SeedDocuments);
            Assert.Single(transport.EvaluationInference);
            var reconnect = transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
            release.TrySetResult();
            var result = await reconnect;
            Assert.Equal("ReadyAwaitingActivation", (string?)result.Result["readiness"]);
            Assert.Equal(16, transport.EvaluationInference.Count);
            PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task Lost_native_admission_or_worker_receipts_quarantine_the_original_operation(string phase)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var interrupted = false;
        transport.AfterEvaluationWrite = body =>
        {
            if (!interrupted && (string?)body["admission"]?["state"] == phase)
            {
                interrupted = true;
                throw new HttpRequestException("Native conditional write response lost.");
            }
        };
        var pins = PreviewEvaluationTests.Pins(transport);
        var failed = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.Equal("Unknown", (string?)failed.Result["readiness"]);
        Assert.Empty(transport.EvaluationInference);
        transport.Restart();
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var status = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.Equal("Unknown", (string?)status.Result["readiness"]);
        var resume = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
        Assert.Equal("Unknown", (string?)resume.Result["readiness"]);
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Empty(transport.EvaluationInference);
        Assert.NotNull(JsonNode.Parse(Assert.Single(native).Value)!["admission"]);
    }

    [Theory]
    [InlineData(179, true)]
    [InlineData(181, false)]
    public async Task Individual_native_model_hop_retains_its_budget_below_ingress(int seconds, bool complete)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        transport.IngressLimit = TimeSpan.FromSeconds(240);
        transport.BeforeEvaluationResponse = async (route, token) =>
        {
            if (route != "embedding") return;
            transport.Scenario.Source.Source!.Clock.Now += TimeSpan.FromSeconds(seconds);
            await Task.Delay(10, token);
        };
        var pins = PreviewEvaluationTests.Pins(transport);
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.Equal(complete ? "ReadyAwaitingActivation" : "Unknown", (string?)result.Result["readiness"]);
        Assert.Equal(complete ? 16 : 1, transport.EvaluationInference.Count);
        if (!complete)
        {
            transport.Restart();
            var resumed = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
            Assert.Equal("Unknown", (string?)resumed.Result["readiness"]);
            Assert.Single(transport.EvaluationInference);
        }
    }

    [Fact]
    public async Task Cancellation_before_http_admission_does_not_create_an_execution_intent()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        transport.BeforeIngressRequest = uri => uri.AbsolutePath == "/evaluate"
            ? throw new OperationCanceledException() : Task.CompletedTask;
        var pins = PreviewEvaluationTests.Pins(transport);
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var canceled = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.Equal("Unknown", (string?)canceled.Result["readiness"]);
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Empty(transport.EvaluationInference);
        transport.BeforeIngressRequest = null;
        transport.Restart();
        var status = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.Equal("NotReady", (string?)status.Result["readiness"]);
        Assert.Equal(native, transport.SeedDocuments);
    }

    [Fact]
    public async Task Host_shutdown_during_model_dispatch_is_not_a_lease_or_permission_to_take_over()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeEvaluationResponse = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var pins = PreviewEvaluationTests.Pins(transport);
        var evaluation = transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(180));
        transport.Restart();
        var interrupted = await evaluation;
        Assert.False((bool)interrupted.Result["currentReady"]!);
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var resumed = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
        Assert.Equal("Unknown", (string?)resumed.Result["readiness"]);
        Assert.Single(transport.EvaluationInference);
        Assert.Equal(native, transport.SeedDocuments);
    }

    [Theory]
    [InlineData(true, "foundryiq")]
    [InlineData(false, "webiq")]
    public async Task Prepared_optional_source_survives_the_same_ingress_safe_native_evaluation(bool foundry, string kind)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync(web: !foundry, foundry: foundry);
        transport.IngressLimit = TimeSpan.FromSeconds(240);
        transport.EvaluationModelDelay = TimeSpan.FromSeconds(20);
        var pins = PreviewEvaluationTests.Pins(transport);
        var start = transport.Requests.Count;
        var evaluated = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.True(evaluated.Exit == 0, (string?)evaluated.Result["reason"] + "\n" + string.Join("\n", transport.HostFailures));
        Assert.Equal(kind, (string?)Assert.Single(evaluated.Result["evaluation"]!["inputs"]!["supplemental"]!["sources"]!.AsArray())!["evidence"]!["proof"]!["kind"]);
        Assert.Equal(16, transport.EvaluationInference.Count);
        transport.Restart();
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var inspected = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.Equal("ReadyAwaitingActivation", (string?)inspected.Result["readiness"]);
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Equal(16, transport.EvaluationInference.Count);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }

    [Fact]
    public async Task An_ingress_readiness_claim_without_native_evaluation_is_never_a_green_cli_result()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        transport.AlterIngressBody = text =>
        {
            var body = JsonNode.Parse(text)!;
            if ((int?)body["protocol"] != 2) return text;
            body["readiness"] = "ReadyAwaitingActivation";
            body["evaluation"] = null;
            return body.ToJsonString();
        };
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.Equal("Unknown", (string?)result.Result["readiness"]);
        Assert.NotEqual(0, result.Exit);
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Empty(transport.EvaluationInference);
    }
}
