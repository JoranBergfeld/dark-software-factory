using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryActiveWorkflowTests(ActivatedFactoryFixture fixture) : IClassFixture<ActivatedFactoryFixture>
{
    [Fact]
    public async Task Original_worker_release_during_inspection_does_not_turn_completed_work_into_unknown()
    {
        var transport = fixture.Reset();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeEvaluationResponse = async (_, token) =>
        {
            transport.BeforeEvaluationResponse = null;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var request = new FactoryDecisionRequest(Guid.NewGuid(), true);
        var id = FactoryPromotion.DecisionId(transport.Manifest.Identity.FactoryId, request.InvocationId);
        var documentPath = "/dbs/widget/colls/council-preview/docs/" + id;
        var calls = transport.EvaluationInference.Count;
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        await entered.Task.WaitAsync(TimeSpan.FromMinutes(3));
        var crossedRelease = false;
        transport.AfterNativeReadResponse = async (path, captured) =>
        {
            if (path != documentPath || (string?)captured["admission"]?["state"] != "Running") return;
            transport.AfterNativeReadResponse = null;
            release.TrySetResult();
            var complete = await fixture.DecideAsync(transport, request);
            Assert.Equal("ReadyAwaitingActivation", (string?)complete["readiness"]);
            crossedRelease = true;
        };
        JsonNode observed;
        try
        {
            using var status = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", request, FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            observed = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
        }
        finally { release.TrySetResult(); transport.AfterNativeReadResponse = null; }
        Assert.True(crossedRelease);
        Assert.Equal("ReadyAwaitingActivation", (string?)observed["readiness"]);
        Assert.Equal(id, (string?)observed["evaluation"]!["proof"]!["previewId"]);
        Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Original_running_invocation_remains_inspectable_across_clients_and_pause_without_readmission()
    {
        var transport = fixture.Reset();
        var machine = transport.Scenario.Machine;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeEvaluationResponse = async (_, token) =>
        {
            transport.BeforeEvaluationResponse = null;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var invocation = new FactoryDecisionRequest(Guid.NewGuid(), true);
        var fingerprint = FactoryLive.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(),
            transport.Scenario.Source.Source!.Clock.Now).Plan.Fingerprint;
        var id = FactoryPromotion.DecisionId(transport.Manifest.Identity.FactoryId, invocation.InvocationId);
        var path = "/dbs/widget/colls/council-preview/docs/" + id;
        var models = transport.EvaluationInference.Count;
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", invocation, FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        await entered.Task.WaitAsync(TimeSpan.FromMinutes(3));
        try
        {
            var original = transport.SeedDocuments[path];
            var native = JsonNode.Parse(original)!;
            Assert.Equal("Running", (string?)native["admission"]!["state"]);
            Assert.Single(native["dispatches"]!.AsArray());
            for (var client = 0; client < 2; client++)
            {
                transport.Scenario.Arm.Product = ReservationTestStore.Restore(transport.Scenario.Arm.Product.Export());
                FactoryActivationTests.ConnectAudit(transport);
                var before = transport.Requests.Count;
                var status = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", "--execution",
                    invocation.InvocationId.ToString(), "--approve", fingerprint));
                Assert.Equal("Running", (string?)status.Result["readiness"]);
                Assert.Equal(invocation.InvocationId, (Guid)status.Result["invocationId"]!);
                Assert.False((bool)status.Result["isActivated"]!);
                Assert.Contains(transport.Requests.Skip(before), r => r.Uri.AbsolutePath == "/execution");
                Assert.Equal(original, transport.SeedDocuments[path]);
                Assert.Equal(models + 1, transport.EvaluationInference.Count);
            }
            using var duplicate = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", invocation, FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
            Assert.Equal("Running", (string?)JsonNode.Parse(await duplicate.Content.ReadAsStringAsync())!["readiness"]);
            foreach (var incompatible in new[] { new FactoryDecisionRequest(Guid.NewGuid(), true), invocation with { DryRun = false } })
            {
                foreach (var route in new[] { "/decide", "/decision" })
                {
                    using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + route, incompatible, FactorySourceValidation.RecordJson);
                    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                }
            }
            var resumeReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.AfterIngressResponse = (route, status) =>
            {
                if (route == "/exercise" && status == HttpStatusCode.Accepted) resumeReached.TrySetResult();
                return Task.CompletedTask;
            };
            var resumed = machine.InvokeAsync("live", "--exercise", invocation.InvocationId.ToString(), "--approve", fingerprint);
            await resumeReached.Task.WaitAsync(TimeSpan.FromMinutes(3));
            Assert.Equal(original, transport.SeedDocuments[path]);
            Assert.Equal(models + 1, transport.EvaluationInference.Count);
            var pause = await machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
            var beforePauseInspection = transport.Requests.Count;
            var draining = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", "--execution",
                invocation.InvocationId.ToString(), "--approve", fingerprint));
            Assert.Equal("Unknown", (string?)draining.Result["readiness"]);
            Assert.Equal(invocation.InvocationId, (Guid)draining.Result["invocationId"]!);
            Assert.Contains("draining", (string?)draining.Result["reason"]);
            Assert.Contains(transport.Requests.Skip(beforePauseInspection), r => r.Uri.AbsolutePath == "/execution");
            Assert.Equal(original, transport.SeedDocuments[path]);
            using var observed = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", invocation, FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
            Assert.Equal("Unknown", (string?)JsonNode.Parse(await observed.Content.ReadAsStringAsync())!["readiness"]);
            using var pausedDispatch = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", invocation, FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, pausedDispatch.StatusCode);
            var resumeObservation = await fixture.AdvanceUntilCompletedAsync(transport, resumed);
            Assert.Equal("Unknown", (string?)resumeObservation.Result["readiness"]);
            Assert.Equal(models + 1, transport.EvaluationInference.Count);
        }
        finally { release.TrySetResult(); }
        for (var i = 0; i < 2000 && (string?)JsonNode.Parse(transport.SeedDocuments[path])!["admission"]?["state"] != "Unknown"; i++)
            await Task.Delay(100);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(transport.SeedDocuments[path])!["admission"]!["state"]);
        var quarantined = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", "--execution",
            invocation.InvocationId.ToString(), "--approve", fingerprint));
        Assert.Equal("Unknown", (string?)quarantined.Result["readiness"]);
        Assert.Equal(models + 1, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Theory]
    [InlineData("exercise")]
    [InlineData("execution")]
    [InlineData("live-status")]
    [InlineData("publication-status")]
    public async Task Promoted_operator_live_surfaces_accept_fresh_inputs_after_original_preview_ages(string surface)
    {
        var transport = fixture.Reset();
        var machine = transport.Scenario.Machine;
        var originalAuthority = transport.Values();
        var originalPreview = transport.SeedDocuments.Where(p => p.Key.Contains("/preview-", StringComparison.Ordinal)).ToDictionary();
        var evaluation = JsonNode.Parse(originalPreview.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Value)!;
        var inputs = evaluation["inputs"]!;
        transport.Scenario.Source.Source!.Clock.Now =
            ((DateTimeOffset)inputs["telemetry"]!["capture"]!["rows"]![0]!["reference"]!["recordTime"]!)
            .AddSeconds((int)inputs["telemetryConfiguration"]!["freshnessSeconds"]!).AddMinutes(2);
        var live = FactoryLive.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(),
            transport.Scenario.Source.Source.Clock.Now);
        var invocation = Guid.NewGuid();
        if (surface == "execution")
            Assert.Equal("ReadyAwaitingActivation", (string?)(
                await fixture.DecideAsync(transport, new(invocation, true)))["readiness"]);
        string[] arguments = surface switch
        {
            "live-status" => ["--status"],
            "publication-status" => ["--publication", "--status"],
            _ => ["--" + surface, invocation.ToString(), "--approve", live.Plan.Fingerprint]
        };
        var result = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", arguments));
        Assert.True(result.Exit == 0, result.Terminal.Output);
        if (surface is "exercise" or "execution")
            Assert.Equal("ReadyAwaitingActivation", (string?)result.Result["readiness"]);
        Assert.Equal(originalAuthority, transport.Values());
        foreach (var pair in originalPreview) Assert.Equal(pair.Value, transport.SeedDocuments[pair.Key]);
        var calls = transport.EvaluationInference.Count;
        var documents = transport.SeedDocuments.ToDictionary();
        var head = transport.Scenario.Source.Source.Head;
        foreach (var failure in new[] { "telemetry", "source", "permissions" })
        {
            transport.AlterApplication = body =>
            {
                if (failure == "telemetry") body["tables"]![0]!["rows"] = new JsonArray();
                return body;
            };
            transport.Scenario.Source.Source.Head = failure == "source" ? new string('f', 40) : head;
            transport.PublisherPermissionDrift = failure == "permissions";
            var blocked = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", arguments));
            Assert.True(blocked.Exit == 1, failure + ": " + blocked.Terminal.Output);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(documents, transport.SeedDocuments);
            Assert.Equal(originalAuthority, transport.Values());
            transport.AlterApplication = null;
            transport.Scenario.Source.Source.Head = head;
            transport.PublisherPermissionDrift = false;
        }
        var recovered = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", arguments));
        Assert.True(recovered.Exit == 0, recovered.Terminal.Output);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(originalAuthority, transport.Values());
        Assert.Equal("DrainingUnknown", (string?)(await machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString())).Result["state"]);
        var paused = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", arguments));
        Assert.Equal(1, paused.Exit);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Fresh_current_telemetry_restores_promoted_admission_after_original_preview_ages_without_reapproval()
    {
        var transport = fixture.Reset();
        var machine = transport.Scenario.Machine;
        var originalAuthority = transport.Values();
        var originalPreview = transport.SeedDocuments.Where(p => p.Key.Contains("/preview-", StringComparison.Ordinal)).ToDictionary();
        var evaluation = JsonNode.Parse(originalPreview.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Value)!;
        var recordTime = (DateTimeOffset)evaluation["inputs"]!["telemetry"]!["capture"]!["rows"]![0]!["reference"]!["recordTime"]!;
        var freshness = (int)evaluation["inputs"]!["telemetryConfiguration"]!["freshnessSeconds"]!;
        transport.Scenario.Source.Source!.Clock.Now = recordTime.AddSeconds(freshness).AddMinutes(2);
        Assert.True((DateTimeOffset)JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryModels])!["verifiedUntil"]!
            > transport.Scenario.Source.Source.Clock.Now);
        var inspected = await fixture.AdvanceUntilCompletedAsync(transport,
            machine.InvokeAsync("preview", ["--evaluation", .. PreviewEvaluationTests.Pins(transport)]));
        Assert.Equal("Blocked", (string?)inspected.Result["readiness"]);
        Assert.Equal("CapturedApplicationEvidenceStale", (string?)inspected.Result["reason"]);

        var recovered = await machine.InvokeAsync("activate", "--status");
        Assert.True(recovered.Exit == 0, recovered.Terminal.Output + "\n" + string.Join("\n", transport.HostFailures));
        Assert.True((bool)recovered.Result["currentReady"]!);
        Assert.True((bool)recovered.Result["isActivated"]!);
        Assert.True((bool)recovered.Result["historicallyCompleted"]!);
        var request = new FactoryDecisionRequest(Guid.NewGuid(), true);
        var decision = await fixture.DecideAsync(transport, request);
        Assert.Equal("ReadyAwaitingActivation", (string?)decision["readiness"]);
        Assert.True((DateTimeOffset)decision["evaluation"]!["inputs"]!["capturedAt"]! > recordTime.AddSeconds(freshness));

        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var calls = transport.EvaluationInference.Count;
        var documents = transport.SeedDocuments.Count;
        foreach (var failure in new[] { "missing", "stale", "denied" })
        {
            transport.ApplicationQueryStatus = failure == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            transport.AlterApplication = body =>
            {
                if (failure == "missing") body["tables"]![0]!["rows"] = new JsonArray();
                if (failure == "stale") body["tables"]![0]!["rows"]![0]![0] = recordTime.ToString("O");
                return body;
            };
            var unavailable = await machine.InvokeAsync("activate", "--status");
            Assert.False((bool)unavailable.Result["currentReady"]!);
            Assert.False((bool)unavailable.Result["isActivated"]!);
            Assert.True((bool)unavailable.Result["historicallyCompleted"]!);
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(documents, transport.SeedDocuments.Count);
            transport.ApplicationQueryStatus = HttpStatusCode.OK;
            transport.AlterApplication = null;
        }
        recovered = await machine.InvokeAsync("activate", "--status");
        Assert.True(recovered.Exit == 0, recovered.Terminal.Output);
        Assert.True((bool)recovered.Result["isActivated"]!);
        inspected = await fixture.AdvanceUntilCompletedAsync(transport,
            machine.InvokeAsync("preview", ["--evaluation", .. PreviewEvaluationTests.Pins(transport)]));
        Assert.Equal("Blocked", (string?)inspected.Result["readiness"]);
        Assert.Equal("CapturedApplicationEvidenceStale", (string?)inspected.Result["reason"]);
        Assert.Equal(originalAuthority, transport.Values());
        foreach (var pair in originalPreview) Assert.Equal(pair.Value, transport.SeedDocuments[pair.Key]);

        var pause = await machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
        var paused = await machine.InvokeAsync("activate", "--status");
        Assert.False((bool)paused.Result["isActivated"]!);
        Assert.True((bool)paused.Result["historicallyCompleted"]!);
        using var closed = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Theory]
    [InlineData("invalid-model")]
    [InlineData("missing-root")]
    [InlineData("missing-completion")]
    [InlineData("changed-completion")]
    public async Task Promoted_operator_live_surfaces_never_fall_back_from_invalid_current_authority_or_promotion(string damage)
    {
        var transport = fixture.Reset();
        var machine = transport.Scenario.Machine;
        var promotion = JsonNode.Parse(transport.Values()[FactoryPromotion.RootKey])!;
        var completion = FactoryPromotion.CompletionKey((Guid)promotion["operationId"]!);
        if (damage == "invalid-model")
        {
            transport.Scenario.Source.Source!.Clock.Now = ((DateTimeOffset)JsonNode.Parse(
                transport.Values()[ProductConfigurationKeys.FactoryModels])!["verifiedUntil"]!).AddMinutes(2);
            var models = FactoryModelsValidation.ReadPreparation(transport.Manifest, transport.Values());
            transport.Scenario.Arm.Product.Change(models.LastProofReference!, raw =>
            {
                var proof = JsonNode.Parse(raw)!;
                proof["observations"]![0]!["outputSha256"] = new string('a', 64);
                return proof.ToJsonString();
            });
        }
        if (damage == "missing-root") transport.Scenario.Arm.Product.Remove(FactoryPromotion.RootKey);
        if (damage == "missing-completion") transport.Scenario.Arm.Product.Remove(completion);
        if (damage == "changed-completion")
            transport.Scenario.Arm.Product.Change(completion, raw =>
            {
                var record = JsonNode.Parse(raw)!;
                record["approvedBy"] = "99999999-9999-9999-9999-999999999999";
                return record.ToJsonString();
            });
        var live = FactoryLive.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(),
            transport.Scenario.Source.Source!.Clock.Now);
        var invocation = Guid.NewGuid().ToString();
        var calls = transport.EvaluationInference.Count;
        var originalAuthority = transport.Values();
        var originalNative = transport.SeedDocuments.ToDictionary();
        string[][] commands = [
            ["--status"], ["--publication", "--status"],
            ["--exercise", invocation, "--approve", live.Plan.Fingerprint],
            ["--execution", invocation, "--approve", live.Plan.Fingerprint]
        ];
        foreach (var arguments in commands)
        {
            var rejected = await fixture.AdvanceUntilCompletedAsync(transport, machine.InvokeAsync("live", arguments));
            Assert.True(rejected.Exit == 1, damage + ": " + rejected.Terminal.Output);
        }
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(originalAuthority, transport.Values());
        Assert.Equal(originalNative, transport.SeedDocuments);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Expired_model_history_requires_current_host_protocol_despite_fresh_telemetry()
    {
        var transport = fixture.Reset();
        var models = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryModels])!;
        transport.Scenario.Source.Source!.Clock.Now = ((DateTimeOffset)models["verifiedUntil"]!).AddMinutes(2);
        transport.AlterIngressBody = raw =>
        {
            var body = JsonNode.Parse(raw)!.AsObject();
            body.Remove("continuationProtocol");
            if (body["authority"] is JsonObject authority) authority.Remove("continuationProtocol");
            return body.ToJsonString();
        };
        var current = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)current.Result["currentReady"]!);
        Assert.False((bool)current.Result["isActivated"]!);
        Assert.True((bool)current.Result["historicallyCompleted"]!);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var calls = transport.EvaluationInference.Count;
        using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);
        transport.AlterIngressBody = null;
    }

    [Fact]
    public async Task Native_authority_denied_after_publication_claim_records_no_dispatch_and_retries_original_intent_once()
    {
        var transport = fixture.Reset();
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        var denied = false;
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterEvaluationWrite = body =>
        {
            if ((string?)body["state"] != "Dispatched") return;
            denied = true;
            transport.DenyNativeReads = true;
        };
        transport.AfterIngressResponse = (path, _) =>
        {
            if (path == "/publish")
            {
                transport.DenyNativeReads = false;
                returned.TrySetResult();
            }
            return Task.CompletedTask;
        };
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        await returned.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(denied);
        Assert.Equal(0, transport.IssuePosts);
        var path = Assert.Single(transport.SeedDocuments.Keys, k => k.Contains("/proposal-publications/docs/proposal-", StringComparison.Ordinal)
            && !k.EndsWith("-origin", StringComparison.Ordinal));
        var original = JsonNode.Parse(transport.SeedDocuments[path])!;
        Assert.Equal("NotDispatched", (string?)original["state"]);
        Assert.Equal("NotDispatched", (string?)original["attempts"]![0]!["state"]);
        var calls = transport.EvaluationInference.Count;
        transport.AfterEvaluationWrite = null;
        transport.AfterIngressResponse = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var retried = await fixture.DecideAsync(transport, request);
        Assert.Equal("Confirmed", (string?)retried["evaluation"]!["publications"]![0]!["state"]);
        var confirmed = JsonNode.Parse(transport.SeedDocuments[path])!;
        Assert.Equal((string?)original["_rid"], (string?)confirmed["_rid"]);
        Assert.Equal(original["intent"]!.ToJsonString(), confirmed["intent"]!.ToJsonString());
        Assert.Equal(2, confirmed["attempts"]!.AsArray().Count);
        Assert.Equal("NotDispatched", (string?)confirmed["attempts"]![0]!["state"]);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(1, transport.IssuePosts);
    }

    [Theory]
    [InlineData("split", "ReadyAwaitingActivation", "Escalate", 16)]
    [InlineData("no-go", "ReadyAwaitingActivation", "Kill", 16)]
    [InlineData("error", "Failed", null, 2)]
    public async Task Actual_promoted_council_preserves_nonqualifying_decisions_without_creating(string vote, string readiness, string? disposition, int calls)
    {
        var transport = fixture.Reset();
        transport.AlterCompletion = (prompt, original) =>
        {
            if (vote == "error" && prompt.StartsWith("PROPOSAL_SYNTHESIS:", StringComparison.Ordinal)) return "not a structured proposal";
            if (prompt.Contains("As juror", StringComparison.Ordinal))
                return (vote == "no-go" || prompt.Contains("As juror 'juror-3'", StringComparison.Ordinal) ? "NO-GO" : "GO")
                    + ": The scoped application evidence and adopted product intent were independently evaluated.";
            return original;
        };
        var before = transport.EvaluationInference.Count;
        var result = await fixture.DecideAsync(transport, new(Guid.NewGuid()));
        Assert.True(readiness == (string?)result["readiness"], $"{result["readiness"]}: {result["reason"]}; "
            + $"{result["evaluation"]?["run"]?["failureReason"]}; {result["evaluation"]?["run"]?["audit"]}");
        Assert.Equal(calls, transport.EvaluationInference.Count - before);
        Assert.NotNull(result["evaluation"]!["receipt"]);
        if (disposition is not null) Assert.Equal(disposition, (string?)result["evaluation"]!["proof"]!["candidates"]![0]!["disposition"]);
        Assert.Equal(0, transport.IssuePosts);
        var audited = await transport.Scenario.Machine.InvokeAsync("audit", "--page", "0");
        Assert.True(audited.Exit == 0, audited.Terminal.Output);
    }

    [Theory]
    [InlineData("confirmed", "ReadyAwaitingActivation", 1)]
    [InlineData("rejected", "Blocked", 2)]
    [InlineData("lost", "Unknown", 1)]
    [InlineData("canceled", "Unknown", 1)]
    public async Task Native_issue_response_controls_serialized_resume_without_search_or_human_edit_mutation(string response, string readiness, int posts)
    {
        var transport = fixture.Reset();
        transport.PublicationFailure = response == "confirmed" ? null : response;
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        var models = transport.EvaluationInference.Count;
        var original = await fixture.DecideAsync(transport, request);
        Assert.Equal(readiness, (string?)original["readiness"]);
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal(16, transport.EvaluationInference.Count - models);
        Assert.NotNull(original["evaluation"]!["publications"]![0]!["nativeIdentity"]);
        var durable = JsonSerializer.Serialize(transport.SeedDocuments);
        if (response == "confirmed")
        {
            transport.PublishedIssues[0]["title"] = "Human changed title";
            transport.PublishedIssues[0]["body"] = "Human changed body";
        }
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        Assert.Equal(durable, JsonSerializer.Serialize(transport.SeedDocuments));
        transport.PublicationFailure = null;
        var resumed = await fixture.DecideAsync(transport, request);
        Assert.Equal(posts, transport.IssuePosts);
        Assert.Equal(16, transport.EvaluationInference.Count - models);
        if (response is "lost" or "canceled")
        {
            Assert.Equal("Unknown", (string?)resumed["readiness"]);
            var audit = await transport.Scenario.Machine.InvokeAsync("audit", "--page", "0");
            Assert.Equal(1, audit.Exit);
            Assert.Equal("Unknown", (string?)audit.Result["state"]);
            Assert.Null(audit.Result["receipt"]);
            Assert.Contains(audit.Result["records"]!.AsArray(), r => (string?)r!["identity"]!["store"] == "Publication");
        }
        else
        {
            Assert.Equal("Confirmed", (string?)resumed["evaluation"]!["publications"]![0]!["state"]);
            if (response == "confirmed")
            {
                Assert.Equal("Human changed title", (string?)transport.PublishedIssues[0]["title"]);
                Assert.Equal("Human changed body", (string?)transport.PublishedIssues[0]["body"]);
            }
        }
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == "api.github.com" && (r.Method is "PATCH" or "PUT" or "DELETE"
            || r.Method == "POST" && !r.Uri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal)
                && !r.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
            || r.Uri.AbsolutePath.Contains("/search/", StringComparison.Ordinal)
            || r.Uri.AbsolutePath.Contains("/comments", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stable_problem_survives_distinct_decisions_and_native_provider_drift_blocks_new_admission()
    {
        var transport = fixture.Reset();
        var first = await fixture.DecideAsync(transport, new(Guid.NewGuid()));
        var second = await fixture.DecideAsync(transport, new(Guid.NewGuid()));
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal((string?)first["evaluation"]!["publications"]![0]!["journal"]!["id"],
            (string?)second["evaluation"]!["publications"]![0]!["journal"]!["id"]);
        Assert.Equal((long?)first["evaluation"]!["publications"]![0]!["receipt"]!["issueId"],
            (long?)second["evaluation"]!["publications"]![0]!["receipt"]!["issueId"]);
        var calls = transport.EvaluationInference.Count;
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var drift in new[] { "permission", "issues", "repository_dispatch", "unrecognized_event" })
        {
            transport.PublisherPermissionDrift = drift == "permission";
            transport.PublisherWorkflowEvent = drift == "permission" ? null : drift;
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", new FactoryDecisionRequest(Guid.NewGuid()),
                FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(1, transport.IssuePosts);
        }
        transport.PublisherPermissionDrift = false;
        transport.PublisherWorkflowEvent = null;
        var witness = Assert.Single(transport.SeedDocuments.Keys, k => k.EndsWith("-origin", StringComparison.Ordinal));
        transport.SeedDocuments.Remove(witness);
        var missing = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)missing.Result["isActivated"]!);
        Assert.Equal("Unknown", (string?)missing.Result["firstFilingState"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_manual_sweep_and_serialized_dry_run_resume_use_native_promoted_workflow_not_legacy_composition(bool changedSource)
    {
        var transport = fixture.Reset();
        if (changedSource) FactoryEvidenceRefreshTests.AdvanceDefaultBranch(transport);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var dependencies = transport.DecisionDependencies(http);
        var settings = new RuntimeSettings("widget", $"https://dsfo-{transport.Manifest.Identity.FactoryId:N}.azconfig.io",
            "", "", "https://cosmos.invalid", "https://model.invalid", "gpt", "embedding", "", "", "", "");
        var env = new Dictionary<string, string?> { [RuntimeIntegrationSettings.FactoryProfilePin] = transport.Manifest.ProfilePin };
        var invocation = Guid.NewGuid();
        var signal = SignalReader.ReadFromJson(JsonSerializer.Serialize(new { invocation_id = invocation, product_hints = new[] { "widget" },
            source_kinds = new[] { "azuremonitor" } }), "native-signal", true);
        var file = Path.Combine(Path.GetTempPath(), "active-signal-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { invocation_id = invocation, product_hints = new[] { "widget" },
            source_kinds = new[] { "azuremonitor" } }));
        var output = new StringWriter(); var error = new StringWriter();
        try
        {
            var exit = await fixture.AdvanceUntilCompletedAsync(transport, RuntimeCliApplication.InvokeAsync(["run", "--signal", file, "--dry-run"],
                new Dictionary<string, string?>(env) { [RuntimeSettingsComposer.DsfProduct] = settings.Product,
                    [RuntimeSettingsComposer.AzureAppConfigEndpoint] = settings.AppConfigEndpoint },
                output, error, dependencies, CancellationToken.None));
            Assert.True(exit == 0, error.ToString());
        }
        finally { File.Delete(file); }
        var preview = await fixture.AdvanceUntilCompletedAsync(transport, RuntimeVerbs.RunSignalAsync(settings, signal, dependencies, CancellationToken.None, env));
        Assert.Equal(RunStatus.Previewed, preview.Status);
        Assert.Equal(0, transport.IssuePosts);
        var resumed = await fixture.AdvanceUntilCompletedAsync(transport, RuntimeVerbs.RunSignalAsync(settings, signal, dependencies, CancellationToken.None, env));
        Assert.Equal(preview.Id, resumed.Id);
        var changed = signal with { DryRun = false };
        await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(settings, changed, dependencies, CancellationToken.None, env));
        Assert.Equal(0, transport.IssuePosts);
        if (changedSource) FactoryEvidenceRefreshTests.AdvanceDefaultBranch(transport, commit: 'f');
        var sweep = await fixture.AdvanceUntilCompletedAsync(transport, RuntimeVerbs.SweepAsync(settings, false, dependencies, CancellationToken.None, env));
        Assert.Equal(RunStatus.Filed, sweep!.Status);
        Assert.Single(sweep.FiledIssues);
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal(changedSource ? 12 : 0, transport.SourceInferenceInputs.Count);
        await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        var requests = transport.Requests.Count;
        await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(settings, signal, dependencies, CancellationToken.None, env));
        await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.SweepAsync(settings, false, dependencies, CancellationToken.None, env));
        Assert.DoesNotContain(transport.Requests.Skip(requests), r => r.Uri.Host == "api.github.com");
    }

    [Fact]
    public async Task Protected_audit_export_does_not_change_authority_of_original_promoted_decision()
    {
        var transport = fixture.Reset();
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        var original = await fixture.DecideAsync(transport, request);
        Assert.Equal("Confirmed", (string?)original["evaluation"]!["publications"]![0]!["state"]);
        var directory = Path.Combine(Path.GetTempPath(), "active-export-" + Guid.NewGuid().ToString("N"));
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var destination = Path.Combine(directory, "export");
        try
        {
            var review = await transport.Scenario.Machine.InvokeAsync("audit", "--export", destination);
            Assert.True(review.Exit == 0, review.Terminal.Output);
            var export = await transport.Scenario.Machine.InvokeAsync("audit", "--export", destination, "--approve",
                (string)review.Result["approvalFingerprint"]!);
            Assert.True(export.Exit == 0, export.Terminal.Output);
            var calls = transport.EvaluationInference.Count;
            var resumed = await fixture.DecideAsync(transport, request);
            Assert.Equal("ReadyAwaitingActivation", (string?)resumed["readiness"]);
            Assert.Equal("Confirmed", (string?)resumed["evaluation"]!["publications"]![0]!["state"]);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(1, transport.IssuePosts);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Deliberate_pause_after_model_issue_quarantines_original_worker_and_closes_new_entry()
    {
        var transport = fixture.Reset();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeEvaluationResponse = async (_, _) =>
        {
            transport.BeforeEvaluationResponse = null;
            var paused = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.Equal("DrainingUnknown", (string?)paused.Result["state"]);
            Assert.False((bool)paused.Result["stopCompleted"]!);
            stopped.SetResult();
        };
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync());
        await stopped.Task.WaitAsync(TimeSpan.FromMinutes(3));
        var key = "/dbs/widget/colls/council-preview/docs/" + FactoryPromotion.DecisionId(transport.Manifest.Identity.FactoryId, request.InvocationId);
        JsonNode? retained = null;
        for (var i = 0; i < 2000; i++)
        {
            retained = JsonNode.Parse(transport.SeedDocuments[key])!;
            if ((string?)retained["admission"]?["state"] == "Unknown") break;
            await Task.Delay(100);
        }
        Assert.Equal("Unknown", (string?)retained!["admission"]!["state"]);
        Assert.Contains(retained["dispatches"]!.AsArray(), d => (string?)d!["state"] == "Unknown");
        using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", new FactoryDecisionRequest(Guid.NewGuid()),
            FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Long_native_council_runs_outside_ingress_and_duplicate_dispatch_contender_observes_original_issue_effect()
    {
        var transport = fixture.Reset();
        transport.IngressLimit = TimeSpan.FromSeconds(240);
        transport.EvaluationModelDelay = TimeSpan.FromSeconds(20);
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeIssueResponse = async token => { issued.TrySetResult(); await release.Task.WaitAsync(token); };
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        var run = fixture.DecideAsync(transport, request);
        await issued.Task.WaitAsync(TimeSpan.FromMinutes(5));
        try
        {
            var decisionId = FactoryPromotion.DecisionId(transport.Manifest.Identity.FactoryId, request.InvocationId);
            var native = JsonNode.Parse(transport.SeedDocuments["/dbs/widget/colls/runs/docs/" + decisionId + "-run"])!;
            var candidate = (string)native["proposals"]![0]!["id"]!;
            var publisher = FactoryPublisher.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal)).ToDictionary(),
                transport.Scenario.Source.Source!.Clock.Now);
            var rollout = JsonSerializer.SerializeToNode(new { publisher.Plan, publisher.Receipts }, new JsonSerializerOptions(FactoryGitHubJson.Options)
                { IgnoreReadOnlyProperties = false })!;
            var contender = await transport.InvokePublisherAsync(rollout, "/publish", new FactoryPublicationRequest(request.InvocationId, candidate));
            Assert.Equal(HttpStatusCode.Conflict, contender.Status);
            Assert.Equal("Unknown", (string?)JsonNode.Parse(contender.Body)!["state"]);
            Assert.Equal(1, transport.IssuePosts);
        }
        finally { release.TrySetResult(); }
        var completed = await run;
        Assert.Equal("Confirmed", (string?)completed["evaluation"]!["publications"]![0]!["state"]);
        Assert.All(transport.IngressResponses, hop => Assert.True(hop.Elapsed < TimeSpan.FromSeconds(240), hop.ToString()));
        Assert.Equal(1, transport.IssuePosts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_periodic_orchestrator_uses_promoted_native_worker_without_legacy_lease_or_filer(bool changedSource)
    {
        var transport = fixture.Reset();
        if (changedSource) FactoryEvidenceRefreshTests.AdvanceDefaultBranch(transport);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var dependencies = transport.DecisionDependencies(http);
        var settings = new RuntimeSettings("widget", $"https://dsfo-{transport.Manifest.Identity.FactoryId:N}.azconfig.io",
            "", "", "https://cosmos.invalid", "https://model.invalid", "gpt", "embedding", "", "", "", "");
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeIssueResponse = async _ =>
        {
            var paused = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.Equal("DrainingUnknown", (string?)paused.Result["state"]);
            issued.TrySetResult();
        };
        await using var host = RuntimeVerbs.BuildOrchestratorHost(settings, dependencies, "127.0.0.1", 0, TimeSpan.FromMilliseconds(20),
            new Dictionary<string, string?> { [RuntimeIntegrationSettings.FactoryProfilePin] = transport.Manifest.ProfilePin });
        await host.StartAsync();
        try
        {
            using var probe = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await probe.GetAsync(host.Urls.Single() + "/healthz")).StatusCode);
            await issued.Task.WaitAsync(TimeSpan.FromMinutes(5));
            Assert.Equal(1, transport.IssuePosts);
            Assert.Equal(changedSource ? 6 : 0, transport.SourceInferenceInputs.Count);
            var confirmed = false;
            for (var i = 0; i < 300; i++)
            {
                confirmed = transport.SeedDocuments.Where(p => p.Key.Contains("/proposal-", StringComparison.Ordinal)
                    && !p.Key.EndsWith("-origin", StringComparison.Ordinal)).Any(p => (string?)JsonNode.Parse(p.Value)!["state"] == "Confirmed");
                if (confirmed) break;
                await Task.Delay(100);
            }
            Assert.True(confirmed, "An already-issued creation must retain its original known response while admission is paused.");
            var paused = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
            Assert.False((bool)paused.Result["isActivated"]!);
            Assert.True((bool)paused.Result["historicallyCompleted"]!);
            Assert.True((bool)paused.Result["firstConfirmedFiling"]!);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_HTTP_requires_forwarded_native_caller_token_not_forged_platform_headers_or_server_identity(bool changedSource)
    {
        var transport = fixture.Reset();
        if (changedSource) FactoryEvidenceRefreshTests.AdvanceDefaultBranch(transport);
        using var native = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var dependencies = transport.DecisionDependencies(native);
        var settings = new RuntimeSettings("widget", $"https://dsfo-{transport.Manifest.Identity.FactoryId:N}.azconfig.io",
            "", "", "", "", "", "", "", "", "", "");
        await using var host = RuntimeVerbs.BuildOrchestratorHost(settings, dependencies, "127.0.0.1", 0);
        await host.StartAsync();
        try
        {
            using var client = new HttpClient();
            var identity = transport.Manifest.Identity;
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", identity.AzureActorId);
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            { auth_typ = "aad", claims = new[] { new { typ = "oid", val = identity.AzureActorId }, new { typ = "tid", val = identity.TenantId } } })));
            var forged = Guid.NewGuid();
            using var denied = await client.PostAsJsonAsync(host.Urls.Single() + "/run",
                new { invocation_id = forged, product_hints = new[] { "widget" } });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.DoesNotContain(transport.SeedDocuments.Keys, k => k.EndsWith(FactoryPromotion.DecisionId(identity.FactoryId, forged), StringComparison.Ordinal));
            client.DefaultRequestHeaders.Clear();
            var live = FactoryLive.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(),
                transport.Scenario.Source.Source!.Clock.Now);
            var bearer = await dependencies.PreviewCredential!.GetTokenAsync(
                new Azure.Core.TokenRequestContext(["api://" + live.Plan.Selection.AudienceClientId + "/.default"]), CancellationToken.None);
            client.DefaultRequestHeaders.Authorization = new("Bearer", bearer.Token);
            var request = new FactoryDecisionRequest(Guid.NewGuid(), true);
            using var admitted = await client.PostAsJsonAsync(host.Urls.Single() + "/run",
                new { invocation_id = request.InvocationId, product_hints = new[] { "widget" } });
            Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync());
            var completed = await fixture.DecideAsync(transport, request);
            Assert.Equal("ReadyAwaitingActivation", (string?)completed["readiness"]);
            Assert.Equal(0, transport.IssuePosts);
            Assert.Equal(changedSource ? 6 : 0, transport.SourceInferenceInputs.Count);
            await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            var calls = transport.EvaluationInference.Count;
            using var paused = await client.PostAsJsonAsync(host.Urls.Single() + "/run",
                new { invocation_id = Guid.NewGuid(), product_hints = new[] { "widget" } });
            Assert.Equal(HttpStatusCode.Forbidden, paused.StatusCode);
            Assert.Equal(calls, transport.EvaluationInference.Count);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("factoryId")]
    [InlineData("approvedBy")]
    public async Task Historical_completion_requires_original_typed_receipt_even_when_paused(string corrupted)
    {
        var transport = fixture.Reset();
        var machine = transport.Scenario.Machine;
        await machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        var valid = await machine.InvokeAsync("activate", "--status");
        Assert.True((bool)valid.Result["historicallyCompleted"]!);
        Assert.False((bool)valid.Result["isActivated"]!);
        var approval = JsonSerializer.Deserialize<FactoryPromotionApproval>(transport.Values()[FactoryPromotion.RootKey], FactorySourceValidation.RecordJson)!;
        transport.Scenario.Arm.Product.Change(FactoryPromotion.CompletionKey(approval.OperationId), value =>
        {
            var receipt = JsonNode.Parse(value)!;
            receipt[corrupted] = corrupted == "protocol" ? JsonValue.Create(99) : JsonValue.Create(Guid.NewGuid().ToString());
            return receipt.ToJsonString();
        });
        var invalid = await machine.InvokeAsync("activate", "--status");
        Assert.Equal(1, invalid.Exit);
        Assert.False((bool)invalid.Result["historicallyCompleted"]!);
        Assert.False((bool)invalid.Result["isActivated"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_native_discovery_is_explicit_unknown_without_unindexed_work(bool entry)
    {
        var transport = fixture.Reset();
        var path = "/" + FactoryActivityIndex.Key("widget", transport.Manifest.Identity.FactoryId, false);
        var native = JsonNode.Parse(transport.SeedDocuments[path])!;
        var original = FactoryActivityIndex.Read(native.ToJsonString(), transport.Manifest.Identity.FactoryId, "widget",
            (Guid)native["promotionOperation"]!, false, transport.Scenario.Source.Source!.Clock.Now);
        var body = native.DeepClone();
        if (entry)
        {
            var corrupted = original with { Entries = [null!] };
            body["entries"] = new JsonArray((JsonNode?)null);
            body["checksum"] = corrupted.CalculateChecksum();
        }
        else body["entries"] = null;
        transport.SeedDocuments[path] = body.ToJsonString();
        using var client = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var calls = transport.EvaluationInference.Count;
        using var response = await client.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", new FactoryDecisionRequest(Guid.NewGuid()),
            FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["state"]);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);
    }
}

