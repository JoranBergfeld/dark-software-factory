using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PreviewExecutionCurrentnessTests
{
    [Fact]
    public async Task Selected_public_guidance_and_required_application_record_reach_the_same_actual_evaluation_without_cloning()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true,
            supplementals: ["foundryiq", "webiq"], aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        transport.WebPayload = """{"webResults":[{"url":"https://provider.example/conflict-guidance","content":"Public guidance recommends actionable explanations for application request conflicts."}]}""";
        var before = transport.Intent.Models.Inference.Count;
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var output = JsonNode.Parse(result.Body)!;
        Assert.Equal("ReadyAwaitingActivation", (string?)output["readiness"]);
        var evidence = output["run"]!["evidence"]!.AsArray();
        Assert.Equal(2, evidence.Count);
        Assert.Equal("azuremonitor", (string?)evidence[0]!["sourceKind"]);
        Assert.Equal("webiq", (string?)evidence[1]!["sourceKind"]);
        Assert.StartsWith("webiq:sha256:", (string?)evidence[1]!["reference"]);
        Assert.Contains("actionable explanations", (string?)evidence[1]!["summary"]);
        Assert.DoesNotContain("provider.example", result.Body);
        var embedding = Assert.Single(transport.Intent.Models.Inference.Skip(before), r => r["input"] is not null);
        Assert.Equal(evidence.Select(e => (string)e!["summary"]!), embedding["input"]!.AsArray().Select(v => (string)v!));
        Assert.Single(output["proof"]!["candidates"]!.AsArray());
        Assert.Equal(16, transport.Intent.Models.Inference.Count - before);
        Assert.Equal(6, transport.SupplementalQueries.Count(q => q.Kind == "foundryiq"));
        Assert.Equal(6, transport.SupplementalQueries.Count(q => q.Kind == "webiq"));
    }

    [Fact]
    public async Task Captured_evidence_expiring_during_the_final_native_read_cannot_be_released_as_current()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var captured = transport.Source.Clock.Now.AddSeconds(-transport.Telemetry!.Configuration.FreshnessSeconds + 60);
        transport.ChangeTelemetryQueryResponse = body =>
        {
            if (transport.TelemetryQueries.Count <= 2) body["tables"]![0]!["rows"]![0]![0] = captured.ToString("O");
            return body;
        };
        var evaluation = await transport.RunEvaluationAsync("/evaluate");
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(evaluation.Body)!["readiness"]);
        var queries = transport.TelemetryQueries.Count;
        var advanced = false;
        transport.EvaluationNativeResponse = r =>
        {
            if (!advanced && r.Method == HttpMethod.Get && transport.TelemetryQueries.Count == queries + 2)
            {
                advanced = true;
                transport.Source.Clock.Now += TimeSpan.FromSeconds(121);
            }
            return null;
        };
        var status = await transport.RunEvaluationAsync("/evaluation");
        Assert.True(advanced);
        Assert.Equal("Blocked", (string?)JsonNode.Parse(status.Body)!["readiness"]);
        Assert.NotNull(JsonNode.Parse(status.Body)!["proof"]);
    }

    [Fact]
    public async Task Selected_source_content_changes_are_not_the_ordinary_telemetry_row_exception()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true,
            supplementals: ["webiq"], aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        transport.AfterEvaluationWrite = body =>
        {
            if (body["proof"] is not null)
                transport.WebPayload = """{"webResults":[{"url":"https://provider.example/new-guidance","content":"New public guidance identifies an additional conflict handling concern."}]}""";
        };
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.Equal("Blocked", (string?)JsonNode.Parse(result.Body)!["readiness"]);
        Assert.NotNull(JsonNode.Parse(result.Body)!["proof"]);
    }

    [Theory]
    [InlineData("Intended")]
    [InlineData("Dispatched")]
    public async Task Concurrent_independent_workers_use_native_ETags_not_expired_leases_to_decide_takeover(string boundary)
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await first.PrepareEvaluationAsync();
        using var second = await PreviewContextTransport.ReconstructAsync(first);
        second.ImportEvaluationState(first);
        second.ShareNativeEvaluationState(first);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstInference = first.Intent.Models.Inference.Count;
        first.AfterEvaluationWriteAsync = async (body, token) =>
        {
            if (body["dispatches"]?.AsArray().Count == 1 && (string?)body["dispatches"]![0]!["state"] == boundary && reached.TrySetResult())
                await release.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
        };
        var running = first.RunEvaluationAsync("/evaluate");
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var secondInference = second.Intent.Models.Inference.Count;
            var other = await second.RunEvaluationAsync("/evaluate");
            Assert.Contains("Unknown", other.Body);
            Assert.Equal(secondInference, second.Intent.Models.Inference.Count);
        }
        finally { release.TrySetResult(); }
        var original = await running;
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(original.Body)!["readiness"]);
        Assert.Equal(firstInference + 16, first.Intent.Models.Inference.Count);
    }

    [Theory]
    [InlineData("adoption")]
    [InlineData("source")]
    [InlineData("native-model")]
    [InlineData("capability")]
    [InlineData("final-source")]
    [InlineData("final-capability")]
    public async Task Observed_drift_during_evaluation_or_proof_release_never_returns_current_readiness(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var inference = transport.Intent.Models.Inference.Count;
        var changed = false;
        void Change()
        {
            if (changed) return;
            changed = true;
            switch (change)
            {
                case "adoption": transport.Source.Values.Remove(ProductConfigurationKeys.CharterAdopted); break;
                case "source":
                case "final-source": transport.Source.Head = new string('f', 40); break;
                case "native-model":
                    transport.Intent.Models.Resources[FactoryModelsValidation.DeploymentTarget(transport.Models.Routes![0].Selection)]
                        ["properties"]!["model"]!["version"] = "replacement-version";
                    break;
                default: transport.CapabilityETag = "changed-current-governance-version"; break;
            }
        }
        if (change.StartsWith("final-", StringComparison.Ordinal))
            transport.AfterEvaluationWrite = body => { if (body["proof"] is not null) Change(); };
        else transport.ChangeModelResponse = text => { if (transport.Intent.Models.Inference.Count > inference) Change(); return text; };
        var result = await transport.RunEvaluationAsync("/evaluate", governanceMayChangeExternally: change == "adoption");
        Assert.True(changed);
        Assert.DoesNotContain("\"readiness\":\"ReadyAwaitingActivation\"", result.Body);
        var coordinator = JsonNode.Parse(transport.EvaluationDocuments.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Value)!;
        if (change.StartsWith("final-", StringComparison.Ordinal)) Assert.NotNull(coordinator["proof"]);
        else Assert.Null(coordinator["proof"]);
        if (change == "final-source")
        {
            var output = JsonNode.Parse(result.Body)!;
            Assert.Equal("Blocked", (string?)output["readiness"]);
            Assert.NotNull(output["proof"]);
        }
    }

    [Fact]
    public async Task Current_rows_and_read_nonces_do_not_replace_or_invalidate_still_fresh_evaluated_content()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        transport.ChangeTelemetryQueryResponse = body =>
        {
            if (transport.TelemetryQueries.Count > 1)
            {
                body["tables"]![0]!["rows"]![0]![2] = "new-application-record";
                body["tables"]![0]!["rows"]![0]![5] = "GET /newer-request";
            }
            return body;
        };
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var output = JsonNode.Parse(result.Body)!;
        Assert.Equal("ReadyAwaitingActivation", (string?)output["readiness"]);
        Assert.Contains("POST /shifts/confirm", (string?)output["run"]!["evidence"]![0]!["summary"]);
        Assert.DoesNotContain("GET /newer-request", output["inputs"]!.ToJsonString());
        Assert.Equal((string?)output["inputs"]!["revision"], (string?)output["proof"]!["inputRevision"]);
        Assert.NotEqual((string?)output["proof"]!["inputRevision"], (string?)output["proof"]!["finalInputRevision"]);
        Assert.Equal((string?)output["receipt"]!["checksum"], (string?)output["proof"]!["runChecksum"]);
        Assert.Equal((string?)output["inputs"]!["context"]!["evidence"]![0]!["reference"], (string?)output["run"]!["evidence"]![0]!["reference"]);
        Assert.Empty(transport.SupplementalQueries);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("unapproved")]
    public async Task Execution_policy_and_seed_are_exact_separately_protected_records(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var key = FactoryPreviewExecution.ConfigurationKey(transport.Request.OperationId);
        if (change == "missing") transport.Source.Values.Remove(key);
        if (change == "changed")
        {
            var execution = JsonNode.Parse(transport.Source.Values[key])!;
            execution["policy"]!["maximumDispatches"] = 63;
            transport.Source.Values[key] = execution.ToJsonString();
        }
        if (change == "unapproved")
        {
            transport.Capability = transport.Capability with { Configuration = transport.Capability.Configuration with { Execution = null, ExecutionRevision = null } };
            transport.ReplaceBinding("runs", JsonSerializer.Deserialize<FoundationOperation>(
                transport.Source.Values[ProductConfigurationKeys.PreviewBinding(transport.Request.OperationId, "runs")], FactorySourceValidation.RecordJson)!);
        }
        var writes = transport.EvaluationWrites.Count;
        var result = await transport.RunEvaluationAsync("/evaluate");
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Equal(writes, transport.EvaluationWrites.Count);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Fact]
    public async Task Source_and_complete_native_run_receipts_are_required_for_readonly_historical_inspection()
    {
        using var original = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await original.PrepareEvaluationAsync();
        var ready = await original.RunEvaluationAsync("/evaluate");
        Assert.Equal("ReadyAwaitingActivation", (string?)JsonNode.Parse(ready.Body)!["readiness"]);
        foreach (var corruption in new[] { "missing-run", "missing-juror", "wrong-profile", "missing-proof", "missing-dispatch", "wrong-run-resource" })
        {
            using var worker = await PreviewContextTransport.ReconstructAsync(original);
            worker.ImportEvaluationState(original);
            var runKey = worker.EvaluationDocuments.Keys.Single(p => p.Contains("/runs/", StringComparison.Ordinal));
            var coordinatorKey = worker.EvaluationDocuments.Keys.Single(p => p.Contains("/council-preview/", StringComparison.Ordinal));
            if (corruption == "missing-run") worker.EvaluationDocuments.Remove(runKey);
            else
            {
                var key = corruption is "missing-proof" or "missing-dispatch" ? coordinatorKey : runKey;
                var body = JsonNode.Parse(worker.EvaluationDocuments[key])!;
                if (corruption == "missing-proof") body.AsObject().Remove("proof");
                if (corruption == "missing-dispatch") body["dispatches"]!.AsArray().RemoveAt(0);
                if (corruption == "wrong-profile") body["profile"] = "Default";
                if (corruption == "missing-juror") body["proposals"]![0]!["councilReview"]!["juryVerdicts"]!.AsArray().RemoveAt(0);
                if (corruption == "wrong-run-resource") body["_rid"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("recreated"));
                worker.EvaluationDocuments[key] = body.ToJsonString();
            }
            var writes = worker.EvaluationWrites.Count;
            var result = await worker.RunEvaluationAsync("/evaluation");
            Assert.Contains("Unknown", result.Body);
            Assert.DoesNotContain("ReadyAwaitingActivation", result.Body);
            Assert.Equal(writes, worker.EvaluationWrites.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_deadline_or_caller_cancellation_after_dispatch_is_never_known_failure_or_replay(bool caller)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        await transport.PrepareEvaluationAsync();
        var before = transport.Intent.Models.Inference.Count;
        using var cancel = new CancellationTokenSource();
        transport.ChangeModelResponse = text =>
        {
            if (transport.Intent.Models.Inference.Count > before)
            {
                if (caller) cancel.Cancel();
                else transport.Source.Clock.Now += TimeSpan.FromSeconds(181);
            }
            return text;
        };
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var env = transport.Source.Environment;
        env["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        try
        {
            var result = await RuntimeVerbs.ExecutePreviewAsync(FactoryHostSettings.Read(env, "Preview execution"),
                RuntimeDependencies.Production() with { PreviewHttp = http, PreviewCredential = new IntentModelCredential(transport.Source),
                    PreviewClock = transport.Source.Clock }, transport.Request, cancel.Token);
            Assert.False(caller);
            Assert.Equal(PreviewReadiness.Unknown, result.Readiness);
        }
        catch (OperationCanceledException) { Assert.True(caller); }
        var coordinator = JsonNode.Parse(transport.EvaluationDocuments.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Value)!;
        Assert.Equal("Unknown", (string?)Assert.Single(coordinator["dispatches"]!.AsArray())!["state"]);
        Assert.Null(coordinator["proof"]);
        Assert.Equal(before + 1, transport.Intent.Models.Inference.Count);
    }
}
