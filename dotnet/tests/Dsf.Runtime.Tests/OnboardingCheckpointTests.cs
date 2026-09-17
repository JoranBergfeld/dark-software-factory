using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class OnboardingCheckpointTests
{
    [Fact]
    public void Unknown_store_profile_is_not_a_default_greenfield_configuration()
    {
        using var remote = new CheckpointTransport();
        using var http = new HttpClient(remote);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CosmosRunStore(
            "https://factory.documents.azure.com", "dsf", "preview-runs", "acme",
            new AzureCosmosDocumentGateway(new CheckpointCredential(), http), (ConveyorProfile)99));
    }

    [Theory]
    [InlineData(2080768, true)]
    [InlineData(2080769, false)]
    public async Task Native_checkpoint_budget_includes_the_entire_serialized_document(int bytes, bool accepted)
    {
        using var remote = new CheckpointTransport();
        using var http = new HttpClient(remote);
        var document = JsonSerializer.SerializeToNode(new { id = "bounded", product = "acme", padding = "" })!;
        var overhead = Encoding.UTF8.GetByteCount(document.ToJsonString());
        document["padding"] = new string('x', bytes - overhead);
        var json = document.ToJsonString();
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(json));
        var gateway = new AzureCosmosDocumentGateway(new CheckpointCredential(), http);

        var write = gateway.WriteCheckpointAsync("https://factory.documents.azure.com", "dsf", "preview-runs", "acme", "bounded", json, null, CancellationToken.None);

        if (accepted)
        {
            Assert.NotEmpty((await write).ETag);
            Assert.Single(remote.Writes);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<CosmosCheckpointException>(() => write);
            Assert.Equal(CosmosCheckpointFailure.Capacity, failure.Failure);
            Assert.Empty(remote.Writes);
        }
    }

    [Theory]
    [InlineData("missing-etag")]
    [InlineData("wrong-etag")]
    [InlineData("changed-echo")]
    public async Task Malformed_native_write_acknowledgments_do_not_confirm_checkpoint_ownership(string corruption)
    {
        using var remote = new CheckpointTransport { ResponseCorruption = corruption };
        using var http = new HttpClient(remote);
        var store = Store(http);
        var run = FailedEvaluation();

        var failure = await Assert.ThrowsAsync<CosmosCheckpointException>(() => store.SaveAsync(run, "initial", CancellationToken.None));

        Assert.Equal(CosmosCheckpointFailure.Unknown, failure.Failure);
        Assert.Contains(run.Id, remote.Documents.Keys);
        await Assert.ThrowsAsync<CosmosCheckpointException>(() => store.SaveAsync(run, "retry", CancellationToken.None));
        Assert.Single(remote.Writes);
    }

    [Fact]
    public async Task Cancellation_after_native_commit_does_not_rollback_or_allow_replay()
    {
        using var cancellation = new CancellationTokenSource();
        using var remote = new CheckpointTransport { AfterWrite = cancellation.Cancel };
        using var http = new HttpClient(remote);
        var store = Store(http);
        var run = FailedEvaluation();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(run, "initial", cancellation.Token));

        Assert.Contains(run.Id, remote.Documents.Keys);
        await Assert.ThrowsAsync<CosmosCheckpointException>(() => store.SaveAsync(run, "retry", CancellationToken.None));
        Assert.Single(remote.Writes);
    }

    [Fact]
    public async Task Onboarding_store_requires_an_explicit_workload_credential_without_trying_a_developer_chain()
    {
        using var remote = new CheckpointTransport();
        using var http = new HttpClient(remote);
        var store = new CosmosRunStore("https://factory.documents.azure.com", "dsf", "preview-runs", "acme",
            new AzureCosmosDocumentGateway(httpClient: http), ConveyorProfile.OnboardingPreview);

        var failure = await Assert.ThrowsAsync<CosmosCheckpointException>(() => store.SaveAsync(FailedEvaluation(), "initial", CancellationToken.None));

        Assert.Equal("ExplicitWorkloadCredentialRequired", failure.Failure.ToString());
        Assert.Empty(remote.Writes);
    }

    [Fact]
    public async Task Removed_profile_cannot_launder_onboarding_artifacts_through_a_default_greenfield_store()
    {
        using var remote = new CheckpointTransport();
        using var http = new HttpClient(remote);
        var run = FailedEvaluation();
        await Store(http).SaveAsync(run, "initial", CancellationToken.None);
        var document = JsonNode.Parse(remote.Documents[run.Id])!;
        document.AsObject().Remove("profile");
        remote.Documents[run.Id] = document.ToJsonString();
        var greenfield = new CosmosRunStore("https://factory.documents.azure.com", "dsf", "preview-runs", "acme",
            new AzureCosmosDocumentGateway(new CheckpointCredential(), http));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => greenfield.LoadAsync(run.Id, CancellationToken.None));

        Assert.Single(remote.Writes);
    }

    [Theory]
    [InlineData("wrong-product")]
    [InlineData("wrong-id")]
    [InlineData("missing-profile")]
    [InlineData("missing-observations")]
    [InlineData("changed-claim")]
    public async Task Corrupt_or_partial_native_checkpoint_cannot_be_loaded_or_promoted_by_the_same_worker(string corruption)
    {
        using var remote = new CheckpointTransport();
        using var http = new HttpClient(remote);
        var run = FailedEvaluation();
        await Store(http).SaveAsync(run, "initial", CancellationToken.None);
        var document = JsonNode.Parse(remote.Documents[run.Id])!;
        if (corruption == "wrong-product") document["product"] = "another-product";
        if (corruption == "wrong-id") document["id"] = "another-run";
        if (corruption == "missing-profile") document.AsObject().Remove("profile");
        if (corruption == "missing-observations") document.AsObject().Remove("modelObservations");
        if (corruption == "changed-claim") document["proposals"]![0]!["synthesis"]!["claims"]![0]!["text"] = "Every user abandoned all shifts.";
        remote.Documents[run.Id] = document.ToJsonString();
        var worker = Store(http);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker.LoadAsync(run.Id, CancellationToken.None));
        var writes = remote.Writes.Count;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker.SaveAsync(run, "unsafe-repair", CancellationToken.None));
        Assert.Equal(writes, remote.Writes.Count);
    }

    [Fact]
    public async Task A_lost_native_write_acknowledgment_does_not_allow_a_forced_retry_or_claim_a_remote_hold()
    {
        using var remote = new CheckpointTransport { LoseWriteResponse = true };
        using var http = new HttpClient(remote);
        var run = FailedEvaluation();
        var worker = Store(http);

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker.SaveAsync(run, "initial", CancellationToken.None));
        Assert.DoesNotContain("test-only-transport-secret", failure.ToString());
        Assert.Contains(run.Id, remote.Documents.Keys);
        run.FailureReason = "MustNotOverwriteUnconfirmedResult";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker.SaveAsync(run, "retry", CancellationToken.None));
        Assert.Single(remote.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Whole_utf8_checkpoint_budget_is_checked_before_native_write(bool escaped)
    {
        using var remote = new CheckpointTransport();
        using var http = new HttpClient(remote);
        var run = FailedEvaluation();
        run.Record("bounded-audit", escaped ? new string('\u00e9', 400000) : new string('x', 2 * 1024 * 1024));

        var failure = await Assert.ThrowsAsync<CosmosCheckpointException>(() => Store(http).SaveAsync(run, "oversized", CancellationToken.None));

        Assert.Equal("Capacity", failure.Failure.ToString());
        Assert.Empty(remote.Writes);
        Assert.Empty(remote.Documents);
    }

    [Fact]
    public async Task Independent_workers_cannot_overwrite_a_newer_onboarding_checkpoint()
    {
        using var initialRemote = new CheckpointTransport();
        using var initialHttp = new HttpClient(initialRemote);
        var original = FailedEvaluation();
        await Store(initialHttp).SaveAsync(original, "initial", CancellationToken.None);
        using var remote = new CheckpointTransport(initialRemote.Export());
        using var http = new HttpClient(remote);
        var first = Store(http);
        var second = Store(http);
        var winner = (await first.LoadAsync(original.Id, CancellationToken.None))!;
        var stale = (await second.LoadAsync(original.Id, CancellationToken.None))!;
        winner.FailureReason = "FirstWorkerObservedAuthorityChange";
        stale.FailureReason = "StaleWorkerWouldOverwriteHistory";

        await first.SaveAsync(winner, "first-worker", CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<CosmosCheckpointException>(() => second.SaveAsync(stale, "stale-worker", CancellationToken.None));
        Assert.Equal(CosmosCheckpointFailure.Conflict, conflict.Failure);

        Assert.Equal(winner.FailureReason, (await Store(http).LoadAsync(original.Id, CancellationToken.None))!.FailureReason);
        Assert.DoesNotContain(remote.Writes, write => write.Upsert);
        Assert.Equal(2, remote.Writes.Count(write => write.Method == HttpMethod.Put));
    }

    [Fact]
    public async Task Native_checkpoint_roundtrip_preserves_onboarding_inputs_and_typed_artifacts_even_when_readiness_failed()
    {
        using var remote = new CheckpointTransport();
        using var http = new HttpClient(remote);
        var run = FailedEvaluation();
        await Store(http).SaveAsync(run, "evaluation:final-authority", CancellationToken.None);
        using var independent = new CheckpointTransport(remote.Export());
        using var nextHttp = new HttpClient(independent);

        var restored = (await Store(nextHttp).LoadAsync(run.Id, CancellationToken.None))!;

        Assert.Equal(ConveyorProfile.OnboardingPreview, restored.Profile);
        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(restored));
        Assert.Equal(RunStatus.Error, restored.Status);
        Assert.Empty(restored.FiledIssues);
        Assert.Equal("dsf:proposal", Assert.Single(restored.Proposals.Single().IntendedPublication!.Labels));
        Assert.Empty(restored.Proposals.Single().IntendedPublication!.Assignees);
    }

    private static CosmosRunStore Store(HttpClient http) =>
        new("https://factory.documents.azure.com", "dsf", "preview-runs", "acme",
            new AzureCosmosDocumentGateway(new CheckpointCredential(), http), ConveyorProfile.OnboardingPreview);

    private static ConveyorRun FailedEvaluation()
    {
        var factory = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var revision = new string('a', 64);
        var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
        var snapshot = new SourceSnapshot(1, factory, factory, revision, 913, 42, "acme/shifts", "main",
            new('b', 40), new('c', 40), now, revision);
        var adoption = new CharterAdoption(1, factory, factory, 1, "UnchangedExisting", revision,
            new('d', 40), revision, snapshot.Commit, snapshot.RootTree, factory, revision, snapshot.Id, revision,
            revision, 42, now, null, null, 1, revision);
        var analysis = new IntentResult(IntentState.Blocked, "Current source authority changed.", null, null, null,
            [], [], ["Deployment correlation remains unknown."], null);
        var evidence = new EvidenceItem("azuremonitor", "telemetry:assignment-1", "An overlapping volunteer assignment was rejected.");
        var model = new ModelWorkloadObservation("synthesis", revision, ModelCapabilityState.Verified,
            ModelCapabilityReason.None, "gpt-4.1", factory.ToString(), revision, 120, 0, []);
        var synthesized = new SynthesizedProposal("Explain assignment conflicts", "Explain the existing assignment conflict.",
            [new("conflict", evidence.Summary, [evidence.Reference])], ["Conflict frequency remains unknown."]);
        var grounding = new GroundingEvaluation(new(true, true, "The observation reports a rejected overlap.",
            [new("conflict", ClaimSupport.Supported, "The record reports this rejection.",
                [new(evidence.Reference, "assignment was rejected")], [])], []), model);
        var shape = new IssuePreview(synthesized.Title, "scope:conflict", ["dsf:proposal"])
            { Body = "Explain the existing assignment conflict.\n\nDisposition: Proceed", Disposition = ProposalVerdict.Proceed };
        var run = new ConveyorRun
        {
            Id = factory.ToString("N"), Profile = ConveyorProfile.OnboardingPreview, DryRun = true,
            ProductHints = ["acme"], SourceKinds = ["azuremonitor"], EvaluationRevision = revision,
            EvaluationContext = new(factory, revision, adoption, new(analysis,
                new(snapshot, "Prior adopted charter retained as history.", adoption.CharterBlob)),
                new("production", ["/applications/shifts"], ["/evidence/shifts"]),
                [new(evidence.SourceKind, evidence.Reference, revision, revision, "private:captured-observation", now)],
                new(revision, []), new(2, 0.6, [new("value", 1)])),
            Status = RunStatus.Error, FailureReason = "CurrentSourceAuthorityChanged",
        };
        run.Evidence.Add(evidence);
        run.Proposals.Add(new("candidate", synthesized.Title, ["azuremonitor"], [evidence.Reference])
        {
            IntentKey = shape.IntentKey, Body = synthesized.Body, Synthesis = synthesized, Grounding = grounding,
            ClusterEvidence = [evidence], IntendedPublication = shape, Verdict = ProposalVerdict.Error,
            CouncilReview = new() { Profile = ConveyorProfile.OnboardingPreview, EvaluationRevision = revision,
                Evidence = [evidence], Outcome = ProposalVerdict.Error, FailureReason = run.FailureReason },
        });
        run.SynthesisEvaluations.Add(new([evidence.Reference], new(true, synthesized, "Explaining this recorded conflict supports coordinators."), model));
        run.ModelObservations.Add(new("synthesis:0", revision, model));
        run.Proposals[0].Labels.Add("dsf:proposal");
        run.PreviewedIssues.Add(shape);
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        run.Record("evaluation:final-authority", "Retained evaluation artifacts are historical, not current readiness.");
        return run;
    }

    private sealed class CheckpointCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token)
        {
            Assert.Equal(["https://cosmos.azure.com/.default"], context.Scopes);
            return new("test-cosmos-token", DateTimeOffset.MaxValue);
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) =>
            ValueTask.FromResult(GetToken(context, token));
    }

    private sealed class CheckpointTransport : HttpMessageHandler
    {
        internal readonly Dictionary<string, string> Documents;
        internal readonly List<(HttpMethod Method, bool Upsert)> Writes = [];
        internal bool LoseWriteResponse;
        internal string? ResponseCorruption { get; init; }
        internal Action? AfterWrite { get; init; }
        private int version;
        private sealed record RemoteState(Dictionary<string, string> Documents, int Version);
        internal CheckpointTransport(string? serialized = null)
        {
            var state = serialized is null ? new RemoteState([], 0) : JsonSerializer.Deserialize<RemoteState>(serialized)!;
            Documents = state.Documents;
            version = state.Version;
        }
        internal string Export() => JsonSerializer.Serialize(new RemoteState(Documents, version));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("factory.documents.azure.com", request.RequestUri!.Host);
            Assert.Equal("[\"acme\"]", request.Headers.GetValues("x-ms-documentdb-partitionkey").Single());
            Assert.Equal("type=aad&ver=1.0&sig=test-cosmos-token",
                Uri.UnescapeDataString(request.Headers.GetValues("Authorization").Single()));
            const string collection = "/dbs/dsf/colls/preview-runs/docs";
            if (request.Method == HttpMethod.Get)
            {
                Assert.StartsWith(collection + "/", request.RequestUri.AbsolutePath);
                return Documents.TryGetValue(request.RequestUri.Segments[^1], out var stored)
                    ? Response(stored, HttpStatusCode.OK) : new(HttpStatusCode.NotFound);
            }
            var document = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var id = (string)document["id"]!;
            var upsert = request.Headers.Contains("x-ms-documentdb-is-upsert");
            Writes.Add((request.Method, upsert));
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(collection, request.RequestUri.AbsolutePath);
                if (!upsert && Documents.ContainsKey(id)) return new(HttpStatusCode.Conflict);
            }
            else
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(collection + "/" + id, request.RequestUri.AbsolutePath);
                if (!Documents.TryGetValue(id, out var previous)) return new(HttpStatusCode.NotFound);
                if (request.Headers.GetValues("If-Match").Single() != (string?)JsonNode.Parse(previous)!["_etag"])
                    return new(HttpStatusCode.PreconditionFailed);
            }
            Assert.Equal("acme", (string?)document["product"]);
            document["_etag"] = "\"native-checkpoint-" + ++version + "\"";
            Documents[id] = document.ToJsonString();
            AfterWrite?.Invoke();
            if (LoseWriteResponse)
            {
                LoseWriteResponse = false;
                throw new HttpRequestException("test-only-transport-secret");
            }
            return Response(document.ToJsonString(), request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);
        }
        private HttpResponseMessage Response(string json, HttpStatusCode status)
        {
            var body = JsonNode.Parse(json)!;
            var etag = (string?)body["_etag"];
            if (ResponseCorruption == "missing-etag") body.AsObject().Remove("_etag");
            if (ResponseCorruption == "wrong-etag") etag = "\"not-the-body-version\"";
            if (ResponseCorruption == "changed-echo") body["failureReason"] = "Different returned checkpoint.";
            var response = new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("ETag", etag);
            return response;
        }
    }
}