public class ActivatedFactoryFixture : IAsyncLifetime
{
    protected virtual bool SelectedSources => false;
    private readonly PublisherFixture publisher = new();
    private DateTimeOffset snapshotAt;
    private Dictionary<string, (string Value, int Revision)> sourceProgress = [];
    private Dictionary<string, DateTimeOffset> sourceModifiedAt = [];
    private string sourceTree = "";
    private string sourceHead = "";
    private string sourceBranch = "";
    public async Task InitializeAsync()
    {
        await publisher.InitializeAsync(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(5), SelectedSources);
        var transport = publisher.Reset();
        var machine = transport.Scenario.Machine;
        var selection = FactoryPublisherTests.Selection().ToJsonString();
        var review = await machine.InvokeAsync("live", "--publication", "--selection", selection);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        var rollout = await machine.InvokeAsync("live", "--publication", "--selection", selection, "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(rollout.Exit == 0, rollout.Terminal.Output);
        var operation = Guid.NewGuid().ToString();
        review = await machine.InvokeAsync("activate", "--promote", "--operation", operation);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        var activated = await machine.InvokeAsync("activate", "--promote", "--operation", operation, "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(activated.Exit == 0, activated.Terminal.Output);
        publisher.CaptureCurrent();
        snapshotAt = transport.Scenario.Source.Source!.Clock.Now;
        sourceProgress = transport.Scenario.Source.Source.Progress.ToDictionary();
        sourceModifiedAt = transport.Scenario.Source.Source.ProgressModifiedAt.ToDictionary();
        sourceTree = transport.Scenario.Source.Source.RootTree;
        sourceHead = transport.Scenario.Source.Source.Head;
        sourceBranch = transport.Scenario.Source.Source.Branch;
    }
    internal PreviewPreparationTransport Reset()
    {
        var transport = publisher.Reset(snapshotAt);
        var source = transport.Scenario.Source.Source!;
        source.Progress.Clear();
        foreach (var pair in sourceProgress) source.Progress.Add(pair.Key, pair.Value);
        source.ProgressModifiedAt.Clear();
        foreach (var pair in sourceModifiedAt) source.ProgressModifiedAt.Add(pair.Key, pair.Value);
        source.RootTree = sourceTree;
        source.Head = sourceHead;
        source.Branch = sourceBranch;
        source.OnRequest = null;
        source.GitHubStatus = 200;
        transport.SourceInferenceInputs.Clear();
        transport.AlterCompletion = null;
        transport.EvaluationResponseStatus = null;
        transport.BeforeEvaluationResponse = null;
        transport.EvaluationModelDelay = null;
        transport.IngressLimit = null;
        transport.DenyNativeReads = false;
        transport.ApplicationQueryStatus = HttpStatusCode.OK;
        transport.AlterApplication = null;
        transport.AfterEvaluationWrite = null;
        transport.AfterIngressResponse = null;
        transport.AfterNativeReadResponse = null;
        return transport;
    }
    internal string LiveEndpoint(PreviewPreparationTransport transport) =>
        FactoryLive.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(),
            transport.Scenario.Source.Source!.Clock.Now).Endpoint;
    internal async Task<T> AdvanceUntilCompletedAsync<T>(PreviewPreparationTransport transport, Task<T> work)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (!work.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await Task.WhenAny(work, Task.Delay(100));
            transport.Scenario.Source.Source!.Clock.Now += TimeSpan.FromMilliseconds(100);
        }
        Assert.True(work.IsCompleted, "Native CLI did not complete while its deterministic timers advanced.");
        return await work;
    }
    internal async Task<JsonNode> DecideAsync(PreviewPreparationTransport transport, FactoryDecisionRequest request)
    {
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var admitted = await http.PostAsJsonAsync(LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.True(admitted.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, await admitted.Content.ReadAsStringAsync());
        var observed = JsonNode.Parse(await admitted.Content.ReadAsStringAsync())!;
        for (var i = 0; i < 2000 && (string?)observed["readiness"] is "Queued" or "Running" or "Checking"; i++)
        {
            await Task.Delay(100);
            using var status = await http.PostAsJsonAsync(LiveEndpoint(transport) + "/decision", request, FactorySourceValidation.RecordJson);
            observed = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
            Assert.True(status.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, observed.ToJsonString());
        }
        Assert.True(observed["evaluation"] is not null, observed.ToJsonString() + "\n" + string.Join("\n", transport.HostFailures.TakeLast(3)));
        return observed;
    }
    public Task DisposeAsync() => publisher.DisposeAsync();
}
