using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed partial class FactoryEvidenceRefreshTests
{
    [Theory]
    [InlineData("before-inference")]
    [InlineData("INFER_INTENT")]
    [InlineData("CHECK_EVERY_CLAIM")]
    [InlineData("CHECK_UNCHANGED_GOVERNING_INTENT")]
    public async Task Native_ARM_429_is_not_model_POST_rejection_authority_or_permission_to_replay(string stage)
    {
        var transport = fixture.Reset();
        AdvanceDefaultBranch(transport);
        var source = transport.Scenario.Source.Source!;
        var governance = transport.Scenario.Arm.Product.Export();
        var armed = false;
        var failed = false;
        transport.BeforeIngressRequest = uri =>
        {
            if (stage == "before-inference" && uri.AbsolutePath == "/synthesis"
                && source.Progress.TryGetValue(FactorySourceRefresh.CurrentKey, out var stored)
                && JsonSerializer.Deserialize<SourceRefreshProgress>(stored.Value, FactorySourceValidation.RecordJson)!.Work is not null)
                armed = true;
            return Task.CompletedTask;
        };
        transport.BeforeEvaluationResponse = (_, _) =>
        {
            if (transport.SourceInferenceInputs.LastOrDefault()?.Contains(stage, StringComparison.Ordinal) == true)
                armed = true;
            return Task.CompletedTask;
        };
        transport.ArmReadStatus = uri =>
        {
            if (!armed || failed || !uri.AbsolutePath.Contains("/providers/microsoft.cognitiveservices/accounts/",
                StringComparison.OrdinalIgnoreCase)) return null;
            failed = true;
            return HttpStatusCode.TooManyRequests;
        };
        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        try
        {
            var stopped = await RefreshAsync(transport, invocation);
            Assert.True(failed, "The actual model-host native ARM request was not rejected.");
            var calls = transport.EvaluationInference.Count;
            if (stage == "before-inference") Assert.Empty(transport.SourceInferenceInputs);
            else Assert.Contains(stage, transport.SourceInferenceInputs.Last(), StringComparison.Ordinal);
            var original = source.Progress.ToDictionary();
            transport.BeforeIngressRequest = null;
            transport.BeforeEvaluationResponse = null;
            transport.ArmReadStatus = null;
            transport.Restart();
            FactoryActivationTests.ConnectAudit(transport);
            using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
            var next = new FactoryDecisionRequest(Guid.NewGuid());
            using var attempted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", next,
                FactorySourceValidation.RecordJson);
            if (attempted.StatusCode == HttpStatusCode.Accepted) await PollRefreshAsync(transport, next);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(HttpStatusCode.Forbidden, attempted.StatusCode);
            Assert.Equal("Unknown", (string?)stopped["readiness"]);
            Assert.Equal(original, transport.Scenario.Source.Source!.Progress);
            var progress = JsonSerializer.Deserialize<SourceRefreshProgress>(
                source.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
            Assert.Null(progress.Recovery);
            Assert.Null(progress.LastFailure?.ModelRejection);
            Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
            Assert.Equal(0, transport.IssuePosts);
            var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
            Assert.Equal(calls, transport.EvaluationInference.Count);
        }
        finally
        {
            transport.BeforeIngressRequest = null;
            transport.BeforeEvaluationResponse = null;
            transport.ArmReadStatus = null;
        }
    }

    [Theory]
    [InlineData(SourceRefreshPhase.Acquisition)]
    [InlineData(SourceRefreshPhase.Analysis)]
    [InlineData(SourceRefreshPhase.Compatibility)]
    public async Task Original_pre_model_snapshot_GET_503_closes_exact_work_and_recovers_after_current_checks(SourceRefreshPhase phase)
    {
        var transport = fixture.Reset();
        AdvanceDefaultBranch(transport);
        var source = transport.Scenario.Source.Source!;
        var governance = transport.Scenario.Arm.Product.Export();
        var failed = false;
        source.OnRequest = request =>
        {
            source.GitHubStatus = 200;
            if (failed || request.RequestUri!.Host != "api.github.com" || request.RequestUri.AbsolutePath != "/repositories/913"
                || !source.Progress.TryGetValue(FactorySourceRefresh.CurrentKey, out var stored)) return;
            var progress = JsonSerializer.Deserialize<SourceRefreshProgress>(stored.Value, FactorySourceValidation.RecordJson)!;
            if (progress.Work is null || progress.Phase != phase) return;
            failed = true;
            source.GitHubStatus = 503;
        };
        var stopped = await RefreshAsync(transport, new FactoryDecisionRequest(Guid.NewGuid()));
        Assert.True(failed, "The initial exact-snapshot GET after recording the current CODE work was not rejected.");
        Assert.Equal("Blocked", (string?)stopped["readiness"]);
        var original = JsonSerializer.Deserialize<SourceRefreshProgress>(
            source.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
        Assert.True(FactorySourceRefreshValidation.HasConfirmedRecovery(original));
        Assert.Equal(503, original.ReadFailure!.StatusCode);
        Assert.Equal(phase, original.Phase);
        Assert.Null(original.Work);
        Assert.Null(original.Dispatch);
        Assert.Contains(FactorySourceRefresh.ReceiptKey(original.LastRequest!.Value), source.Progress.Keys);
        var retained = source.Progress.Where(p => p.Key.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal)
            && p.Key != FactorySourceRefresh.CurrentKey).ToDictionary();
        var calls = transport.EvaluationInference.Count;
        source.OnRequest = null;
        source.GitHubStatus = 200;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        transport.ApplicationQueryStatus = HttpStatusCode.ServiceUnavailable;
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using (var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        transport.ApplicationQueryStatus = HttpStatusCode.OK;
        var recovered = await RefreshAsync(transport, new FactoryDecisionRequest(Guid.NewGuid()));
        Assert.Equal("ReadyAwaitingActivation", (string?)recovered["readiness"]);
        var current = transport.Scenario.Source.Source!;
        var complete = JsonSerializer.Deserialize<SourceRefreshProgress>(
            current.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
        Assert.Equal(original.Id, complete.Id);
        Assert.Equal(SourceRefreshState.Compatible, complete.State);
        Assert.Equal(6, transport.SourceInferenceInputs.Count);
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        foreach (var pair in retained) Assert.Equal(pair.Value, current.Progress[pair.Key]);
        var audit = await transport.Scenario.Machine.InvokeAsync("audit");
        Assert.True(audit.Exit == 0, audit.Terminal.Output);
    }

    [Theory]
    [InlineData("lost-work-write")]
    [InlineData("lost-closeout-write")]
    [InlineData("post-model-read")]
    [InlineData("paused-closeout")]
    public async Task Pre_model_read_recovery_never_clears_post_model_or_unconfirmed_native_closeout(string boundary)
    {
        var transport = fixture.Reset();
        AdvanceDefaultBranch(transport);
        var source = transport.Scenario.Source.Source!;
        var failedRead = false;
        var failedWrite = false;
        var originalAfterWrite = source.AfterWrite;
        source.AfterWrite = (key, value) =>
        {
            originalAfterWrite?.Invoke(key, value);
            if (!failedWrite && (boundary == "lost-work-write" && key == FactorySourceRefresh.CurrentKey
                    && JsonSerializer.Deserialize<SourceRefreshProgress>(value, FactorySourceValidation.RecordJson)!.Work is not null
                || boundary == "lost-closeout-write" && failedRead
                    && key.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal) && key.Contains("/checkpoints/", StringComparison.Ordinal)))
            {
                failedWrite = true;
                throw new HttpRequestException("test-only-original-native-write-response-lost");
            }
        };
        source.BeforeRequestAsync = async (request, _) =>
        {
            source.GitHubStatus = 200;
            if (boundary == "lost-work-write" || failedRead || request.RequestUri!.Host != "api.github.com"
                || request.RequestUri.AbsolutePath != "/repositories/913"
                || !source.Progress.TryGetValue(FactorySourceRefresh.CurrentKey, out var stored)
                || JsonSerializer.Deserialize<SourceRefreshProgress>(stored.Value, FactorySourceValidation.RecordJson)!.Work is null
                || boundary == "post-model-read" && transport.SourceInferenceInputs.Count == 0) return;
            failedRead = true;
            if (boundary == "paused-closeout")
            {
                var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
                Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
            }
            source.GitHubStatus = 503;
        };
        try
        {
            var invocation = new FactoryDecisionRequest(Guid.NewGuid());
            var stopped = await RefreshAsync(transport, invocation);
            Assert.Equal(boundary != "lost-work-write", failedRead);
            Assert.Equal(boundary is "lost-work-write" or "lost-closeout-write", failedWrite);
            Assert.Equal("Unknown", (string?)stopped["readiness"]);
            var calls = transport.EvaluationInference.Count;
            Assert.Equal(boundary == "post-model-read" ? 1 : 0, transport.SourceInferenceInputs.Count);
            var original = source.Progress.ToDictionary();
            source.AfterWrite = originalAfterWrite;
            source.BeforeRequestAsync = null;
            source.GitHubStatus = 200;
            transport.Restart();
            FactoryActivationTests.ConnectAudit(transport);
            var inspected = await PollRefreshAsync(transport, invocation);
            Assert.Equal("Unknown", (string?)inspected["readiness"]);
            using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(original, transport.Scenario.Source.Source!.Progress);
            Assert.Equal(0, transport.IssuePosts);
        }
        finally
        {
            source.AfterWrite = originalAfterWrite;
            source.BeforeRequestAsync = null;
            source.GitHubStatus = 200;
        }
    }
}
