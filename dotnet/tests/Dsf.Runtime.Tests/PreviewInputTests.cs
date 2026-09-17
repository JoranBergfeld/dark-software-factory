using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PreviewInputTests
{
    [Theory]
    [InlineData("capability")]
    [InlineData("judgment")]
    [InlineData("adoption")]
    [InlineData("source")]
    [InlineData("model")]
    [InlineData("native-model")]
    [InlineData("repository")]
    public async Task Current_authority_is_rechecked_after_application_collection(string change)
    {
        using var transport = await CreateAsync();
        transport.OnTelemetryQuery = () =>
        {
            switch (change)
            {
                case "capability": transport.CapabilityETag = "replaced-current-revision"; break;
                case "judgment":
                    var capability = JsonNode.Parse(transport.Source.Values[ProductConfigurationKeys.FactoryPreview])!;
                    capability["configuration"]!["inputs"]!["judgment"]!["threshold"] = 0.8;
                    transport.Source.Values[ProductConfigurationKeys.FactoryPreview] = capability.ToJsonString(); break;
                case "adoption": transport.Source.Values.Remove(ProductConfigurationKeys.CharterAdopted); break;
                case "source": transport.Source.Values.Remove(ProductConfigurationKeys.FactorySource); break;
                case "model": transport.Source.Values.Remove(ProductConfigurationKeys.FactoryModels); break;
                case "native-model":
                    var target = FactoryModelsValidation.DeploymentTarget(transport.Models.Routes![0].Selection);
                    transport.Intent.Models.Resources[target]["properties"]!["model"]!["version"] = "foreign-version"; break;
                case "repository": transport.Source.Head = new string('f', 40); break;
            }
        };
        var result = await transport.RunInputsAsync();
        Assert.NotEmpty(transport.TelemetryQueries);
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("\"observations\"", result.Body);
        Assert.DoesNotContain("POST /shifts/confirm", result.Body);
    }

    [Fact]
    public async Task New_telemetry_rows_do_not_replace_still_fresh_captured_application_content()
    {
        using var transport = await CreateAsync();
        transport.ChangeTelemetryQueryResponse = body =>
        {
            if (transport.TelemetryQueries.Count > 1)
            {
                body["tables"]![0]!["rows"]![0]![2] = "application-record-2";
                body["tables"]![0]!["rows"]![0]![5] = "GET /newer-request";
            }
            return body;
        };
        var result = await transport.RunInputsAsync();
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var snapshot = JsonSerializer.Deserialize<PreviewInputSnapshot>(result.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal("application-record-1", snapshot.Telemetry.Capture.Rows[0].Reference.ItemId);
        Assert.Contains("POST /shifts/confirm", Assert.Single(snapshot.Observations).Summary);
        Assert.DoesNotContain("GET /newer-request", result.Body);
        Assert.Equal(snapshot.Telemetry.SourceAuthorityRevision, snapshot.Recheck.TelemetryAuthorityRevision);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("incomplete")]
    [InlineData("schema")]
    [InlineData("sensitive")]
    [InlineData("oversized")]
    [InlineData("identity")]
    [InlineData("request")]
    public async Task Aggregate_never_substitutes_for_unusable_required_application_evidence(string failure)
    {
        using var transport = await CreateAsync();
        if (failure == "identity") transport.TelemetryTokenIdentity = transport.Source.Code.PrincipalId;
        if (failure == "oversized") transport.TelemetryNativeResponse = request => request.RequestUri!.Host == "api.loganalytics.io"
            ? new(HttpStatusCode.OK) { Content = new PreviewTelemetryConsumerTests.CountingTelemetryContent() } : null;
        if (failure == "request") transport.ChangeTelemetryResponse = text =>
        {
            var body = JsonNode.Parse(text)!;
            body["requestId"] = Guid.NewGuid();
            return body.ToJsonString();
        };
        transport.ChangeTelemetryQueryResponse = body =>
        {
            var table = body["tables"]![0]!;
            switch (failure)
            {
                case "empty": table["rows"] = new JsonArray(); break;
                case "stale": table["rows"]![0]![0] = transport.Source.Clock.Now.AddDays(-2).ToString("O"); break;
                case "foreign": table["rows"]![0]![1] = FactoryTelemetryHostTests.TelemetryTransport.Component + "-foreign"; break;
                case "incomplete": body["error"] = new JsonObject { ["code"] = "PartialError", ["message"] = "private-provider-detail" }; break;
                case "schema": table["columns"]![5]!["name"] = "Properties"; break;
                case "sensitive": table["rows"]![0]![5] = "person@example.test"; break;
            }
            return body;
        };
        var result = await transport.RunInputsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("\"observations\"", result.Body);
        Assert.DoesNotContain("private-provider-detail", result.Body);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("partial")]
    [InlineData("denied")]
    [InlineData("orphan")]
    [InlineData("unreadable-history")]
    public async Task All_selected_sources_must_succeed_and_absent_selection_must_be_proven_pristine(string failure)
    {
        var selected = failure is not ("orphan" or "unreadable-history");
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true,
            supplementals: selected ? ["webiq", "foundryiq"] : null, aggregateInputs: true);
        if (failure == "orphan") transport.Source.Values[ProductConfigurationKeys.SupplementalOperation(Guid.NewGuid(), 0)] = "{}";
        if (failure == "unreadable-history") transport.SupplementalNamespaceResponse = _ => new(HttpStatusCode.Forbidden);
        if (failure == "null") transport.WebPayload = """{"webResults":null}""";
        if (failure == "partial") transport.WebPayload = """{"webResults":[],"partial":true}""";
        if (failure == "denied") transport.SupplementalNativeResponse = r => r.RequestUri!.Host == "api.microsoft.ai" ? new(HttpStatusCode.Forbidden) : null;
        var result = await transport.RunInputsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("\"observations\"", result.Body);
        Assert.DoesNotContain("NotSelected", result.Body);
        if (selected) Assert.Equal(new[] { "webiq", "foundryiq" }, transport.SupplementalQueries.Select(q => q.Kind));
        else Assert.Contains("Unknown", result.Body);
    }

    [Theory]
    [InlineData("council")]
    [InlineData("code")]
    [InlineData("foreign")]
    public async Task Only_the_approved_operator_can_request_aggregate_inputs(string caller)
    {
        using var transport = await CreateAsync();
        var principal = caller == "council" ? transport.Models.Bindings![0].PrincipalId!
            : caller == "code" ? transport.Source.Code.PrincipalId! : Guid.NewGuid().ToString();
        var result = await transport.RunInputsAsync(principal);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Contains("ApprovedPreviewCallerRequired", result.Body);
        Assert.Equal(0, transport.ContextCalls);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Theory]
    [InlineData("inputs", "ApprovedPreviewInputsRequired")]
    [InlineData("model", "ApprovedPreviewModelConsumerRequired")]
    [InlineData("telemetry", "PreviewTelemetryProjectionRequired")]
    public async Task Missing_approved_aggregate_prerequisites_fail_by_name(string missing, string reason)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: missing != "model",
            telemetryEvidence: missing != "telemetry", aggregateInputs: missing != "inputs");
        var result = await transport.RunInputsAsync();
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Contains(reason, result.Body);
        Assert.Equal(0, transport.ContextCalls);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Fact]
    public async Task Complete_snapshot_capacity_is_explicit_not_silently_truncated()
    {
        using var transport = await CreateAsync();
        transport.ChangeContextResponse = text =>
        {
            var body = JsonNode.Parse(text)!;
            body["context"]!["detail"] = new string('x', 1024 * 1024);
            return body.ToJsonString();
        };
        var result = await transport.RunInputsAsync();
        Assert.Contains("Partial", result.Body);
        Assert.Contains("CompletePreviewInputsExceedSnapshotBudget", result.Body);
        Assert.DoesNotContain("\"observations\"", result.Body);
    }

    private static Task<PreviewContextTransport> CreateAsync() =>
        PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);

    [Theory]
    [InlineData("reviewed")]
    [InlineData("threshold")]
    [InlineData("rounds")]
    [InlineData("lenses")]
    [InlineData("policy")]
    public async Task Judgment_and_limits_are_explicit_protected_inputs_not_ambient_defaults(string scenario)
    {
        using var transport = await CreateAsync();
        var inputs = new PreviewInputConfiguration(FactoryPreviewInputValidation.Policy,
            new(1, 0.7, [new("value", 2)]));
        inputs = scenario switch
        {
            "threshold" => inputs with { Judgment = inputs.Judgment with { Threshold = 1.1 } },
            "rounds" => inputs with { Judgment = inputs.Judgment with { Rounds = 0 } },
            "lenses" => inputs with { Judgment = inputs.Judgment with { Lenses = [] } },
            "policy" => inputs with { Policy = inputs.Policy with { MaximumObservations = 33 } },
            _ => inputs,
        };
        transport.Capability = transport.Capability with { Configuration = transport.Capability.Configuration with { Inputs = inputs } };
        transport.ReplaceBinding("runs", JsonSerializer.Deserialize<FoundationOperation>(
            transport.Source.Values[ProductConfigurationKeys.PreviewBinding(transport.Request.OperationId, "runs")], FactorySourceValidation.RecordJson)!);
        var result = await transport.RunInputsAsync();
        if (scenario == "reviewed")
        {
            Assert.True(result.Status == HttpStatusCode.OK, result.Body);
            var snapshot = JsonSerializer.Deserialize<PreviewInputSnapshot>(result.Body, FactorySourceValidation.RecordJson)!;
            Assert.Equal(1, snapshot.Context.Judgment.Rounds);
            Assert.Equal(0.7, snapshot.Context.Judgment.Threshold);
            Assert.Equal(new CouncilLensConfiguration("value", 2), Assert.Single(snapshot.Context.Judgment.Lenses));
            Assert.Equal(transport.Request.ApprovalFingerprint, snapshot.Capability.ApprovalFingerprint);
        }
        else
        {
            Assert.Contains("ApprovedPreviewInputPolicyAndJudgmentRequired", result.Body);
            Assert.NotEqual(HttpStatusCode.OK, result.Status);
            Assert.Equal(0, transport.ContextCalls);
        }
    }

    [Fact]
    public async Task Under_budget_HTTP_client_cannot_hide_a_valid_chained_request_behind_its_default_timeout()
    {
        using var transport = await CreateAsync();
        using var http = new HttpClient(transport, false);
        var env = transport.Source.Environment;
        env["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        var error = await Assert.ThrowsAsync<PreviewCapabilityException>(() =>
            RuntimeVerbs.AcquirePreviewInputsAsync(FactoryHostSettings.Read(env, "Preview inputs"),
                RuntimeDependencies.Production() with { PreviewHttp = http, PreviewCredential = new IntentModelCredential(transport.Source),
                    PreviewClock = transport.Source.Clock }, transport.Request, CancellationToken.None));
        Assert.Equal("PreviewHttpTimeoutMustCoverConsumerBudget", error.Message);
        Assert.Equal(0, transport.ContextCalls);
    }

    [Fact]
    public async Task Unconfirmed_native_source_progress_remains_Unknown_without_an_aggregate()
    {
        using var transport = await CreateAsync();
        transport.Source.BeforeRequestAsync = (request, _) => request.RequestUri!.Host == new Uri(transport.Source.ProgressEndpoint).Host
            ? Task.FromException(new HttpRequestException("private-provider-detail")) : Task.CompletedTask;
        var result = await transport.RunInputsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.DoesNotContain("private-provider-detail", result.Body);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Fact]
    public async Task Source_analysis_response_must_remain_the_exact_fetched_snapshot()
    {
        using var transport = await CreateAsync();
        transport.ChangeContextResponse = text =>
        {
            var body = JsonNode.Parse(text)!;
            if (transport.ContextCalls == 2)
                body["context"]!["current"]!["analysis"]!["limitations"]!.AsArray().Add("New current uncertainty observed.");
            return body.ToJsonString();
        };
        var result = await transport.RunInputsAsync();
        Assert.Equal(2, transport.ContextCalls);
        Assert.Contains("CurrentAdoptionOrAnalyzedSourceChangedDuringCollection", result.Body);
        Assert.DoesNotContain("\"observations\"", result.Body);
    }

    [Fact]
    public async Task Aggregate_deadline_is_Unknown_not_an_operator_cancellation_or_success()
    {
        using var transport = await CreateAsync();
        transport.BeforeRequest = request =>
        {
            if (request.RequestUri!.Host == new Uri(transport.ContextEndpoint).Host)
                transport.Source.Clock.Now += TimeSpan.FromSeconds(1801);
        };
        var result = await transport.RunInputsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.Contains("PreviewInputCollectionDeadlineExceeded", result.Body);
    }

    [Fact]
    public async Task Legitimate_chained_source_reads_are_not_limited_by_the_old_whole_request_deadline()
    {
        using var transport = await CreateAsync();
        var start = transport.Source.Clock.Now;
        transport.ChangeContextResponse = text =>
        {
            transport.Source.Clock.Now += TimeSpan.FromSeconds(110);
            return text;
        };
        var result = await transport.RunInputsAsync();
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        Assert.Equal(TimeSpan.FromSeconds(220), transport.Source.Clock.Now - start);
        var snapshot = JsonSerializer.Deserialize<PreviewInputSnapshot>(result.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(start, snapshot.Source.Context.Current!.Source.Snapshot.ObservedAt);
    }

    [Fact]
    public async Task An_unresponsive_source_context_has_its_own_consumer_budget_before_other_collection()
    {
        using var transport = await CreateAsync();
        transport.ChangeContextResponse = text =>
        {
            transport.Source.Clock.Now += TimeSpan.FromSeconds(181);
            return text;
        };
        var result = await transport.RunInputsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Empty(transport.TelemetryQueries);
        Assert.Equal(1, transport.ContextCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combined_observation_limit_includes_all_sources_without_dropping_any(bool overflow)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true,
            supplementals: ["foundryiq", "webiq"], aggregateInputs: true);
        transport.WebPayload = JsonSerializer.Serialize(new { webResults = Enumerable.Range(0, 16)
            .Select(i => new { url = "https://provider.example/document/" + i, content = "Public accessibility guidance " + i }).ToArray() });
        var count = overflow ? 16 : 15;
        transport.SupplementalNativeResponse = request =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.Host != "product-search.search.windows.net") return null;
            return SourceInventoryTransport.Json(new
            {
                response = new[] { new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(Enumerable.Range(0, count)
                    .Select(i => new { ref_id = i.ToString(), guidance = "Product accessibility guidance " + i })) } } } },
                activity = new[] { new { id = 1, type = "searchIndex", knowledgeSourceName = "product-docs", count } },
                references = Enumerable.Range(0, count).Select(i => new { id = i.ToString(), type = "searchIndex", activitySource = 1, docKey = "document-" + i }).ToArray(),
            });
        };
        var result = await transport.RunInputsAsync();
        if (overflow)
        {
            Assert.Contains("Partial", result.Body);
            Assert.Contains("CompletePreviewEvidenceExceedsEvaluationBudget", result.Body);
            Assert.DoesNotContain("\"observations\"", result.Body);
        }
        else
        {
            Assert.True(result.Status == HttpStatusCode.OK, result.Body);
            var snapshot = JsonSerializer.Deserialize<PreviewInputSnapshot>(result.Body, FactorySourceValidation.RecordJson)!;
            Assert.Equal(32, snapshot.Observations.Count);
            Assert.Equal(32, snapshot.Context.Evidence.Count);
        }
        Assert.Equal(4, transport.SupplementalQueries.Count);
    }

    [Fact]
    public async Task Original_evidence_expiring_during_final_source_checks_is_not_refreshed_or_returned()
    {
        using var transport = await CreateAsync();
        transport.ChangeContextResponse = text =>
        {
            if (transport.ContextCalls == 2) transport.Source.Clock.Now += TimeSpan.FromSeconds(181);
            return text;
        };
        var result = await transport.RunInputsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("\"observations\"", result.Body);
        Assert.Equal(2, transport.TelemetryQueries.Count);
    }

    [Fact]
    public async Task Independent_worker_reconstructs_inputs_and_exposes_only_the_real_bound_model_consumer()
    {
        using var first = await CreateAsync();
        var initial = await first.RunInputsAsync();
        Assert.True(initial.Status == HttpStatusCode.OK, initial.Body);
        var captured = JsonSerializer.Deserialize<PreviewInputSnapshot>(initial.Body, FactorySourceValidation.RecordJson)!;
        using var worker = await PreviewContextTransport.ReconstructAsync(first);
        using var http = new HttpClient(worker, false) { Timeout = Timeout.InfiniteTimeSpan };
        var env = worker.Source.Environment;
        env["AZURE_CLIENT_ID"] = worker.Models.Bindings![0].ClientId;
        var settings = FactoryHostSettings.Read(env, "Preview inputs");
        var dependencies = RuntimeDependencies.Production() with { PreviewHttp = http, PreviewCredential = new IntentModelCredential(worker.Source),
            PreviewClock = worker.Source.Clock };
        var inference = worker.Intent.Models.Inference.Count;
        var acquired = await RuntimeVerbs.AcquirePreviewInputsAsync(settings, dependencies, worker.Request, CancellationToken.None);
        Assert.Equal(captured.CapabilityRevision, acquired.Snapshot.CapabilityRevision);
        Assert.Equal(captured.Context.Adoption, acquired.Snapshot.Context.Adoption);
        Assert.Equal(captured.Context.Intent.Analysis.Progress!.ChainSha256, acquired.Snapshot.Context.Intent.Analysis.Progress!.ChainSha256);
        Assert.Equal(captured.Observations, acquired.Snapshot.Observations);
        Assert.NotEqual(captured.Request.RequestId, acquired.Snapshot.Request.RequestId);
        Assert.Equal(2, acquired.Snapshot.ModelObservations.Count);
        Assert.IsAssignableFrom<IFactoryModelWorkloads>(acquired.Models);
        var modelAuthority = await acquired.Models.ReadAuthorityAsync(CancellationToken.None);
        Assert.Equal(new[] { "synthesis", "embedding", "juror-1", "juror-2", "juror-3" }, modelAuthority.Routes.Select(r => r.Route));
        Assert.Equal(inference, worker.Intent.Models.Inference.Count);
        Assert.All(acquired.Models.Observations, o => Assert.Null(o.Output));
        Assert.Equal(2, acquired.Snapshot.ModelObservations.Count);
        Assert.Equal(acquired.Snapshot.Revision, acquired.Snapshot.CalculateRevision());
        var serialized = JsonSerializer.SerializeToNode(acquired, new JsonSerializerOptions(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false })!;
        Assert.NotNull(serialized["snapshot"]);
        Assert.False(serialized.AsObject().ContainsKey("models"));
        Assert.DoesNotContain(SourceInventoryTransport.InstallationToken, JsonSerializer.Serialize(acquired, FactoryGitHubJson.Options));
        worker.Source.Values.Remove(ProductConfigurationKeys.FactoryModels);
        await Assert.ThrowsAsync<PreviewCapabilityException>(() => acquired.Models.ReadAuthorityAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_cancellation_and_unconfirmed_transport_remain_distinct(bool canceled)
    {
        using var transport = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        if (canceled) transport.OnTelemetryQuery = () => cancellation.Cancel();
        else transport.TelemetryNativeResponse = request => request.RequestUri!.Host == "api.loganalytics.io"
            ? throw new OperationCanceledException() : null;
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var env = transport.Source.Environment;
        env["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        var error = await Assert.ThrowsAsync<PreviewCapabilityException>(() =>
            RuntimeVerbs.AcquirePreviewInputsAsync(FactoryHostSettings.Read(env, "Preview inputs"),
                RuntimeDependencies.Production() with { PreviewHttp = http, PreviewCredential = new IntentModelCredential(transport.Source),
                    PreviewClock = transport.Source.Clock }, transport.Request, cancellation.Token));
        Assert.Equal(canceled ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, error.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_selected_source_is_captured_with_original_proofs_even_when_empty(bool content)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true,
            supplementals: ["foundryiq", "webiq"], aggregateInputs: true);
        if (content)
        {
            transport.FoundryScenario = "evidence";
            transport.WebPayload = """{"webResults":[{"url":"https://provider.example/private-provider-detail","content":"Public checkout keyboard navigation guidance."}]}""";
        }
        var result = await transport.RunInputsAsync();
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var snapshot = JsonSerializer.Deserialize<PreviewInputSnapshot>(result.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(content ? 3 : 1, snapshot.Observations.Count);
        Assert.Equal(new[] { "foundryiq", "webiq" }, snapshot.Supplemental.Sources.Select(s => s.Evidence.Proof.Kind));
        Assert.Equal(2, snapshot.SupplementalConfiguration!.Hosts.Count);
        Assert.Equal(new[] { "foundryiq", "webiq", "foundryiq", "webiq" }, transport.SupplementalQueries.Select(q => q.Kind));
        Assert.All(snapshot.Supplemental.Sources, s => Assert.Equal(snapshot.Request.RequestId, s.Authority.Request.Preview.RequestId));
        if (content)
        {
            Assert.Contains("Public keyboard accessibility.", snapshot.Observations[1].Summary);
            Assert.Equal("Public checkout keyboard navigation guidance.", snapshot.Observations[2].Summary);
            Assert.Matches("^webiq:sha256:[0-9a-f]{64}$", snapshot.Observations[2].Reference);
            Assert.Equal(snapshot.Supplemental.Sources[1].Evidence.Items[0].Reference, snapshot.Observations[2].Reference);
        }
        else Assert.All(snapshot.Supplemental.Sources, s => Assert.Equal(SupplementalReason.Empty, s.Evidence.Proof.Reason));
        Assert.DoesNotContain("private-provider-detail", result.Body);
        Assert.All(transport.SupplementalQueries.Where(q => q.Kind == "webiq"),
            q => Assert.DoesNotContain("coordinate volunteer shifts", q.Body));
    }

    [Theory]
    [InlineData("chunks")]
    [InlineData("claims")]
    [InlineData("foreign-analysis")]
    [InlineData("partial-chunk")]
    public async Task Complete_current_source_context_is_required_not_just_a_DraftReady_flag(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        transport.ChangeContextResponse = text =>
        {
            var body = JsonNode.Parse(text)!;
            var analysis = body["context"]!["current"]!["analysis"]!;
            if (change is "chunks" or "claims") analysis[change] = new JsonArray();
            if (change == "foreign-analysis") analysis["progress"]!["snapshot"]!["factoryId"] = Guid.NewGuid().ToString();
            if (change == "partial-chunk") analysis["chunks"]![0]!["candidate"]!["complete"] = false;
            return body.ToJsonString();
        };
        var result = await transport.RunInputsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("\"context\"", result.Body);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Fact]
    public async Task Operator_captures_current_adopted_intent_and_one_actual_Azure_record_without_evaluation_or_writes()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, aggregateInputs: true);
        var records = transport.Source.Values.ToDictionary(p => p.Key, p => p.Value);
        var result = await transport.RunInputsAsync();
        Assert.Equal(records, transport.Source.Values);
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var body = JsonNode.Parse(result.Body)!;
        Assert.Equal("Captured", (string?)body["state"]);
        Assert.Contains("coordinate volunteer shifts", (string)body["context"]!["intent"]!["source"]!["content"]!);
        Assert.Single(body["observations"]!.AsArray());
        Assert.Contains("POST /shifts/confirm", (string)body["observations"]![0]!["summary"]!);
        Assert.Equal("NotSelected", (string?)body["supplemental"]!["state"]);
        Assert.DoesNotContain("\"completion\"", result.Body);
        Assert.DoesNotContain("secret-key-do-not-print", result.Body);
        var snapshot = JsonSerializer.Deserialize<PreviewInputSnapshot>(result.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(snapshot.Revision, snapshot.CalculateRevision());
        Assert.NotEqual(snapshot.Revision, (snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(1) }).CalculateRevision());
        Assert.Equal(2, snapshot.Context.Judgment.Rounds);
        Assert.Equal(0.6, snapshot.Context.Judgment.Threshold);
        Assert.Equal(transport.Capability.Configuration.Inputs!.Judgment.Lenses, snapshot.Context.Judgment.Lenses);
        Assert.Equal(new[] { "synthesis", "embedding", "juror-1", "juror-2", "juror-3" }, snapshot.Context.Models.Routes.Select(r => r.Name));
        Assert.Equal(snapshot.Context.Adoption.Revision, snapshot.Source.Context.Adoption!.Adoption.Revision);
        Assert.Equal(snapshot.Context.Intent.Analysis.Progress!.Revision, snapshot.Source.Context.Current!.Analysis.Progress!.Revision);
        Assert.NotEmpty(snapshot.Context.Intent.Analysis.Limitations);
        Assert.Equal(snapshot.Observations[0].Reference, snapshot.Context.Evidence[0].Reference);
        Assert.Equal(FactoryModelsValidation.Hash(snapshot.Observations[0].Summary), snapshot.Context.Evidence[0].ContentSha256);
        Assert.Equal($"input:{snapshot.Request.RequestId:D}#/telemetry/capture/rows/0", snapshot.Context.Evidence[0].ObservationReference);
        Assert.All(snapshot.ModelObservations, o => Assert.Null(o.Output));
        Assert.Equal(2, transport.ContextCalls);
        Assert.Equal(2, transport.TelemetryQueries.Count);
        Assert.Empty(transport.SupplementalQueries);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host.Contains("preview-webiq", StringComparison.Ordinal)
            || r.Uri.Host.Contains("preview-foundryiq", StringComparison.Ordinal));
    }
}

internal sealed partial class PreviewContextTransport
{
    internal async Task<PreviewContextRunner> RunInputsAsync(string? caller = null)
    {
        var writes = Source.Writes.Count;
        var inference = Intent.Models.Inference.Count;
        var progress = Source.Progress.ToDictionary(p => p.Key, p => p.Value);
        var runner = new PreviewContextRunner(Source) { Route = "/inputs", Caller = caller ?? Source.Manifest.Identity.AzureActorId,
            Client = Models.Bindings![0].ClientId!, Request = Request };
        using var http = new HttpClient(this, false) { Timeout = Timeout.InfiniteTimeSpan };
        var env = Source.Environment;
        env["AZURE_CLIENT_ID"] = Models.Bindings![0].ClientId;
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-preview"], env, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, PreviewHttp = http,
                PreviewCredential = new IntentModelCredential(Source), PreviewClock = Source.Clock }, CancellationToken.None));
        Assert.Equal(writes, Source.Writes.Count);
        Assert.Equal(inference, Intent.Models.Inference.Count);
        Assert.Equal(progress, Source.Progress);
        Assert.DoesNotContain(Requests, r => r.Method is "PUT" or "PATCH" or "DELETE");
        return runner;
    }
}
