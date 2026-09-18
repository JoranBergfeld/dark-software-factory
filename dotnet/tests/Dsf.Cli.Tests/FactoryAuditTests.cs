using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryAuditTests
{
    [Fact]
    public async Task Interrupted_first_adoption_cannot_certify_missing_declared_payload_references()
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        scenario.Arm.Product.AfterPut = (key, value) =>
        {
            if (key == ProductConfigurationKeys.CharterAdoptionOperation)
                scenario.Arm.Product.RejectKey = ProductConfigurationKeys.CharterAdoptionPayload(
                    (Guid)JsonNode.Parse(value)!["adoption"]!["revision"]!, 0);
        };
        Assert.Equal(1, (await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing",
            "--approve", (string)review.Result["revision"]!)).Exit);
        Assert.DoesNotContain(ProductConfigurationKeys.CharterAdopted,
            JsonNode.Parse(scenario.Arm.Product.ListJson())!.AsArray().Select(r => (string)r!["key"]!));
        using var transport = new AuditTransport(scenario);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var inspected = await scenario.Machine.InvokeAsync("audit");
        Assert.Equal(1, inspected.Exit);
        Assert.Null(inspected.Result["receipt"]);
    }

    [Fact]
    public async Task Uncertain_subsequent_preview_checkpoint_cannot_certify_prior_run_as_complete_closure()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var lost = false;
        transport.AfterEvaluationWrite = body =>
        {
            if (lost || body["run"] is null || body["pendingRun"] is null) return;
            lost = true;
            throw new HttpRequestException("Synthetic lost pending-checkpoint acknowledgment");
        };
        var evaluated = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True(lost);
        Assert.NotEqual("ReadyAwaitingActivation", (string?)evaluated.Result["readiness"]);
        using var audit = new PreviewAuditTransport(transport);
        transport.Scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(audit);
        var inspected = await transport.Scenario.Machine.InvokeAsync("audit");
        Assert.Equal(1, inspected.Exit);
        Assert.Null(inspected.Result["receipt"]);
    }

    [Theory]
    [InlineData("public-url")]
    [InlineData("missing-locators")]
    [InlineData("conflicting")]
    public async Task Redirected_audit_rejects_incomplete_or_unsafe_input_before_provider_access(string mode)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        var extra = mode switch
        {
            "public-url" => new[] { "--export", "https://github.com/public/export" },
            "conflicting" => ["--approve-policy", "--verify-export", "/private/export"],
            _ => [],
        };
        var exit = await CliApplication.InvokeAsync(["onboard", "decide", "audit", "--json", .. extra], CancellationToken.None, terminal);
        Assert.Equal(1, exit);
        Assert.Contains("Blocked", terminal.Output);
        Assert.Empty(terminal.Prompts);
    }

    [Theory]
    [InlineData("chunk")]
    [InlineData("model-proof")]
    [InlineData("source-proof")]
    [InlineData("secret")]
    [InlineData("oversized")]
    public async Task Missing_or_sensitive_required_evidence_never_yields_successful_inspection_or_export(string damage)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        using var transport = new AuditTransport(scenario);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var source = scenario.Source.Source!;
        if (damage == "chunk") source.Progress.Remove(source.Progress.Keys.First(k => k.Contains("/steps/", StringComparison.Ordinal)));
        if (damage == "source-proof") source.Progress.Remove(source.Progress.Keys.Single(k => k.Contains("/integrity/", StringComparison.Ordinal)));
        if (damage == "model-proof")
        {
            var models = JsonNode.Parse(scenario.Source.Values()[ProductConfigurationKeys.FactoryModels])!;
            scenario.Arm.Product.Remove((string)models["lastProofReference"]!);
        }
        if (damage == "secret")
            scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryModels, value => value.Replace("observed",
                "ghp_DO_NOT_EXPORT_THIS_SYNTHETIC_TOKEN", StringComparison.Ordinal));
        if (damage == "oversized")
        {
            var original = source.Progress["intent/current"];
            source.Progress["intent/current"] = (original.Value + new string(' ', 11000), original.Revision);
        }
        var result = await scenario.Machine.InvokeAsync("audit");
        Assert.Equal(1, result.Exit);
        Assert.Null(result.Result["receipt"]);
        Assert.DoesNotContain("DO_NOT_EXPORT", result.Terminal.Output);
        Assert.Empty(transport.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_or_denied_delete_keeps_inspectable_disposition_and_forbids_replay(bool lost)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var revision = (Guid)JsonNode.Parse(scenario.Source.Source!.Progress["intent/current"].Value)!["revision"]!;
        Assert.Equal(1, (await scenario.Machine.InvokeAsync("infer", "--new-analysis")).Exit);
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("infer")).Exit);
        scenario.Source.Source.Clock.Now += TimeSpan.FromDays(90);
        scenario.Machine.Boundary.Repository.ReservationClock = scenario.Source.Source.Clock;
        using var transport = new AuditTransport(scenario) { DeleteFailure = lost ? "lost" : "denied" };
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var policy = await scenario.Machine.InvokeAsync("audit", "--approve-policy");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit", "--approve-policy",
            "--approve", (string)policy.Result["approvalFingerprint"]!)).Exit);
        var review = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString());
        var result = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString(),
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal(lost ? "Unknown" : "Blocked", (string?)result.Result["state"]);
        Assert.Single(transport.Deletes);
        scenario.Reconstruct();
        var retained = scenario.Arm.Product.Export();
        var status = await scenario.Machine.InvokeAsync("audit");
        Assert.Equal("Partial", (string?)status.Result["state"]);
        Assert.Contains(status.Result["records"]!.AsArray(), record =>
            ((string)record!["content"]!).Contains(lost ? "Unknown" : "KnownNoEffect", StringComparison.Ordinal));
        Assert.Equal(retained, scenario.Arm.Product.Export());
        var retry = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString(),
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal("Partial", (string?)retry.Result["state"]);
        Assert.Single(transport.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staged_adoption_and_expiry_compete_on_one_native_protected_revision_claim(bool protectionWins)
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        var revision = (Guid)review.Result["review"]!["analysisRevision"]!;
        var key = Dsf.Core.Onboarding.FactoryAuditRetention.ClaimKey(revision);
        var adoptionWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiryWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdoption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExpiry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        scenario.Arm.Product.BeforePut = async (target, create) =>
        {
            if (target != key || !create) return;
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                adoptionWaiting.SetResult();
                await releaseAdoption.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
            else
            {
                expiryWaiting.SetResult();
                await releaseExpiry.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        };
        var adoption = scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)review.Result["revision"]!);
        try
        {
            await adoptionWaiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var refreshed = await scenario.Machine.InvokeAsync("infer", "--new-analysis");
            for (var i = 0; i < 8 && refreshed.Exit != 0; i++) refreshed = await scenario.Machine.InvokeAsync("infer");
            Assert.Equal(0, refreshed.Exit);
            scenario.Source.Source!.Clock.Now += TimeSpan.FromDays(90);
            scenario.Machine.Boundary.Repository.ReservationClock = scenario.Source.Source.Clock;
            using var transport = new AuditTransport(scenario);
            scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
            var policy = await scenario.Machine.InvokeAsync("audit", "--approve-policy");
            Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit", "--approve-policy",
                "--approve", (string)policy.Result["approvalFingerprint"]!)).Exit);
            var expiryReview = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString());
            Assert.True(expiryReview.Exit == 0, expiryReview.Terminal.Output);
            var expiry = scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString(),
                "--approve", (string)expiryReview.Result["approvalFingerprint"]!);
            await expiryWaiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
            if (protectionWins)
            {
                releaseAdoption.SetResult();
                await adoption;
                releaseExpiry.SetResult();
            }
            else
            {
                releaseExpiry.SetResult();
                Assert.Equal(0, (await expiry).Exit);
                releaseAdoption.SetResult();
            }
            var result = await expiry;
            await adoption;
            Assert.Equal(protectionWins, result.Exit != 0);
            Assert.Equal(protectionWins, transport.Deletes.Count == 0);
            Assert.Empty(scenario.GitHubWrites);
        }
        finally
        {
            releaseAdoption.TrySetResult();
            releaseExpiry.TrySetResult();
            await adoption;
        }
    }

    [Fact]
    public async Task Retained_native_preview_run_is_included_and_missing_run_blocks_export_review()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var machine = transport.Scenario.Machine;
        var capability = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryPreview])!;
        var evaluation = await machine.InvokeAsync("preview", "--evaluate", "--operation", (string)capability["operationId"]!,
            "--approve", (string)capability["approvalFingerprint"]!);
        Assert.True(evaluation.Exit == 0, evaluation.Terminal.Output);
        using var auditTransport = new PreviewAuditTransport(transport);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(auditTransport);
        var inspected = await machine.InvokeAsync("audit");
        Assert.True(inspected.Exit == 0, inspected.Terminal.Output);
        var records = inspected.Result["records"]!.AsArray().ToList();
        var page = inspected.Result["nextPage"];
        while (page is not null)
        {
            var next = await machine.InvokeAsync("audit", "--page", ((int)page).ToString());
            Assert.Equal(0, next.Exit);
            records.AddRange(next.Result["records"]!.AsArray());
            page = next.Result["nextPage"];
        }
        Assert.Contains(records, record => (string?)record!["identity"]!["store"] == "Preview");
        Assert.Contains(records, record => (string?)record!["identity"]!["store"] == "Run");
        var run = transport.SeedDocuments.Keys.Single(k => k.Contains("/runs/", StringComparison.Ordinal));
        transport.SeedDocuments.Remove(run);
        var missing = await machine.InvokeAsync("audit");
        Assert.Equal("Unknown", (string?)missing.Result["state"]);
        Assert.Null(missing.Result["receipt"]);
    }

    private sealed class PreviewAuditTransport(PreviewPreparationTransport transport) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var source = transport.Scenario.Source.Source!;
            using var native = new HttpMessageInvoker(request.RequestUri!.Host == new Uri(source.ProgressEndpoint).Host ? source : transport, false);
            return await native.SendAsync(request, token);
        }
    }

    [Fact]
    public async Task Preview_retains_exact_analysis_in_code_store_before_capture_without_governance_writes()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var source = transport.Scenario.Source.Source!;
        var revision = (Guid)JsonNode.Parse(source.Progress["intent/current"].Value)!["revision"]!;
        var key = $"intent/{revision:N}/retention";
        var governance = transport.Scenario.Arm.Product.Export();
        var evaluated = await transport.Scenario.Machine.InvokeAsync("preview",
            ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True(evaluated.Exit == 0, evaluated.Terminal.Output);
        Assert.True(source.Progress.ContainsKey(key), "Captured preview must retain its exact analysis through the CODE store interlock.");
        var retained = JsonNode.Parse(source.Progress[key].Value)!;
        Assert.Equal("Protected", (string?)retained["kind"]);
        Assert.Equal(revision, (Guid)retained["revision"]!);
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        var progress = System.Text.Json.JsonSerializer.Serialize(source.Progress);
        Assert.Equal(0, (await transport.Scenario.Machine.InvokeAsync("preview",
            ["--evaluation", .. PreviewEvaluationTests.Pins(transport)])).Exit);
        Assert.Equal(progress, System.Text.Json.JsonSerializer.Serialize(source.Progress));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_preview_reference_and_operator_expiry_compete_on_exact_native_analysis_interlock(bool runtimeWins)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var scenario = transport.Scenario;
        await Refresh();
        var source = scenario.Source.Source!;
        var revision = (Guid)JsonNode.Parse(source.Progress["intent/current"].Value)!["revision"]!;
        var key = $"intent/{revision:N}/retention";
        var runtimeWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiryWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRuntime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExpiry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BeforeProgressWrite = async (target, value) =>
        {
            if (target != key) return;
            if ((string?)JsonNode.Parse(value)!["kind"] == "Protected")
            {
                runtimeWaiting.TrySetResult();
                await releaseRuntime.Task.WaitAsync(TimeSpan.FromSeconds(90));
            }
            else
            {
                expiryWaiting.TrySetResult();
                await releaseExpiry.Task.WaitAsync(TimeSpan.FromSeconds(90));
            }
        };
        source.AfterWrite = (target, _) => { if (target == key) runtimeCommitted.TrySetResult(); };
        var preview = Evaluate();
        try
        {
            var arrived = await Task.WhenAny(runtimeWaiting.Task, preview).WaitAsync(TimeSpan.FromSeconds(90));
            if (arrived != runtimeWaiting.Task)
                Assert.Fail((await preview).ToJsonString() + "\n" + string.Join("\n", transport.HostFailures));
            await Refresh();
            // An independently delayed native request cannot be fenced by the operator's elapsed time.
            scenario.Machine.Boundary.Repository.ReservationClock = new Dsf.Runtime.Tests.SourceTestClock { Now = source.Clock.Now.AddDays(90) };
            using var audit = new AuditTransport(scenario, transport);
            scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(audit);
            var policy = await scenario.Machine.InvokeAsync("audit", "--approve-policy");
            Assert.True(policy.Exit == 0, policy.Terminal.Output);
            Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit", "--approve-policy",
                "--approve", (string)policy.Result["approvalFingerprint"]!)).Exit);
            var review = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString());
            Assert.True(review.Exit == 0, review.Terminal.Output);
            var expiry = scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString(),
                "--approve", (string)review.Result["approvalFingerprint"]!);
            await expiryWaiting.Task.WaitAsync(TimeSpan.FromSeconds(90));
            if (runtimeWins)
            {
                releaseRuntime.SetResult();
                await runtimeCommitted.Task.WaitAsync(TimeSpan.FromSeconds(90));
                releaseExpiry.SetResult();
            }
            else
            {
                releaseExpiry.SetResult();
                Assert.Equal(0, (await expiry).Exit);
                releaseRuntime.SetResult();
            }
            var expired = await expiry;
            Assert.Equal(runtimeWins ? "Blocked" : "Expired", (string?)expired.Result["state"]);
            Assert.Equal(runtimeWins, audit.Deletes.Count == 0);
            Assert.NotEqual("ReadyAwaitingActivation", (string?)(await preview)["readiness"]);
            Assert.Empty(transport.EvaluationInference);
            Assert.All(transport.SeedDocuments.Where(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)),
                document => Assert.Null(JsonNode.Parse(document.Value)!["inputs"]));
            var inspected = await scenario.Machine.InvokeAsync("audit");
            Assert.True(inspected.Exit == 0, inspected.Terminal.Output);
        }
        finally
        {
            releaseRuntime.TrySetResult();
            releaseExpiry.TrySetResult();
            await preview;
        }

        async Task Refresh()
        {
            var refreshed = await scenario.Machine.InvokeAsync("infer", "--new-analysis");
            for (var i = 0; i < 8 && refreshed.Exit != 0; i++) refreshed = await scenario.Machine.InvokeAsync("infer");
            Assert.True(refreshed.Exit == 0, refreshed.Terminal.Output);
        }

        async Task<JsonNode> Evaluate()
        {
            var request = new Dsf.Core.Onboarding.PreviewCapabilityRequest(transport.Manifest.PreviewOperationId!.Value,
                transport.Manifest.PreviewApprovalFingerprint!, Guid.NewGuid());
            var result = await transport.InspectEvaluationAsync(request, "/evaluate");
            while ((string?)result["readiness"] is "Queued" or "Running" or "Checking")
            {
                await Task.Delay(10);
                result = await transport.InspectEvaluationAsync(request);
            }
            return result;
        }
    }

    [Fact]
    public async Task Bounded_audit_history_does_not_exhaust_unrelated_preview_authority_budget()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var persisted = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(transport.Scenario.Arm.Product.Export())!;
        var template = JsonNode.Parse(persisted[ProductConfigurationKeys.FactoryManifest])!;
        for (var i = 0; i < 1100; i++)
        {
            var key = $"dsf/factory/audit/analysis/{i:D4}/effects/0000";
            var record = template.DeepClone();
            record["key"] = key;
            record["value"] = "{}";
            record["etag"] = "audit-" + i;
            persisted.Add(key, record.ToJsonString());
        }
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(System.Text.Json.JsonSerializer.Serialize(persisted));
        var review = await transport.Scenario.Machine.InvokeAsync("preview", "--configure");
        Assert.True(review.Exit == 0, review.Terminal.Output);
    }

    [Fact]
    public async Task Lost_runtime_retention_acknowledgment_preserves_hold_without_capturing_or_evaluating()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var source = transport.Scenario.Source.Source!;
        var revision = (Guid)JsonNode.Parse(source.Progress["intent/current"].Value)!["revision"]!;
        var key = $"intent/{revision:N}/retention";
        source.AfterWrite = (target, _) =>
        {
            if (target == key) throw new HttpRequestException("Synthetic lost retention acknowledgment");
        };
        var preview = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.Equal("Unknown", (string?)preview.Result["readiness"]);
        Assert.True(source.Progress.ContainsKey(key));
        Assert.Empty(transport.EvaluationInference);
        Assert.All(transport.SeedDocuments.Where(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)),
            document => Assert.Null(JsonNode.Parse(document.Value)!["inputs"]));
        var progress = System.Text.Json.JsonSerializer.Serialize(source.Progress);
        await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.Equal(progress, System.Text.Json.JsonSerializer.Serialize(source.Progress));
    }

    [Theory]
    [InlineData("partial", "Partial")]
    [InlineData("adoption", "Unknown")]
    public async Task Retention_reports_expected_context_failures_as_structured_outcomes(string fault, string expected)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var source = transport.Scenario.Source.Source!;
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<System.Net.HttpStatusCode>();
        var changed = false;
        transport.BeforeIngressRequest = async uri =>
        {
            if (uri.AbsolutePath != "/retain-council-context" || changed) return;
            changed = true;
            if (fault == "partial")
                Assert.Equal("Partial", (string?)(await transport.Scenario.Machine.InvokeAsync("infer", "--new-analysis")).Result["state"]);
            else
            {
                var adoption = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.CharterAdopted])!;
                transport.Scenario.Arm.Product.RejectReadKey =
                    ProductConfigurationKeys.CharterAdoptionPayload((Guid)adoption["revision"]!, 0);
            }
        };
        transport.AfterIngressResponse = (path, status) =>
        {
            if (path == "/retain-council-context") statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        var preview = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True(changed);
        Assert.Equal(expected, (string?)preview.Result["readiness"]);
        Assert.Contains(System.Net.HttpStatusCode.Conflict, statuses);
        Assert.DoesNotContain(System.Net.HttpStatusCode.InternalServerError, statuses);
        Assert.Empty(transport.EvaluationInference);
        Assert.DoesNotContain(source.Progress.Keys, key => key.EndsWith("/retention", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changed_native_retention_interlock_stops_expiry_before_any_delete()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var source = scenario.Source.Source!;
        var revision = (Guid)JsonNode.Parse(source.Progress["intent/current"].Value)!["revision"]!;
        Assert.Equal(1, (await scenario.Machine.InvokeAsync("infer", "--new-analysis")).Exit);
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("infer")).Exit);
        source.Clock.Now += TimeSpan.FromDays(90);
        scenario.Machine.Boundary.Repository.ReservationClock = source.Clock;
        using var transport = new AuditTransport(scenario);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var policy = await scenario.Machine.InvokeAsync("audit", "--approve-policy");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit", "--approve-policy",
            "--approve", (string)policy.Result["approvalFingerprint"]!)).Exit);
        var review = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString());
        scenario.Arm.Product.BeforePut = (target, create) =>
        {
            if (create && target == Dsf.Core.Onboarding.FactoryAuditRetention.EffectKey(revision, 0))
            {
                var key = $"intent/{revision:N}/retention";
                var original = source.Progress[key];
                var changed = JsonNode.Parse(original.Value)!;
                changed["kind"] = "Protected";
                source.Progress[key] = (changed.ToJsonString(), original.Revision + 1000);
            }
            return Task.CompletedTask;
        };
        var expired = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString(),
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal("Unknown", (string?)expired.Result["state"]);
        Assert.Empty(transport.Deletes);
    }

    [Fact]
    public async Task Prepared_preview_protects_only_referenced_analysis_and_allows_unused_historical_expiry()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var scenario = transport.Scenario;
        for (var revision = 0; revision < 2; revision++)
        {
            var refreshed = await scenario.Machine.InvokeAsync("infer", "--new-analysis");
            for (var i = 0; i < 8 && refreshed.Exit != 0; i++) refreshed = await scenario.Machine.InvokeAsync("infer");
            Assert.True(refreshed.Exit == 0, refreshed.Terminal.Output);
        }
        var source = scenario.Source.Source!;
        var unusedProgress = JsonNode.Parse(source.Progress["intent/history/02"].Value)!;
        var unused = (Guid)unusedProgress["revision"]!;
        var records = (int)unusedProgress["analyzedChunks"]! + (int)unusedProgress["checkedChunks"]!;
        source.Clock.Now += TimeSpan.FromDays(90);
        using var audit = new AuditTransport(scenario, transport);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(audit);
        var policy = await scenario.Machine.InvokeAsync("audit", "--approve-policy");
        Assert.True(policy.Exit == 0, policy.Terminal.Output);
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit", "--approve-policy",
            "--approve", (string)policy.Result["approvalFingerprint"]!)).Exit);
        var review = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", unused.ToString());
        Assert.True(review.Exit == 0, review.Terminal.Output);
        Assert.Equal(records, (int)review.Result["eligibleRecords"]!);
        var expired = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", unused.ToString(),
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(expired.Exit == 0, expired.Terminal.Output);
        Assert.Equal(records, audit.Deletes.Count);
        Assert.All(audit.Deletes, deleted => Assert.StartsWith($"intent/{unused:N}/steps/", deleted.Key));
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit")).Exit);
    }
    [Theory]
    [InlineData(-1, "Blocked", 0)]
    [InlineData(0, "Expired", 2)]
    [InlineData(1, "Expired", 2)]
    public async Task Unused_analysis_expires_only_at_ninety_days_through_native_conditional_deletes(long ticks, string expected, int deleted)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var original = JsonNode.Parse(scenario.Source.Source!.Progress["intent/current"].Value)!;
        var revision = (Guid)original["revision"]!;
        var refresh = await scenario.Machine.InvokeAsync("infer", "--new-analysis");
        Assert.Equal("Partial", (string?)refresh.Result["state"]);
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("infer")).Exit);
        scenario.Reconstruct();
        var clock = scenario.Source.Source!.Clock;
        clock.Now = clock.Now.AddDays(90).AddTicks(ticks);
        scenario.Machine.Boundary.Repository.ReservationClock = clock;
        using var transport = new AuditTransport(scenario);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var policy = await scenario.Machine.InvokeAsync("audit", "--approve-policy");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit", "--approve-policy",
            "--approve", (string)policy.Result["approvalFingerprint"]!)).Exit);
        var review = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString());
        if (ticks < 0)
        {
            Assert.Equal(expected, (string?)review.Result["state"]);
            Assert.Empty(transport.Deletes);
            return;
        }
        Assert.True(review.Exit == 0, review.Terminal.Output);
        Assert.Equal(2, (int)review.Result["eligibleRecords"]!);
        var result = await scenario.Machine.InvokeAsync("audit", "--expire-analysis", revision.ToString(),
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(result.Exit == 0, result.Terminal.Output);
        Assert.Equal(expected, (string?)result.Result["state"]);
        Assert.Equal(deleted, transport.Deletes.Count);
        Assert.All(transport.Deletes, request => Assert.False(string.IsNullOrEmpty(request.ETag)));
        scenario.Reconstruct();
        var inspected = await scenario.Machine.InvokeAsync("audit");
        Assert.True(inspected.Exit == 0, inspected.Terminal.Output);
        Assert.Equal(0, (int)inspected.Result["eligibleRecords"]!);
        Assert.Empty(scenario.GitHubWrites);
    }

    [Fact]
    public async Task Private_export_requires_exact_review_and_is_reverified_against_current_roots_and_readable_files()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var adoption = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing",
            "--approve", (string)adoption.Result["revision"]!)).Exit);
        using var transport = new AuditTransport(scenario);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var directory = Path.Combine(Path.GetTempPath(), "dsf-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var destination = Path.Combine(directory, "export");
        try
        {
            var before = scenario.Arm.Product.Export();
            var review = await scenario.Machine.InvokeAsync("audit", "--export", destination);
            Assert.True(review.Exit == 0, review.Terminal.Output + review.Terminal.Error);
            Assert.Equal("Reviewed", (string?)review.Result["state"]);
            Assert.False(Directory.Exists(destination));
            Assert.Equal(before, scenario.Arm.Product.Export());
            var wrong = await scenario.Machine.InvokeAsync("audit", "--export", destination, "--approve", new string('a', 64));
            Assert.Equal("Blocked", (string?)wrong.Result["state"]);
            Assert.False(Directory.Exists(destination));
            var exported = await scenario.Machine.InvokeAsync("audit", "--export", destination,
                "--approve", (string)review.Result["approvalFingerprint"]!);
            Assert.True(exported.Exit == 0, exported.Terminal.Output + exported.Terminal.Error);
            Assert.Equal("Exported", (string?)exported.Result["state"]);
            Assert.False((bool)exported.Result["deletionAuthorized"]!);
            Assert.NotNull(exported.Result["receipt"]);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(destination));
            foreach (var file in Directory.GetFiles(destination))
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            var readable = string.Join("\n", Directory.GetFiles(destination, "records-*.json").Select(File.ReadAllText));
            Assert.Contains("Coordinate volunteer shifts", readable);
            Assert.DoesNotContain("source-installation-token-never-persist", readable);
            var documents = Directory.GetFiles(destination, "document-*.json").Select(file => JsonNode.Parse(File.ReadAllText(file))!).ToArray();
            Assert.Contains(documents, document => (string?)document["kind"] == "ReviewedBoundary");
            var charter = Assert.Single(documents, document => (string?)document["kind"] == "AdoptedCharter");
            Assert.Equal(CharterScenario.ExistingCharter, (string?)charter["content"]!["review"]!["after"]);
            scenario.Reconstruct();
            var retained = scenario.Arm.Product.Export();
            var verified = await scenario.Machine.InvokeAsync("audit", "--verify-export", destination);
            Assert.True(verified.Exit == 0, verified.Terminal.Output);
            Assert.Equal("Verified", (string?)verified.Result["state"]);
            Assert.Equal(retained, scenario.Arm.Product.Export());
            var chunk = Directory.GetFiles(destination, "records-*.json").First();
            var original = File.ReadAllText(chunk);
            File.WriteAllText(chunk, "{}");
            var corrupt = await scenario.Machine.InvokeAsync("audit", "--verify-export", destination);
            Assert.Equal(1, corrupt.Exit);
            Assert.Null(corrupt.Result["receipt"]);
            File.WriteAllText(chunk, original);
            File.SetUnixFileMode(chunk, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Equal("Blocked", (string?)(await scenario.Machine.InvokeAsync("audit", "--verify-export", destination)).Result["state"]);
            File.SetUnixFileMode(chunk, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var displaced = Path.Combine(directory, "retained-original.json");
            File.Move(chunk, displaced);
            var incomplete = await scenario.Machine.InvokeAsync("audit", "--verify-export", destination);
            Assert.Equal(1, incomplete.Exit);
            Assert.Null(incomplete.Result["receipt"]);
            File.CreateSymbolicLink(chunk, displaced);
            var linked = await scenario.Machine.InvokeAsync("audit", "--verify-export", destination);
            Assert.Equal(1, linked.Exit);
            Assert.Null(linked.Result["receipt"]);
            File.Delete(chunk);
            File.Move(displaced, chunk);
            var unexpected = Path.Combine(destination, "unmanifested.json");
            File.WriteAllText(unexpected, "{}");
            Assert.Equal(1, (await scenario.Machine.InvokeAsync("audit", "--verify-export", destination)).Exit);
            File.Delete(unexpected);
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Equal("Blocked", (string?)(await scenario.Machine.InvokeAsync("audit", "--verify-export", destination)).Result["state"]);
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(0, (await scenario.Machine.InvokeAsync("audit", "--verify-export", destination)).Exit);
            scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, value => value);
            var stale = await scenario.Machine.InvokeAsync("audit", "--verify-export", destination);
            Assert.Equal("Blocked", (string?)stale.Result["state"]);
            Assert.Null(stale.Result["receipt"]);
            Assert.Empty(scenario.GitHubWrites);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var file in Directory.GetFiles(destination)) File.Delete(file);
                Directory.Delete(destination);
            }
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Operator_inspects_retained_intent_without_current_source_authorization_or_writes()
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing",
            "--approve", (string)review.Result["revision"]!)).Exit);
        scenario.Reconstruct();
        using var transport = new AuditTransport(scenario);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        scenario.Source.Source!.GitHubStatus = 403;
        var owner = scenario.Machine.Store.Export();
        var product = scenario.Arm.Product.Export();
        var progress = System.Text.Json.JsonSerializer.Serialize(scenario.Source.Source.Progress);
        var result = await scenario.Machine.InvokeAsync("audit");
        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
        Assert.Equal("Inspected", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["currentAuthorization"]!);
        Assert.True((int)result.Result["protectedRecords"]! > 0);
        Assert.Equal(0, (int)result.Result["eligibleRecords"]!);
        Assert.Contains(result.Result["records"]!.AsArray(), record =>
            (string?)record!["identity"]!["key"] == ProductConfigurationKeys.CharterAdopted);
        Assert.Equal(owner, scenario.Machine.Store.Export());
        Assert.Equal(product, scenario.Arm.Product.Export());
        Assert.Equal(progress, System.Text.Json.JsonSerializer.Serialize(scenario.Source.Source.Progress));
        Assert.Empty(scenario.GitHubWrites);
    }

    internal sealed class AuditTransport(CharterScenario scenario, HttpMessageHandler? outer = null) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker product = new(outer ?? scenario.Source, false);
        internal List<(string Key, string ETag)> Deletes { get; } = [];
        internal string? DeleteFailure { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == new Uri(scenario.Source.Source!.ProgressEndpoint).Host)
            {
                if (request.Method == HttpMethod.Delete)
                {
                    var key = Uri.UnescapeDataString(request.RequestUri.AbsolutePath["/kv/".Length..]);
                    var etag = request.Headers.IfMatch.Single().Tag;
                    Deletes.Add((key, etag));
                    if (DeleteFailure == "denied") return new(System.Net.HttpStatusCode.Forbidden);
                    if (!scenario.Source.Source.Progress.TryGetValue(key, out var value))
                        return new(System.Net.HttpStatusCode.NotFound);
                    if (etag != "\"" + value.Revision + "\"") return new(System.Net.HttpStatusCode.PreconditionFailed);
                    scenario.Source.Source.Progress.Remove(key);
                    if (DeleteFailure == "lost") throw new HttpRequestException("Synthetic lost deletion acknowledgment");
                    return new(System.Net.HttpStatusCode.NoContent);
                }
                using var source = new HttpMessageInvoker(scenario.Source.Source, false);
                return await source.SendAsync(request, token);
            }
            return await product.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) product.Dispose();
            base.Dispose(disposing);
        }
    }
}
