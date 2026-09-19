using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryEvidenceRefreshTests(ActivatedFactoryFixture fixture) : IClassFixture<ActivatedFactoryFixture>
{
    [Fact]
    public async Task Promoted_decision_automatically_refreshes_complete_source_without_changing_governing_intent_or_publishing()
    {
        var transport = fixture.Reset();
        transport.SourceInferenceInputs.Clear();
        var source = transport.Scenario.Source.Source!;
        var governance = transport.Scenario.Arm.Product.Export();
        var original = JsonNode.Parse(source.Progress["intent/current"].Value)!;
        var tree = source.Trees[source.RootTree]["tree"]!.AsArray();
        source.RootTree = source.Tree(tree.Select(n => ((string)n!["path"]!, (string)n["mode"]!,
            (string)n["path"]! == "README.md"
                ? source.Blob(Dsf.Runtime.Tests.IntentModelFixture.SourceText + "\nScheduling implementation now rejects an overlapping shift earlier.\n")
                : (string)n["sha"]!)).ToArray());
        source.Head = new string('e', 40);
        var untouched = source.Progress.ToDictionary();
        var statusOnly = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)statusOnly.Result["currentReady"]!);
        Assert.Equal(untouched, source.Progress);
        Assert.Empty(transport.SourceInferenceInputs);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        using var admission = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", invocation, FactorySourceValidation.RecordJson);
        Assert.True(admission.StatusCode == HttpStatusCode.Accepted, await admission.Content.ReadAsStringAsync());
        JsonNode observed = null!;
        for (var i = 0; i < 2000; i++)
        {
            await Task.Delay(100);
            using var status = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", invocation, FactorySourceValidation.RecordJson);
            observed = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
            if (status.StatusCode != HttpStatusCode.Accepted) break;
        }
        Assert.True((string?)observed["readiness"] == "Blocked", observed.ToJsonString() + "\n"
            + string.Join("\n", transport.HostFailures.TakeLast(5)) + "\n"
            + source.Progress.GetValueOrDefault("intent/refresh/current").Value + "\n"
            + source.Progress["intent/current"].Value);
        Assert.Equal("CompatibleSourceRefreshAwaitingQualification", (string?)observed["reason"]);
        var refreshed = JsonNode.Parse(source.Progress["intent/current"].Value)!;
        Assert.NotEqual((string?)original["revision"], (string?)refreshed["revision"]);
        Assert.Equal(source.Head, (string?)refreshed["snapshot"]!["commit"]);
        Assert.Equal("DraftReady", (string?)refreshed["state"]);
        Assert.Equal((int)refreshed["availableChunks"]!, (int)refreshed["checkedChunks"]!);
        var compatibility = transport.SourceInferenceInputs.Where(p => p.StartsWith("CHECK_UNCHANGED_GOVERNING_INTENT", StringComparison.Ordinal)).ToArray();
        Assert.Equal((int)refreshed["availableChunks"]!, compatibility.Length);
        Assert.All(compatibility, p =>
        {
            Assert.Contains("governingCharter", p);
            Assert.Contains("currentCompleteClaims", p);
            Assert.Contains("originalApprovedClaims", p);
            Assert.Contains("dependencies", p);
        });
        Assert.Contains(compatibility, p => p.Contains("rejects an overlapping shift earlier", StringComparison.Ordinal));
        Assert.DoesNotContain(source.Progress, p => p.Value.Value.Contains("INFER_INTENT", StringComparison.Ordinal)
            || p.Value.Value.Contains("CHECK_UNCHANGED_GOVERNING_INTENT", StringComparison.Ordinal));
        Assert.Contains(source.Progress, p => p.Key.EndsWith("/retention", StringComparison.Ordinal)
            && p.Value.Value.Contains((string)refreshed["revision"]!, StringComparison.Ordinal));
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        Assert.Equal(0, transport.IssuePosts);
        FactoryActivationTests.ConnectAudit(transport);
        var audit = await transport.Scenario.Machine.InvokeAsync("audit");
        Assert.True(audit.Exit == 0, audit.Terminal.Output);
        var step = source.Progress.First(p => p.Key.Contains("/checks/", StringComparison.Ordinal));
        source.Progress.Remove(step.Key);
        var damaged = await transport.Scenario.Machine.InvokeAsync("audit");
        Assert.Equal(1, damaged.Exit);
        source.Progress.Add(step.Key, step.Value);
        var history = source.Progress.Where(p => p.Key.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal)).ToArray();
        foreach (var record in history) source.Progress.Remove(record.Key);
        var erased = await transport.Scenario.Machine.InvokeAsync("audit");
        Assert.Equal(1, erased.Exit);
        using var forbidden = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain(source.Progress.Keys, k => k.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal));
        foreach (var record in history) source.Progress.Add(record.Key, record.Value);
        var held = transport.Scenario.Source.Source!.Progress[FactorySourceRefresh.CurrentKey];
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var historical = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)historical.Result["currentReady"]!);
        Assert.Equal(held, transport.Scenario.Source.Source!.Progress[FactorySourceRefresh.CurrentKey]);
    }

    [Theory]
    [InlineData("Conflict")]
    [InlineData("Unknown")]
    [InlineData("Unbounded")]
    [InlineData("Malformed")]
    [InlineData("Gap")]
    public async Task Automatic_refresh_preserves_material_unknown_and_necessary_gap_review(string condition)
    {
        var transport = fixture.Reset();
        transport.SourceInferenceInputs.Clear();
        var source = transport.Scenario.Source.Source!;
        var governance = transport.Scenario.Arm.Product.Export();
        AdvanceDefaultBranch(transport, condition == "Gap");
        transport.AlterCompletion = (prompt, response) =>
        {
            if (!prompt.StartsWith("CHECK_UNCHANGED_GOVERNING_INTENT", StringComparison.Ordinal)) return response;
            if (condition == "Malformed") return "{}";
            var result = JsonNode.Parse(response)!;
            if (condition == "Unbounded")
            {
                result["complete"] = false;
                foreach (var judgment in result["judgments"]!.AsArray())
                {
                    judgment!["verdict"] = "Unknown";
                    judgment["reason"] = "The necessary dependency impact exceeds this bounded analysis.";
                }
                return result.ToJsonString();
            }
            result["judgments"]![0]!["verdict"] = condition;
            result["judgments"]![0]!["reason"] = condition == "Conflict"
                ? "The current product purpose conflicts with the unchanged adopted intent."
                : "The impact on adopted intent cannot be bounded from the necessary current dependencies.";
            return result.ToJsonString();
        };
        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        var observed = await RefreshAsync(transport, invocation);
        Assert.True((string?)observed["readiness"] == (condition == "Malformed" ? "Failed" : "Blocked"),
            observed.ToJsonString() + "\n" + string.Join("\n", transport.HostFailures.TakeLast(3)));
        var progress = JsonSerializer.Deserialize<SourceRefreshProgress>(source.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
        Assert.Equal(condition == "Malformed" ? SourceRefreshState.Failed : SourceRefreshState.ReviewRequired, progress.State);
        Assert.Null(progress.Work);
        Assert.Null(progress.Dispatch);
        Assert.Equal(condition == "Gap" ? 0 : 5, transport.SourceInferenceInputs.Count);
        var calls = transport.SourceInferenceInputs.Count;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var fresh = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, fresh.StatusCode);
        Assert.Equal(calls, transport.SourceInferenceInputs.Count);
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        Assert.Equal(0, transport.IssuePosts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_refresh_never_replays_unknown_model_work_or_advances_a_raced_snapshot(bool moveSnapshot)
    {
        var transport = fixture.Reset();
        AdvanceDefaultBranch(transport);
        var source = transport.Scenario.Source.Source!;
        transport.BeforeEvaluationResponse = (_, _) =>
        {
            if (transport.SourceInferenceInputs.Count == 0) return Task.CompletedTask;
            if (!moveSnapshot) throw new HttpRequestException("test-only-source-model-response-lost");
            source.Head = new string('f', 40);
            return Task.CompletedTask;
        };
        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        var observed = await RefreshAsync(transport, invocation);
        Assert.Equal("Unknown", (string?)observed["readiness"]);
        if (moveSnapshot) Assert.Contains("SourceMovedDuringRefreshReviewRequired", (string?)observed["reason"]);
        Assert.Single(transport.SourceInferenceInputs);
        var original = source.Progress.ToDictionary();
        var dispatched = JsonNode.Parse(source.Progress["intent/current"].Value)!["dispatch"]!.DeepClone();
        transport.BeforeEvaluationResponse = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var inspection = await PollRefreshAsync(transport, invocation);
        Assert.Equal("Unknown", (string?)inspection["readiness"]);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var fresh = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, fresh.StatusCode);
        Assert.Single(transport.SourceInferenceInputs);
        Assert.Equal(original, transport.Scenario.Source.Source!.Progress);
        Assert.True(JsonNode.DeepEquals(dispatched, JsonNode.Parse(transport.Scenario.Source.Source.Progress["intent/current"].Value)!["dispatch"]));
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Promoted_operator_source_mutations_cannot_bypass_indexed_refresh_admission()
    {
        var transport = fixture.Reset();
        var source = transport.Scenario.Source.Source!;
        var progress = source.Progress.ToDictionary();
        var governance = transport.Scenario.Arm.Product.Export();
        foreach (var (command, selection) in new[] { ("source", "--new-snapshot"), ("infer", "--new-analysis") })
        {
            var result = await transport.Scenario.Machine.InvokeAsync(command, selection);
            Assert.True(result.Exit == 1, result.Terminal.Output);
            Assert.Contains("PromotedSourceWorkRequiresIndexedDecision", result.Terminal.Output);
            Assert.Equal(progress, source.Progress);
        }
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Erased_refresh_history_between_advances_never_recreates_work_or_reports_completed_stop()
    {
        var transport = fixture.Reset();
        AdvanceDefaultBranch(transport);
        var source = transport.Scenario.Source.Source!;
        var erased = false;
        transport.BeforeIngressRequest = uri =>
        {
            if (uri.AbsolutePath == "/source-refresh-advance" && source.Progress.ContainsKey(FactorySourceRefresh.CurrentKey))
            {
                foreach (var key in source.Progress.Keys.Where(k => k.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal)).ToArray())
                    source.Progress.Remove(key);
                erased = true;
            }
            return Task.CompletedTask;
        };
        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        var observed = await RefreshAsync(transport, invocation);
        Assert.True(erased);
        Assert.Equal("Unknown", (string?)observed["readiness"]);
        Assert.Contains("OriginalSourceRefreshRootMissingBeforeContinuation", (string?)observed["reason"]);
        Assert.DoesNotContain(source.Progress.Keys, k => k.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal));
        Assert.Empty(transport.SourceInferenceInputs);
        transport.BeforeIngressRequest = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var historical = await PollRefreshAsync(transport, invocation);
        Assert.Equal("Unknown", (string?)historical["readiness"]);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var forbidden = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.True((bool)pause.Result["admissionClosed"]!);
        Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
        Assert.DoesNotContain(source.Progress.Keys, k => k.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal));
        Assert.Empty(transport.SourceInferenceInputs);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Original_refresh_process_proves_no_model_dispatch_before_transient_preflight_recovery()
    {
        var transport = fixture.Reset();
        AdvanceDefaultBranch(transport);
        var source = transport.Scenario.Source.Source!;
        transport.BeforeIngressRequest = uri =>
        {
            if (uri.AbsolutePath == "/authority" && source.Progress.TryGetValue(FactorySourceRefresh.CurrentKey, out var root))
            {
                var progress = JsonSerializer.Deserialize<SourceRefreshProgress>(root.Value, FactorySourceValidation.RecordJson)!;
                if (progress.Work is not null && progress.Phase == SourceRefreshPhase.Analysis)
                    throw new HttpRequestException("test-only-preflight-outage-before-any-model-request");
            }
            return Task.CompletedTask;
        };
        var stopped = await RefreshAsync(transport, new FactoryDecisionRequest(Guid.NewGuid()));
        Assert.True((string?)stopped["readiness"] == "Blocked", stopped.ToJsonString());
        Assert.Contains("NoModelDispatch", (string?)stopped["reason"]);
        Assert.Empty(transport.SourceInferenceInputs);
        var current = JsonSerializer.Deserialize<SourceRefreshProgress>(source.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
        Assert.Null(current.Work);
        Assert.Null(current.Dispatch);
        transport.BeforeIngressRequest = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var recovered = await RefreshAsync(transport, new FactoryDecisionRequest(Guid.NewGuid()));
        Assert.Equal("CompatibleSourceRefreshAwaitingQualification", (string?)recovered["reason"]);
        Assert.Equal(6, transport.SourceInferenceInputs.Count);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Issued_refresh_is_discoverable_without_concurrent_takeover_and_pause_never_replays_after_restart()
    {
        var transport = fixture.Reset();
        transport.SourceInferenceInputs.Clear();
        AdvanceDefaultBranch(transport);
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeEvaluationResponse = async (_, token) =>
        {
            if (transport.SourceInferenceInputs.Count == 0) return;
            issued.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", invocation, FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        try
        {
            await issued.Task.WaitAsync(TimeSpan.FromMinutes(2));
            using var concurrent = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, concurrent.StatusCode);
            var source = transport.Scenario.Source.Source!;
            var root = JsonSerializer.Deserialize<SourceRefreshProgress>(source.Progress[FactorySourceRefresh.CurrentKey].Value, FactorySourceValidation.RecordJson)!;
            Assert.Equal(invocation.InvocationId, root.Work!.Invocation);
            var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.True((bool)pause.Result["admissionClosed"]!);
            Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
        }
        finally { release.TrySetResult(); }
        var observed = await PollRefreshAsync(transport, invocation);
        Assert.Equal("Unknown", (string?)observed["readiness"]);
        var count = transport.SourceInferenceInputs.Count;
        var stored = transport.Scenario.Source.Source!.Progress.ToDictionary();
        transport.BeforeEvaluationResponse = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var progress = await PollRefreshAsync(transport, invocation);
        Assert.Equal("Unknown", (string?)progress["readiness"]);
        using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", invocation, FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(count, transport.SourceInferenceInputs.Count);
        Assert.Equal(stored, transport.Scenario.Source.Source!.Progress);
        Assert.Equal(0, transport.IssuePosts);
    }

    private static void AdvanceDefaultBranch(PreviewPreparationTransport transport, bool gap = false)
    {
        var source = transport.Scenario.Source.Source!;
        var entries = source.Trees[source.RootTree]["tree"]!.AsArray().Select(n => ((string)n!["path"]!, (string)n["mode"]!,
            (string)n["path"]! == "README.md" ? source.Blob(Dsf.Runtime.Tests.IntentModelFixture.SourceText + "\nCurrent scheduling implementation changed.\n") : (string)n["sha"]!)).ToList();
        if (gap) entries.Add(("z-external", "120000", source.Blob("../unread-necessary-dependency")));
        source.RootTree = source.Tree(entries.ToArray());
        source.Head = new string('e', 40);
    }

    private async Task<JsonNode> RefreshAsync(PreviewPreparationTransport transport, FactoryDecisionRequest invocation)
    {
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", invocation, FactorySourceValidation.RecordJson);
        Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync());
        return await PollRefreshAsync(transport, invocation);
    }

    private async Task<JsonNode> PollRefreshAsync(PreviewPreparationTransport transport, FactoryDecisionRequest invocation)
    {
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        for (var i = 0; i < 2000; i++)
        {
            await Task.Delay(100);
            using var status = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", invocation, FactorySourceValidation.RecordJson);
            var observed = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
            if (status.StatusCode != HttpStatusCode.Accepted) return observed;
        }
        throw new Xunit.Sdk.XunitException("Original admitted refresh did not finish within the fixture observation budget.");
    }

    [Fact]
    public async Task Completed_promotion_recovers_after_model_observation_expiry_using_current_native_checks_and_real_evaluation()
    {
        var transport = fixture.Reset();
        var authority = FactoryModelsValidation.ReadPreparation(transport.Manifest, transport.Values());
        var historicalProof = JsonSerializer.Deserialize<ModelCapabilityProof>(
            transport.Values()[authority.LastProofReference!], FactorySourceValidation.RecordJson)!;
        var governance = transport.Scenario.Arm.Product.Export();
        transport.Restart(historicalProof.ExpiresAt.AddMinutes(2));
        FactoryActivationTests.ConnectAudit(transport);
        var calls = transport.EvaluationInference.Count;
        var requests = transport.Requests.Count;

        transport.BeforeIngressRequest = uri => uri.AbsolutePath == "/authority"
            ? throw new HttpRequestException("test-only-model-authority-outage") : Task.CompletedTask;
        var unavailable = await fixture.AdvanceUntilCompletedAsync(transport, transport.Scenario.Machine.InvokeAsync("activate", "--status"));
        Assert.Equal(1, unavailable.Exit);
        Assert.True((bool)unavailable.Result["historicallyCompleted"]!);
        Assert.False((bool)unavailable.Result["currentReady"]!);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);

        transport.BeforeIngressRequest = null;
        var current = await fixture.AdvanceUntilCompletedAsync(transport, transport.Scenario.Machine.InvokeAsync("activate", "--status"));
        Assert.Equal(1, current.Exit);
        Assert.False((bool)current.Result["currentReady"]!);
        Assert.False((bool)current.Result["isActivated"]!);
        Assert.True((bool)current.Result["historicallyCompleted"]!);
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        Assert.Equal(FactoryModelsValidation.Hash(historicalProof), FactoryModelsValidation.Hash(
            JsonSerializer.Deserialize<ModelCapabilityProof>(transport.Values()[authority.LastProofReference!], FactorySourceValidation.RecordJson)!));
        Assert.Contains(transport.Requests.Skip(requests), r => r.Uri.AbsolutePath == "/authority");
        Assert.Contains(transport.Requests.Skip(requests), r => r.Uri.Host == "api.loganalytics.io");

        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        var decision = await fixture.DecideAsync(transport, invocation);
        Assert.Equal("ReadyAwaitingActivation", (string?)decision["readiness"]);
        Assert.Equal("Confirmed", (string?)Assert.Single(decision["evaluation"]!["publications"]!.AsArray())!["state"]);
        Assert.Equal(16, transport.EvaluationInference.Count - calls);
        Assert.Equal(FactoryModelsValidation.RouteNames.Order(), transport.EvaluationInference.Skip(calls).Distinct().Order());
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == "api.github.com" && r.Method is "PATCH" or "PUT" or "DELETE");
        var evaluated = await fixture.AdvanceUntilCompletedAsync(transport, transport.Scenario.Machine.InvokeAsync("activate", "--status"));
        Assert.True(evaluated.Exit == 0, evaluated.Terminal.Output + "\n" + string.Join("\n", transport.HostFailures.TakeLast(5)));
        Assert.True((bool)evaluated.Result["currentReady"]!);
        Assert.True((bool)evaluated.Result["isActivated"]!);
        var observedAt = transport.Scenario.Source.Source!.Clock.Now;
        transport.Restart(observedAt.AddSeconds(authority.Configuration.ValidForSeconds + 1));
        FactoryActivationTests.ConnectAudit(transport);
        var expired = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)expired.Result["currentReady"]!);
        Assert.True((bool)expired.Result["historicallyCompleted"]!);
        Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        transport.Restart(observedAt);
        FactoryActivationTests.ConnectAudit(transport);
        var resumed = await fixture.DecideAsync(transport, invocation);
        Assert.Equal("ReadyAwaitingActivation", (string?)resumed["readiness"]);
        Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        Assert.Equal(1, transport.IssuePosts);

        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var failure in new[] { "empty", "stale", "denied" })
        {
            var documents = transport.SeedDocuments.ToDictionary();
            transport.ApplicationQueryStatus = failure == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            transport.AlterApplication = body =>
            {
                if (failure == "empty") body["tables"]![0]!["rows"] = new JsonArray();
                if (failure == "stale") body["tables"]![0]!["rows"]![0]![0] =
                    transport.Scenario.Source.Source!.Clock.Now.AddDays(-2).ToString("O");
                return body;
            };
            var blocked = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
            Assert.False((bool)blocked.Result["currentReady"]!);
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(documents, transport.SeedDocuments);
            Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        }
        transport.ApplicationQueryStatus = HttpStatusCode.OK;
        transport.AlterApplication = null;

        var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.True((bool)pause.Result["admissionClosed"]!);
        var stopped = transport.Scenario.Arm.Product.Export();
        var status = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)status.Result["isActivated"]!);
        foreach (var route in new[] { "/decide", "/decision" })
        {
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + route,
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Equal(stopped, transport.Scenario.Arm.Product.Export());
        Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        Assert.Equal(1, transport.IssuePosts);
    }

    [Fact]
    public async Task Expired_model_history_never_replays_an_unconfirmed_actual_provider_response_after_recovery()
    {
        var transport = fixture.Reset();
        var model = FactoryModelsValidation.ReadPreparation(transport.Manifest, transport.Values());
        transport.Restart(model.VerifiedUntil!.Value.AddMinutes(2));
        FactoryActivationTests.ConnectAudit(transport);
        transport.BeforeEvaluationResponse = (_, _) => throw new HttpRequestException("test-only-issued-model-response-lost");
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        var calls = transport.EvaluationInference.Count;
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync());
        JsonNode observed = null!;
        for (var i = 0; i < 1000; i++)
        {
            using var response = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", request, FactorySourceValidation.RecordJson);
            observed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            if (response.StatusCode != HttpStatusCode.Accepted) break;
            await Task.Delay(100);
        }
        Assert.Equal("Unknown", (string?)observed["readiness"]);
        Assert.Equal(calls + 1, transport.EvaluationInference.Count);
        var original = transport.SeedDocuments.ToDictionary();
        transport.BeforeEvaluationResponse = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        using var retried = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.Contains((string?)JsonNode.Parse(await retried.Content.ReadAsStringAsync())!["readiness"], new[] { "Unknown", "Checking" });
        using var inspected = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", request, FactorySourceValidation.RecordJson);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(await inspected.Content.ReadAsStringAsync())!["readiness"]);
        using var newWork = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, newWork.StatusCode);
        Assert.Equal(calls + 1, transport.EvaluationInference.Count);
        Assert.Equal(original, transport.SeedDocuments);
        Assert.Equal(0, transport.IssuePosts);
    }
}
