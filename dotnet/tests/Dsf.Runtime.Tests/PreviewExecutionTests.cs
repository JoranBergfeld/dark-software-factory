using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PreviewExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_run_receipts_remain_readable_but_changed_native_export_hash_is_rejected(bool corruptHash)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var evaluation = await transport.RunEvaluationAsync("/evaluate");
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(evaluation.Body)!["readiness"]);
        var key = transport.EvaluationDocuments.Keys.Single(k => k.Contains("/council-preview/", StringComparison.Ordinal));
        var native = JsonNode.Parse(transport.EvaluationDocuments[key])!;
        if (corruptHash) native["run"]!["nativeDocumentSha256"] = new string('f', 64);
        else native["run"]!.AsObject().Remove("nativeDocumentSha256");
        using var parsed = JsonDocument.Parse(native.ToJsonString());
        native["checksum"] = PreviewEvaluationDocument.ReadNative(parsed.RootElement).CalculateChecksum();
        transport.EvaluationDocuments[key] = native.ToJsonString();
        var writes = transport.EvaluationWrites.Count;
        var inference = transport.Intent.Models.Inference.Count;
        var status = await transport.RunEvaluationAsync("/evaluation");
        if (corruptHash) Assert.DoesNotContain("ReadyAwaitingActivation", status.Body);
        else Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(status.Body)!["readiness"]);
        Assert.Equal(writes, transport.EvaluationWrites.Count);
        Assert.Equal(inference, transport.Intent.Models.Inference.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unsupported")]
    [InlineData("citation")]
    [InlineData("partial-round")]
    [InlineData("juror")]
    [InlineData("oversized")]
    public async Task Incomplete_or_invalid_native_evaluation_never_becomes_a_proof(string fault)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        if (fault == "juror") transport.Intent.Models.Scenario = "malformed-judgment";
        transport.AlterEvaluationCompletion = (input, text) =>
        {
            if (fault == "oversized" && input.StartsWith("PROPOSAL_SYNTHESIS", StringComparison.Ordinal)) return new string('x', 8193);
            if (fault == "partial-round" && input.StartsWith("As the 'cost'", StringComparison.Ordinal) && !input.Contains("\"PriorRound\":[]", StringComparison.Ordinal))
                return "Perhaps the observed request warrants investigation, but this is deliberately not a typed lens position.";
            if (input.StartsWith("PROPOSAL_SYNTHESIS", StringComparison.Ordinal) && fault is "empty" or "citation")
            {
                var body = JsonNode.Parse(text)!;
                if (fault == "empty")
                {
                    body["Candidate"] = null;
                    body["Reason"] = "The isolated application request does not establish a genuinely supported feature candidate without additional investigation.";
                }
                else body["Candidate"]!["Claims"]![0]!["EvidenceReferences"] = new JsonArray("missing-observation");
                return body.ToJsonString();
            }
            if (fault == "unsupported" && input.StartsWith("SEMANTIC_GROUNDING", StringComparison.Ordinal))
            {
                var body = JsonNode.Parse(text)!;
                body["ContentSupported"] = false;
                body["Checks"]![0]!["Support"] = "Unsupported";
                return body.ToJsonString();
            }
            return text;
        };
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var output = JsonNode.Parse(result.Body)!;
        Assert.NotEqual("ReadyAwaitingActivation", (string?)output["readiness"]);
        Assert.Null(output["proof"]);
        Assert.Empty(output["run"]!["filedIssues"]!.AsArray());
        Assert.True((string?)output["run"]!["status"] == (fault == "empty" ? "Previewed" : "Error"),
            output["run"]!["failureReason"]?.ToJsonString());
        if (fault == "partial-round") Assert.Single(output["run"]!["proposals"]![0]!["councilReview"]!["rounds"]!.AsArray());
        if (fault == "juror")
        {
            Assert.Equal("KnownFailed", (string?)output["dispatches"]!.AsArray()[^1]!["state"]);
            Assert.Equal("Failed", (string?)output["readiness"]);
        }
        var inference = transport.Intent.Models.Inference.Count;
        var status = await transport.RunEvaluationAsync("/evaluation");
        Assert.True(status.Status == HttpStatusCode.OK, status.Body);
        Assert.Null(JsonNode.Parse(status.Body)!["proof"]);
        Assert.Equal(inference, transport.Intent.Models.Inference.Count);
    }

    [Fact]
    public async Task Selected_empty_sources_are_evaluated_without_substituting_for_actual_application_evidence()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true,
            supplementals: ["foundryiq", "webiq"], aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        foreach (var (key, value) in transport.Source.Values.Where(p => p.Key.StartsWith("dsf/factory/preview", StringComparison.Ordinal)))
            Assert.True(Encoding.UTF8.GetByteCount(key + value) + 1024 < 10 * 1024, key + ":" + Encoding.UTF8.GetByteCount(key + value));
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var body = JsonNode.Parse(result.Body)!;
        Assert.Equal("ReadyAwaitingActivation", (string?)body["readiness"]);
        Assert.Single(body["run"]!["evidence"]!.AsArray());
        Assert.Equal(2, body["inputs"]!["supplemental"]!["sources"]!.AsArray().Count);
        Assert.Equal(6, transport.SupplementalQueries.Count(q => q.Kind == "webiq"));
    }

    [Fact]
    public async Task Status_keeps_historical_proof_but_withholds_readiness_when_captured_application_evidence_expires()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var capturedTime = transport.Source.Clock.Now.AddSeconds(-transport.Telemetry!.Configuration.FreshnessSeconds + 60);
        transport.ChangeTelemetryQueryResponse = body =>
        {
            if (transport.TelemetryQueries.Count <= 2) body["tables"]![0]!["rows"]![0]![0] = capturedTime.ToString("O");
            return body;
        };
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(result.Body)!["readiness"]);
        var writes = transport.EvaluationWrites.Count;
        transport.Source.Clock.Now += TimeSpan.FromSeconds(121);
        var stale = await transport.RunEvaluationAsync("/evaluation");
        Assert.True(stale.Status == HttpStatusCode.OK, stale.Body);
        var historical = JsonNode.Parse(stale.Body)!;
        Assert.Equal("Blocked", (string?)historical["readiness"]);
        Assert.Equal("CapturedApplicationEvidenceStale", (string?)historical["reason"]);
        Assert.NotNull(historical["proof"]);
        Assert.Equal(writes, transport.EvaluationWrites.Count);
    }

    [Fact]
    public async Task Status_cannot_release_readiness_if_durable_state_disappears_during_current_input_checks()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(result.Body)!["readiness"]);
        transport.OnTelemetryQuery = () => transport.EvaluationDocuments.Clear();
        var status = await transport.RunEvaluationAsync("/evaluation");
        Assert.DoesNotContain("\"readiness\":\"ReadyAwaitingActivation\"", status.Body);
    }

    [Fact]
    public async Task Compatible_terminal_evaluation_finishes_interrupted_final_current_checks_without_rejudging()
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await first.PrepareEvaluationAsync();
        first.ChangeContextResponse = body => first.ContextCalls == 3 ? throw new HttpRequestException("Lost final context response.") : body;
        var interrupted = await first.RunEvaluationAsync("/evaluate");
        Assert.DoesNotContain("\"readiness\":\"ReadyAwaitingActivation\"", interrupted.Body);
        var coordinator = JsonNode.Parse(first.EvaluationDocuments.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Value)!;
        Assert.Equal("Evaluated", (string?)coordinator["phase"]);
        Assert.Null(coordinator["proof"]);
        using var second = await PreviewContextTransport.ReconstructAsync(first);
        second.ImportEvaluationState(first);
        var inference = second.Intent.Models.Inference.Count;
        var resumed = await second.RunEvaluationAsync("/evaluate");
        Assert.True(resumed.Status == HttpStatusCode.OK, resumed.Body);
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(resumed.Body)!["readiness"]);
        Assert.Equal(inference, second.Intent.Models.Inference.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("partial")]
    [InlineData("identity")]
    [InlineData("intent")]
    [InlineData("orphan")]
    [InlineData("selected-failed")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task Execution_rejects_unusable_inputs_before_creating_a_run_or_dispatching_models(string failure)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true,
            supplementals: failure == "selected-failed" ? ["webiq"] : null, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        if (failure == "identity") transport.TelemetryTokenIdentity = transport.Source.Code.PrincipalId;
        if (failure == "intent") transport.Source.Values.Remove(ProductConfigurationKeys.CharterAdopted);
        if (failure == "orphan") transport.Source.Values[ProductConfigurationKeys.SupplementalOperation(Guid.NewGuid(), 0)] = "{}";
        if (failure == "selected-failed") transport.WebPayload = """{"webResults":null}""";
        if (failure == "malformed") transport.ChangeTelemetryResponse = _ => "{";
        if (failure == "oversized") transport.TelemetryNativeResponse = r => r.RequestUri!.Host == "api.loganalytics.io"
            ? new(HttpStatusCode.OK) { Content = new PreviewTelemetryConsumerTests.CountingTelemetryContent() } : null;
        transport.ChangeTelemetryQueryResponse = body =>
        {
            var table = body["tables"]![0]!;
            if (failure == "empty") table["rows"] = new JsonArray();
            if (failure == "stale") table["rows"]![0]![0] = transport.Source.Clock.Now.AddDays(-2).ToString("O");
            if (failure == "foreign") table["rows"]![0]![1] = FactoryTelemetryHostTests.TelemetryTransport.Component + "-foreign";
            if (failure == "partial") body["error"] = new JsonObject { ["code"] = "PartialError" };
            return body;
        };
        var inference = transport.Intent.Models.Inference.Count;
        var writes = transport.EvaluationWrites.Count;
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("ReadyAwaitingActivation", result.Body);
        Assert.Equal(inference, transport.Intent.Models.Inference.Count);
        Assert.Single(transport.EvaluationDocuments);
        var coordinator = JsonNode.Parse(transport.EvaluationDocuments.Single().Value)!;
        Assert.Null(coordinator["inputs"]);
        Assert.Null(coordinator["run"]);
        Assert.Null(coordinator["pendingRun"]);
        Assert.Empty(coordinator["dispatches"]!.AsArray());
        if (coordinator["admission"] is null) Assert.Equal(writes, transport.EvaluationWrites.Count);
        else Assert.Contains((string?)coordinator["admission"]!["state"], new[] { "Released", "Unknown" });
    }

    [Theory]
    [InlineData("/prepare-evaluation", "code")]
    [InlineData("/prepare-evaluation", "council")]
    [InlineData("/evaluate", "code")]
    [InlineData("/evaluate", "council")]
    [InlineData("/evaluation", "foreign")]
    public async Task Evaluation_routes_do_not_expand_native_operator_authority(string route, string caller)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        var principal = caller == "code" ? transport.Source.Code.PrincipalId : caller == "council" ? transport.Models.Bindings![0].PrincipalId : Guid.NewGuid().ToString();
        var result = await transport.RunEvaluationAsync(route, principal);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Contains("ApprovedPreviewCallerRequired", result.Body);
        Assert.Empty(transport.EvaluationWrites);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Theory]
    [InlineData("before-intent")]
    [InlineData("intended")]
    [InlineData("dispatched")]
    [InlineData("confirmed")]
    public async Task Only_definitely_undispatched_or_confirmed_native_work_can_resume(string stop)
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await first.PrepareEvaluationAsync();
        var inference = first.Intent.Models.Inference.Count;
        var stopped = false;
        first.AfterEvaluationWrite = body =>
        {
            var dispatches = body["dispatches"]?.AsArray();
            if (!stopped && (stop == "before-intent" ? body["pendingRun"]?["station"]?.GetValue<string>() == "s2_investigation"
                : dispatches?.Count == 1 && (string?)dispatches[0]!["state"] == char.ToUpperInvariant(stop[0]) + stop[1..]))
            {
                stopped = true;
                throw new HttpRequestException("Lost native acknowledgment at " + stop);
            }
        };
        var initial = await first.RunEvaluationAsync("/evaluate");
        Assert.True(stopped);
        var initialBody = JsonNode.Parse(initial.Body)!;
        Assert.True((string?)initialBody["readiness"] == "Unknown" || (string?)initialBody["state"] == "Unknown",
            initial.Body);
        Assert.DoesNotContain("\"readiness\":\"ReadyAwaitingActivation\"", initial.Body);
        Assert.Equal(stop == "confirmed" ? 1 : 0, first.Intent.Models.Inference.Count - inference);
        using var next = await PreviewContextTransport.ReconstructAsync(first);
        next.ImportEvaluationState(first);
        var resumedInference = next.Intent.Models.Inference.Count;
        var resumed = await next.RunEvaluationAsync("/evaluate");
        if (stop is "before-intent" or "dispatched")
        {
            Assert.Contains("Unknown", resumed.Body);
            Assert.DoesNotContain("ReadyAwaitingActivation", resumed.Body);
            Assert.Equal(resumedInference, next.Intent.Models.Inference.Count);
        }
        else
        {
            Assert.True(resumed.Status == HttpStatusCode.OK, resumed.Body);
            Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(resumed.Body)!["readiness"]);
            Assert.Equal(stop == "confirmed" ? 15 : 16, next.Intent.Models.Inference.Count - resumedInference);
        }
    }

    [Fact]
    public async Task Pending_run_write_with_a_lost_native_acknowledgment_reconciles_only_the_exact_saved_run()
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await first.PrepareEvaluationAsync();
        var interrupted = false;
        first.AfterEvaluationWrite = body =>
        {
            if (!interrupted && body["onboardingChecksum"] is not null && body["checkpoints"]?.AsArray().Count == 2)
            {
                interrupted = true;
                throw new HttpRequestException("Lost run checkpoint acknowledgment.");
            }
        };
        var initial = await first.RunEvaluationAsync("/evaluate");
        Assert.True(interrupted);
        Assert.NotEqual(HttpStatusCode.OK, initial.Status);
        using var next = await PreviewContextTransport.ReconstructAsync(first);
        next.ImportEvaluationState(first);
        var resumed = await next.RunEvaluationAsync("/evaluate");
        Assert.True(resumed.Status == HttpStatusCode.OK, resumed.Body);
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(resumed.Body)!["readiness"]);
    }

    [Fact]
    public async Task Lost_acknowledgment_of_confirmed_juror_resumes_serialized_state_without_rejudging()
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await first.PrepareEvaluationAsync();
        var before = first.Intent.Models.Inference.Count;
        var interrupted = false;
        first.AfterEvaluationWrite = body =>
        {
            if (!interrupted && body["dispatches"]?.AsArray().Any(d => (string?)d!["request"]!["route"] == "juror-1"
                && (string?)d["state"] == "Confirmed") == true)
            {
                interrupted = true;
                throw new HttpRequestException("lost-native-acknowledgment");
            }
        };
        var stopped = await first.RunEvaluationAsync("/evaluate");
        Assert.True(interrupted);
        Assert.NotEqual(HttpStatusCode.OK, stopped.Status);
        Assert.Equal(14, first.Intent.Models.Inference.Count - before);
        using var second = await PreviewContextTransport.ReconstructAsync(first);
        second.ImportEvaluationState(first);
        var secondBefore = second.Intent.Models.Inference.Count;
        var resumed = await second.RunEvaluationAsync("/evaluate");
        Assert.True(resumed.Status == HttpStatusCode.OK, resumed.Body);
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(resumed.Body)!["readiness"]);
        Assert.Equal(2, second.Intent.Models.Inference.Count - secondBefore);
        var writes = second.EvaluationWrites.Count;
        var duplicate = await second.RunEvaluationAsync("/evaluate");
        Assert.True(duplicate.Status == HttpStatusCode.OK, duplicate.Body);
        Assert.Equal(2, second.Intent.Models.Inference.Count - secondBefore);
        Assert.Equal(writes, second.EvaluationWrites.Count);
    }

    [Fact]
    public async Task Lost_model_response_is_durably_unknown_and_never_replayed_by_an_independent_worker()
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await first.PrepareEvaluationAsync();
        var before = first.Intent.Models.Inference.Count;
        first.ChangeModelResponse = text => first.Intent.Models.Inference.Count > before ? throw new HttpRequestException("private-provider-detail") : text;
        var stopped = await first.RunEvaluationAsync("/evaluate");
        Assert.Equal(1, first.Intent.Models.Inference.Count - before);
        Assert.Contains("Unknown", stopped.Body);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(stopped.Body)!["readiness"]);
        Assert.DoesNotContain("private-provider-detail", stopped.Body);
        using var second = await PreviewContextTransport.ReconstructAsync(first);
        second.ImportEvaluationState(first);
        var nextBefore = second.Intent.Models.Inference.Count;
        var retry = await second.RunEvaluationAsync("/evaluate");
        Assert.DoesNotContain("ReadyAwaitingActivation", retry.Body);
        Assert.Contains("Unknown", retry.Body);
        Assert.Equal(nextBefore, second.Intent.Models.Inference.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("checksum")]
    [InlineData("incarnation")]
    [InlineData("malformed-incarnation")]
    [InlineData("erased-field")]
    public async Task Approved_seed_cannot_be_replaced_by_missing_corrupt_or_foreign_native_state(string corruption)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var key = transport.EvaluationDocuments.Keys.Single();
        if (corruption == "missing") transport.EvaluationDocuments.Clear();
        else
        {
            var body = JsonNode.Parse(transport.EvaluationDocuments[key])!;
            if (corruption == "checksum") body["checksum"] = new string('f', 64);
            if (corruption == "incarnation") body["_rid"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("replacement"));
            if (corruption == "malformed-incarnation") body["_rid"] = "malformed-native-resource";
            if (corruption == "erased-field") body.AsObject().Remove("dispatches");
            transport.EvaluationDocuments[key] = body.ToJsonString();
        }
        var writes = transport.EvaluationWrites.Count;
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.Equal(writes, transport.EvaluationWrites.Count);
        var reinitialize = await transport.RunEvaluationAsync("/prepare-evaluation");
        Assert.NotEqual(HttpStatusCode.OK, reinitialize.Status);
        Assert.Equal(writes, transport.EvaluationWrites.Count);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Theory]
    [InlineData("Go,Go,Go", "Proceed", "Previewed")]
    [InlineData("Go,Go,NoGo", "Escalate", "Escalated")]
    [InlineData("Go,NoGo,NoGo", "Escalate", "Escalated")]
    [InlineData("NoGo,NoGo,NoGo", "Kill", "Killed")]
    public async Task Complete_actual_jury_dispositions_are_durable_readiness_not_activation(string positions, string disposition, string status)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var jury = positions.Split(',');
        transport.AlterEvaluationCompletion = (input, text) =>
        {
            for (var index = 0; index < 3; index++)
                if (input.Contains($"As juror 'juror-{index + 1}'", StringComparison.Ordinal))
                    return (jury[index] == "Go" ? "GO" : "NO-GO") + ": The scoped application evidence and proposed benefit were independently considered.";
            return text;
        };
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var body = JsonNode.Parse(result.Body)!;
        Assert.Equal("ReadyAwaitingActivation", (string?)body["readiness"]);
        Assert.Equal(status, (string?)body["run"]!["status"]);
        var proof = body["proof"]!;
        Assert.Single(proof["candidates"]!.AsArray());
        Assert.Equal(disposition, (string?)proof["candidates"]![0]!["disposition"]);
        Assert.Equal((string?)body["receipt"]!["checksum"], (string?)proof["runChecksum"]);
        Assert.Equal((string?)body["inputs"]!["revision"], (string?)proof["inputRevision"]);
        var writes = transport.EvaluationWrites.Count;
        var inference = transport.Intent.Models.Inference.Count;
        var inspect = await transport.RunEvaluationAsync("/evaluation");
        Assert.True(inspect.Status == HttpStatusCode.OK, inspect.Body);
        var current = JsonNode.Parse(inspect.Body)!;
        Assert.Equal("ReadyAwaitingActivation", (string?)current["readiness"]);
        Assert.True(JsonNode.DeepEquals(proof, current["proof"]));
        Assert.Equal(writes, transport.EvaluationWrites.Count);
        Assert.Equal(inference, transport.Intent.Models.Inference.Count);
    }

    [Fact]
    public async Task Operator_executes_actual_singleton_embedding_synthesis_grounding_deliberation_and_three_jurors()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var before = transport.Intent.Models.Inference.Count;
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var body = JsonNode.Parse(result.Body)!;
        var run = body["run"]!;
        Assert.Equal("OnboardingPreview", (string?)run["profile"]);
        Assert.True((bool)run["dryRun"]!);
        var proposal = Assert.Single(run["proposals"]!.AsArray())!;
        Assert.Equal("Show shift conflicts clearly", (string?)proposal["title"]);
        Assert.True((bool)proposal["grounding"]!["result"]!["contentSupported"]!);
        Assert.Equal(2, proposal["councilReview"]!["rounds"]!.AsArray().Count);
        Assert.Equal(3, proposal["councilReview"]!["juryVerdicts"]!.AsArray().Count);
        Assert.Equal("Proceed", (string?)proposal["verdict"]);
        Assert.Equal(16, transport.Intent.Models.Inference.Count - before);
        Assert.Equal(16, body["dispatches"]!.AsArray().Count);
        Assert.All(body["dispatches"]!.AsArray(), d => Assert.Equal("Confirmed", (string?)d!["state"]));
        Assert.Empty(run["filedIssues"]!.AsArray());
        Assert.Equal("dsf:proposal", (string?)Assert.Single(proposal["intendedPublication"]!["labels"]!.AsArray()));
        Assert.Empty(proposal["intendedPublication"]!["assignees"]!.AsArray());
        Assert.Equal(2, transport.EvaluationDocuments.Count);
    }

    [Fact]
    public async Task Operator_prepares_native_create_once_state_without_evaluation_or_governance_writes()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        var result = await transport.RunEvaluationAsync("/prepare-evaluation");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        Assert.Single(transport.EvaluationDocuments);
        Assert.Single(transport.EvaluationWrites);
        Assert.Equal("POST", transport.EvaluationWrites[0]);
        Assert.Empty(transport.TelemetryQueries);
        Assert.Equal(0, transport.ContextCalls);
        var again = await transport.RunEvaluationAsync("/prepare-evaluation");
        Assert.Equal(result.Body, again.Body);
        Assert.Single(transport.EvaluationWrites);
    }
}

