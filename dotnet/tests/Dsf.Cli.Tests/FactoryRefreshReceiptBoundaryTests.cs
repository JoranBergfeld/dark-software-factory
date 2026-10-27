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
    [InlineData("INFER_INTENT", "authority", 2)]
    [InlineData("CHECK_EVERY_CLAIM", "authority", 2)]
    [InlineData("CHECK_UNCHANGED_GOVERNING_INTENT", "authority", 2)]
    [InlineData("INFER_INTENT", "authority", 3)]
    [InlineData("CHECK_EVERY_CLAIM", "authority", 3)]
    [InlineData("INFER_INTENT", "authority", 4)]
    [InlineData("CHECK_EVERY_CLAIM", "authority", 4)]
    [InlineData("INFER_INTENT", "step-response", 0)]
    [InlineData("CHECK_EVERY_CLAIM", "step-response", 0)]
    [InlineData("CHECK_UNCHANGED_GOVERNING_INTENT", "step-response", 0)]
    [InlineData("INFER_INTENT", "checkpoint-response", 0)]
    [InlineData("CHECK_EVERY_CLAIM", "checkpoint-response", 0)]
    [InlineData("CHECK_UNCHANGED_GOVERNING_INTENT", "checkpoint-response", 0)]
    [InlineData("CHECK_EVERY_CLAIM", "retention-response", 0)]
    public async Task Accepted_result_to_original_receipt_boundary_never_reclassifies_currentness_or_write_failure(
        string stage, string boundary, int authorityCheck)
    {
        var transport = fixture.Reset();
        AdvanceDefaultBranch(transport);
        var source = transport.Scenario.Source.Source!;
        var governance = transport.Scenario.Arm.Product.Export();
        var compatibility = stage == "CHECK_UNCHANGED_GOVERNING_INTENT";
        IntentDispatch? dispatch = null;
        Guid analysisRevision = Guid.Empty;
        string? stepKey = null;
        var checks = 0;
        var failed = false;
        var rejectAuthority = false;
        var originalAfterWrite = source.AfterWrite;
        transport.AfterHostResponse = (path, _) =>
        {
            if (path == "/authority") rejectAuthority = false;
            if (path != "/synthesis" || dispatch is not null
                || transport.SourceInferenceInputs.LastOrDefault()?.Contains(stage, StringComparison.Ordinal) != true) return;
            var analysis = JsonSerializer.Deserialize<IntentProgress>(source.Progress["intent/current"].Value, FactorySourceValidation.RecordJson)!;
            if (boundary == "retention-response" && analysis.CheckedChunks + 1 != analysis.AvailableChunks) return;
            var refresh = JsonSerializer.Deserialize<SourceRefreshProgress>(
                source.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
            analysisRevision = analysis.Revision;
            dispatch = compatibility ? refresh.Dispatch : analysis.Dispatch;
            Assert.NotNull(dispatch);
            stepKey = compatibility ? FactorySourceRefresh.StepKey(refresh.Id, refresh.CheckedChunks)
                : $"intent/{analysis.Revision:N}/steps/{analysis.AnalyzedChunks + analysis.CheckedChunks:D4}";
        };
        transport.BeforeIngressRequest = uri =>
        {
            if (boundary == "authority" && dispatch is not null && uri.AbsolutePath == "/authority" && ++checks == authorityCheck)
                rejectAuthority = true;
            return Task.CompletedTask;
        };
        transport.ConfigurationResponse = (host, response) =>
        {
            if (!rejectAuthority || host != "serve-models") return Task.FromResult(response);
            failed = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("test-only-late-native-governance-failure") });
        };
        source.AfterWrite = (key, value) =>
        {
            originalAfterWrite?.Invoke(key, value);
            if (dispatch is null || failed) return;
            var selected = boundary == "step-response" && key == stepKey
                || boundary == "retention-response" && key == FactoryAuditRetention.EvidenceKey(analysisRevision);
            if (boundary == "checkpoint-response")
                selected = compatibility ? key == FactorySourceRefresh.CurrentKey
                    && JsonSerializer.Deserialize<SourceRefreshProgress>(value, FactorySourceValidation.RecordJson)!.Dispatch is null
                    : key == "intent/current"
                        && JsonSerializer.Deserialize<IntentProgress>(value, FactorySourceValidation.RecordJson)!.ModelOutcome == IntentModelOutcome.Confirmed;
            if (!selected) return;
            failed = true;
            throw new HttpRequestException("test-only-original-native-result-closeout-response-lost");
        };
        try
        {
            var invocation = new FactoryDecisionRequest(Guid.NewGuid());
            var stopped = await RefreshAsync(transport, invocation);
            Assert.True(failed, $"The actual {stage}/{boundary}/{authorityCheck} boundary was not failed.");
            Assert.Equal("Unknown", (string?)stopped["readiness"]);
            Assert.NotNull(dispatch);
            var refresh = JsonSerializer.Deserialize<SourceRefreshProgress>(
                source.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
            var analysis = JsonSerializer.Deserialize<IntentProgress>(source.Progress["intent/current"].Value, FactorySourceValidation.RecordJson)!;
            var hasReceipt = boundary != "authority" || authorityCheck >= 3;
            Assert.Equal(hasReceipt, source.Progress.ContainsKey(stepKey!));
            Assert.Null(refresh.Recovery);
            Assert.Null(refresh.LastFailure);
            Assert.Null(analysis.LastFailure);
            if (boundary == "step-response" || boundary == "authority" && authorityCheck < 4)
                Assert.Equal(dispatch, compatibility ? refresh.Dispatch : analysis.Dispatch);
            else if (!compatibility)
            {
                Assert.Null(analysis.Dispatch);
                Assert.Equal(IntentModelOutcome.Confirmed, analysis.ModelOutcome);
            }
            var original = source.Progress.ToDictionary();
            var calls = transport.EvaluationInference.Count;
            source.AfterWrite = originalAfterWrite;
            transport.AfterHostResponse = null;
            transport.BeforeIngressRequest = null;
            transport.ConfigurationResponse = null;
            transport.Restart();
            FactoryActivationTests.ConnectAudit(transport);
            Assert.Equal("Unknown", (string?)(await PollRefreshAsync(transport, invocation))["readiness"]);
            using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
            using (var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(original, transport.Scenario.Source.Source!.Progress);
            Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
            var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(0, transport.IssuePosts);
        }
        finally
        {
            source.AfterWrite = originalAfterWrite;
            transport.AfterHostResponse = null;
            transport.BeforeIngressRequest = null;
            transport.ConfigurationResponse = null;
        }
    }
}