internal sealed partial class PreviewContextTransport
{
    private sealed class NativeEvaluationState
    {
        internal readonly Dictionary<string, string> Documents = [];
        internal readonly List<string> Writes = [];
    }
    private NativeEvaluationState evaluationState = new();
    internal Dictionary<string, string> EvaluationDocuments => evaluationState.Documents;
    internal List<string> EvaluationWrites => evaluationState.Writes;
    internal Action<JsonNode>? AfterEvaluationWrite { get; set; }
    internal Func<JsonNode, CancellationToken, Task>? AfterEvaluationWriteAsync { get; set; }
    internal Func<HttpRequestMessage, HttpResponseMessage?>? EvaluationNativeResponse { get; set; }
    internal Func<string, string, string>? AlterEvaluationCompletion { get; set; }

    internal void ImportEvaluationState(PreviewContextTransport original)
    {
        EvaluationDocuments.Clear();
        foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(original.EvaluationDocuments))!)
            EvaluationDocuments[pair.Key] = pair.Value;
        Intent.Models.Completion = input => AlterEvaluationCompletion?.Invoke(input, EvaluationCompletion(input)) ?? EvaluationCompletion(input);
        Intent.Models.Judgment = (_, input) => AlterEvaluationCompletion?.Invoke(input, EvaluationCompletion(input)) ?? EvaluationCompletion(input);
    }

    internal void ShareNativeEvaluationState(PreviewContextTransport original) => evaluationState = original.evaluationState;

    internal async Task PrepareEvaluationAsync()
    {
        var seedResponse = await RunEvaluationAsync("/prepare-evaluation");
        Assert.True(seedResponse.Status == HttpStatusCode.OK, seedResponse.Body);
        var seed = JsonSerializer.Deserialize<PreviewExecutionSeed>(seedResponse.Body, FactorySourceValidation.RecordJson)!;
        var execution = new PreviewExecutionConfiguration(FactoryPreviewExecution.Policy, seed);
        Source.Values[FactoryPreviewExecution.ConfigurationKey(Request.OperationId)] = JsonSerializer.Serialize(execution, FactoryGitHubJson.Options);
        Capability = Capability with { Configuration = Capability.Configuration with
            { Execution = execution, ExecutionRevision = FactoryModelsValidation.Hash(execution) } };
        ReplaceBinding("runs", JsonSerializer.Deserialize<FoundationOperation>(
            Source.Values[ProductConfigurationKeys.PreviewBinding(Request.OperationId, "runs")], FactorySourceValidation.RecordJson)!);
        Intent.Models.Completion = input => AlterEvaluationCompletion?.Invoke(input, EvaluationCompletion(input)) ?? EvaluationCompletion(input);
        Intent.Models.Judgment = (_, input) => AlterEvaluationCompletion?.Invoke(input, EvaluationCompletion(input)) ?? EvaluationCompletion(input);
    }

    private string EvaluationCompletion(string input)
    {
        if (input.StartsWith("PROPOSAL_SYNTHESIS", StringComparison.Ordinal))
        {
            var evidence = JsonNode.Parse(input[(input.LastIndexOf(" Evidence: ", StringComparison.Ordinal) + 11)..])!;
            return JsonSerializer.Serialize(new ProposalSynthesis(true,
                new("Show shift conflicts clearly", "A shift confirmation request returned 409. Show an actionable conflict explanation.",
                    [new("conflict", "A shift confirmation request returned 409.", [(string)evidence[0]!["reference"]!])],
                    ["The frequency and root cause remain unknown."]), "An attributable application failure supports this proposal."));
        }
        if (input.StartsWith("SEMANTIC_GROUNDING", StringComparison.Ordinal))
        {
            const string marker = " Original evidence including possible counterevidence: ";
            var evidence = JsonNode.Parse(input[(input.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..])!;
            return JsonSerializer.Serialize(new SemanticGroundingResult(true, true, "The actual request observation supports the bounded claim.",
                [new("conflict", ClaimSupport.Supported, "The original request reports a 409 conflict response.",
                    [new((string)evidence[0]!["reference"]!, "409")], [])], ["No prevalence or cause is established by this observation."]));
        }
        return "GO: The actual scoped conflict observation supports this bounded improvement to volunteer scheduling.";
    }

    internal async Task<PreviewContextRunner> RunEvaluationAsync(string route, string? caller = null, bool governanceMayChangeExternally = false)
    {
        var writes = Source.Writes.Count;
        var records = Source.Values.ToDictionary(p => p.Key, p => p.Value);
        var runner = new PreviewContextRunner(Source) { Route = route, Caller = caller ?? Source.Manifest.Identity.AzureActorId, Request = Request };
        using var http = new HttpClient(this, false) { Timeout = Timeout.InfiniteTimeSpan };
        var env = Source.Environment;
        env["AZURE_CLIENT_ID"] = Models.Bindings![0].ClientId;
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-preview"], env, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, PreviewHttp = http,
                PreviewCredential = new IntentModelCredential(Source), PreviewClock = Source.Clock }, CancellationToken.None));
        if (route != "/evaluate") Assert.Equal(writes, Source.Writes.Count);
        else Assert.All(Source.Writes.Skip(writes), key => Assert.EndsWith("/retention", key));
        if (!governanceMayChangeExternally) Assert.Equal(records, Source.Values);
        Assert.DoesNotContain(Requests, r => r.Method is "PATCH" or "DELETE" || r.Method == "PUT" && !IsAnalysisRetention(r.Uri));
        return runner;
    }

    private async Task<HttpResponseMessage> EvaluationRequestAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.Equal("[\"" + Source.Manifest.Identity.ProductKey + "\"]", request.Headers.GetValues("x-ms-documentdb-partitionkey").Single());
        var authorization = Uri.UnescapeDataString(request.Headers.GetValues("Authorization").Single());
        Assert.StartsWith("type=aad&ver=1.0&sig=", authorization);
        var jwt = authorization.Split("sig=", StringSplitOptions.None)[1].Split('.')[1].Replace('-', '+').Replace('_', '/');
        var claims = JsonNode.Parse(Convert.FromBase64String(jwt.PadRight((jwt.Length + 3) / 4 * 4, '=')))!;
        Assert.Equal(Models.Bindings![0].PrincipalId, (string?)claims["oid"]);
        Assert.Equal("https://cosmos.azure.com", (string?)claims["aud"]);
        Assert.False(request.Headers.Contains("x-ms-documentdb-is-upsert"));
        var path = request.RequestUri!.AbsolutePath;
        var prefix = "/dbs/" + Source.Manifest.Identity.ProductKey + "/colls/";
        Assert.StartsWith(prefix, path);
        var collection = path[prefix.Length..].Split('/')[0];
        Assert.Contains(collection, new[] { "runs", "council-preview" });
        if (EvaluationNativeResponse?.Invoke(request) is { } response) return response;
        if (request.Method == HttpMethod.Get)
        {
            lock (evaluationState)
                return EvaluationDocuments.TryGetValue(path, out var found) ? Native(found, HttpStatusCode.OK) : new(HttpStatusCode.NotFound);
        }
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        var id = (string)body["id"]!;
        var key = prefix + collection + "/docs/" + id;
        Assert.Equal(Source.Manifest.Identity.ProductKey, (string?)body["product"]);
        lock (evaluationState)
        {
            EvaluationWrites.Add(request.Method.Method);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(prefix + collection + "/docs", path);
                if (EvaluationDocuments.ContainsKey(key)) return new(HttpStatusCode.Conflict);
                var rid = Guid.NewGuid().ToByteArray();
                rid[4] |= 0x80;
                rid[15] &= 0x0f;
                body["_rid"] = Convert.ToBase64String(rid).Replace('/', '-');
            }
            else
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(key, path);
                if (!EvaluationDocuments.TryGetValue(key, out var prior)) return new(HttpStatusCode.NotFound);
                var current = JsonNode.Parse(prior)!;
                if ((string?)current["_etag"] != request.Headers.GetValues("If-Match").Single()) return new(HttpStatusCode.PreconditionFailed);
                body["_rid"] = current["_rid"]!.DeepClone();
            }
            body["_etag"] = "\"" + Guid.NewGuid().ToString("D") + "\"";
            EvaluationDocuments[key] = body.ToJsonString();
        }
        AfterEvaluationWrite?.Invoke(body);
        if (AfterEvaluationWriteAsync is not null) await AfterEvaluationWriteAsync(body, token);
        return Native(body.ToJsonString(), request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);

        static HttpResponseMessage Native(string text, HttpStatusCode status)
        {
            var result = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            result.Headers.TryAddWithoutValidation("ETag", (string?)JsonNode.Parse(text)!["_etag"]);
            return result;
        }
    }
}
