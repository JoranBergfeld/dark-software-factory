using System.Text.Json;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

public sealed class OnboardingConveyorTests
{
    [Theory]
    [InlineData("round", 1, 0)]
    [InlineData("juror", 2, 1)]
    public async Task Partial_judgments_remain_durable_without_a_completed_candidate_or_provider_secret(
        string boundary, int rounds, int jurors)
    {
        const string secret = "test-only-provider-token";
        var store = new SerializedEvaluationStore();
        var fixture = new EvaluationFixture { Store = store };
        fixture.Models.Failure = (route, _) => (boundary == "round" && fixture.Models.Requests.Count == 10
            || boundary == "juror" && route == "juror-2") ? new HttpRequestException("Unauthorized bearer " + secret) : null;

        var run = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);

        var durable = (await new SerializedEvaluationStore(store.Export()).LoadAsync(run.Id, CancellationToken.None))!;
        var proposal = Assert.Single(durable.Proposals);
        Assert.Equal(RunStatus.Error, durable.Status);
        Assert.Equal(rounds, proposal.CouncilReview!.Rounds.Count);
        Assert.Equal(jurors, proposal.CouncilReview.JuryVerdicts.Count);
        Assert.Equal(ProposalVerdict.Error, proposal.CouncilReview.Outcome);
        Assert.DoesNotContain("s5_council", durable.Checkpoints);
        Assert.Null(proposal.IntendedPublication);
        Assert.Empty(durable.PreviewedIssues);
        Assert.Empty(fixture.Filer.Filed);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(durable));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("claim")]
    [InlineData("round")]
    [InlineData("juror-model")]
    [InlineData("evidence")]
    [InlineData("review-evidence")]
    [InlineData("context")]
    public async Task A_changed_serialized_candidate_or_incomplete_review_cannot_skip_evaluation_and_reach_publication_shape(string damage)
    {
        string? checkpoint = null;
        var store = new SerializedEvaluationStore
        {
            AfterSave = (run, station) =>
            {
                if (station == "s5_council" && run.Checkpoints.Contains(station))
                    checkpoint = JsonSerializer.Serialize(run);
            },
        };
        var fixture = new EvaluationFixture { Store = store };
        await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);
        var serialized = System.Text.Json.Nodes.JsonNode.Parse(checkpoint!)!;
        var proposal = serialized["Proposals"]![0]!;
        if (damage == "title") proposal["Title"] = "Every volunteer is abandoning all shifts";
        if (damage == "body") proposal["Body"] = "All coordinators have stopped using the application.";
        if (damage == "claim") proposal["Synthesis"]!["Claims"]![0]!["Text"] = "All volunteers abandoned their shifts.";
        if (damage == "round") proposal["CouncilReview"]!["Rounds"]![0]!["Verdicts"]!.AsArray().RemoveAt(0);
        if (damage == "juror-model") proposal["CouncilReview"]!["JuryVerdicts"]![2]!["Model"]!["Family"] = "gpt";
        if (damage == "evidence") proposal["ClusterEvidence"]!.AsArray().Clear();
        if (damage == "review-evidence") proposal["CouncilReview"]!["Evidence"]![0]!["Summary"] = "A different application failed its assignment flow.";
        if (damage == "context") serialized["EvaluationContext"]!["ManifestRevision"] = new string('c', 64);
        var restored = serialized.Deserialize<ConveyorRun>()!;
        var independent = new EvaluationFixture { Store = new SerializedEvaluationStore() };

        await ConveyorLine.RunAsync(restored, independent.Services(), CancellationToken.None);

        Assert.Equal(RunStatus.Error, restored.Status);
        Assert.DoesNotContain("s6_routing", restored.Checkpoints);
        Assert.Empty(restored.PreviewedIssues);
        Assert.Empty(independent.Models.Requests);
        Assert.Empty(independent.Filer.Filed);
    }

    [Theory]
    [InlineData("round", 8, 8)]
    [InlineData("juror", 14, 2)]
    public async Task Cancellation_between_persisted_judgments_stops_dispatch_and_an_independent_worker_keeps_completed_work(
        string boundary, int beforeCancellation, int remaining)
    {
        using var cancellation = new CancellationTokenSource();
        var store = new SerializedEvaluationStore();
        var fixture = new EvaluationFixture { Store = store };
        var initial = fixture.Run();
        store.AfterSave = (run, station) =>
        {
            var review = run.Proposals.SingleOrDefault()?.CouncilReview;
            if (station == "s5_council" && (boundary == "round" && review?.Rounds.Count == 1
                || boundary == "juror" && review?.JuryVerdicts.Count == 1))
                cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConveyorLine.RunAsync(initial, fixture.Services(), cancellation.Token));

        Assert.Equal(beforeCancellation, fixture.Models.Requests.Count);
        var independent = new SerializedEvaluationStore(store.Export());
        var loaded = (await independent.LoadAsync(initial.Id, CancellationToken.None))!;
        Assert.Equal(RunStatus.Open, loaded.Status);
        Assert.DoesNotContain("s5_council", loaded.Checkpoints);
        var replacement = new EvaluationFixture { Store = independent };
        var completed = await ConveyorLine.RunAsync(loaded, replacement.Services(), CancellationToken.None);
        Assert.True(completed.Status == RunStatus.Previewed, completed.FailureReason);
        Assert.Equal(remaining, replacement.Models.Requests.Count);
        Assert.Equal(2, completed.Proposals.Single().CouncilReview!.Rounds.Count);
        Assert.Equal(3, completed.Proposals.Single().CouncilReview!.JuryVerdicts.Count);
        Assert.Empty(replacement.Filer.Filed);
    }

    [Fact]
    public async Task Independent_serialized_workers_retain_the_complete_candidate_and_never_redrive_a_terminal_run()
    {
        var store = new SerializedEvaluationStore();
        var fixture = new EvaluationFixture { Store = store };
        var evaluated = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);
        var independent = new SerializedEvaluationStore(store.Export());
        var loaded = await independent.LoadAsync(evaluated.Id, CancellationToken.None);

        var proposal = Assert.Single(loaded!.Proposals);
        Assert.Equal("Explain volunteer assignment conflicts", proposal.Title);
        Assert.Equal(2, proposal.CouncilReview!.Rounds.Count);
        Assert.Equal(3, proposal.CouncilReview.JuryVerdicts.Count);
        Assert.Equal(16, loaded.ModelObservations.Count);
        Assert.Single(loaded.SynthesisEvaluations);
        Assert.Single(loaded.PreviewedIssues);
        Assert.Single(proposal.Labels);
        Assert.Equal(ConveyorLine.StationNames, loaded.Checkpoints);
        var replacement = new EvaluationFixture { Store = independent };
        var returned = await ConveyorLine.RunAsync(loaded, replacement.Services(), CancellationToken.None);
        Assert.Same(loaded, returned);
        Assert.Empty(replacement.Models.Requests);
        Assert.Empty(replacement.Filer.Filed);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("dropped")]
    [InlineData("duplicate")]
    [InlineData("invented")]
    public async Task Async_embedding_groups_account_for_every_genuine_observation(string grouping)
    {
        var fixture = new EvaluationFixture
        {
            Items = [EvaluationFixture.Record, new("azuremonitor", "telemetry:assignment-2",
                "A second coordinator's overlapping volunteer assignment was rejected by the deployed assignment endpoint.")],
        };
        fixture.Models.Grouping = grouping;

        var run = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);

        Assert.Single(fixture.Models.Requests, r => r.Route == "embedding");
        if (grouping == "complete")
        {
            Assert.True(run.Status == RunStatus.Previewed, run.FailureReason);
            Assert.Equal(["telemetry:assignment-1", "telemetry:assignment-2"], Assert.Single(run.Proposals).EvidenceReferences);
            Assert.Equal("embedding", run.ModelObservations[0].Model.Route);
        }
        else
        {
            Assert.Equal(RunStatus.Error, run.Status);
            Assert.DoesNotContain("s3_synthesis", run.Checkpoints);
            Assert.Empty(run.Proposals);
            Assert.DoesNotContain(fixture.Models.Requests, r => r.Route != "embedding");
        }
        Assert.Empty(fixture.Filer.Filed);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("adoption")]
    [InlineData("analysis")]
    [InlineData("scope")]
    [InlineData("juror")]
    [InlineData("family")]
    [InlineData("lenses")]
    [InlineData("live")]
    [InlineData("downgrade")]
    [InlineData("unknown-profile")]
    public async Task Missing_or_incompatible_profile_inputs_fail_before_investigation_or_model_effects(string missing)
    {
        var fixture = new EvaluationFixture();
        var context = EvaluationFixture.Context();
        var routes = context.Models.Routes.ToArray();
        if (missing == "adoption") context = context with { Adoption = context.Adoption with { Revision = Guid.Empty } };
        if (missing == "analysis") context = context with { Intent = context.Intent with { Analysis = context.Intent.Analysis with { State = IntentState.Partial } } };
        if (missing == "scope") context = context with { Application = context.Application with { ApplicationResourceIds = [] } };
        if (missing == "juror") context = context with { Models = context.Models with { Routes = routes[..^1] } };
        if (missing == "family")
        {
            routes[4] = routes[4] with { Format = "OpenAI", Family = "gpt", Model = "gpt-4.1" };
            context = context with { Models = context.Models with { Routes = routes } };
        }
        if (missing == "lenses") context = context with { Judgment = context.Judgment with { Lenses = [] } };
        var run = new ConveyorRun
        {
            Profile = missing == "downgrade" ? ConveyorProfile.Greenfield
                : missing == "unknown-profile" ? (ConveyorProfile)99 : ConveyorProfile.OnboardingPreview,
            EvaluationContext = missing == "context" ? null : context,
            DryRun = missing != "live",
            ProductHints = ["acme"], SourceKinds = ["azuremonitor"],
        };

        await ConveyorLine.RunAsync(run, fixture.Services(), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Empty(run.Checkpoints);
        Assert.Empty(run.Evidence);
        Assert.Empty(fixture.Models.Requests);
        Assert.Empty(fixture.Filer.Filed);
        Assert.NotNull(run.FailureReason);
    }

    [Theory]
    [InlineData(3, ProposalVerdict.Proceed, RunStatus.Previewed)]
    [InlineData(2, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData(1, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData(0, ProposalVerdict.Kill, RunStatus.Killed)]
    public async Task Real_deliberation_and_three_model_families_finish_without_creation_maturity_or_filing(
        int goVotes, ProposalVerdict disposition, RunStatus status)
    {
        var fixture = new EvaluationFixture();
        fixture.Models.GoVotes = goVotes;

        var run = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services() with { ProductMaturity = "low" }, CancellationToken.None);

        Assert.True(run.Status == status, run.FailureReason);
        var proposal = Assert.Single(run.Proposals);
        Assert.Equal(disposition, proposal.Verdict);
        var review = Assert.IsType<CouncilReview>(proposal.CouncilReview);
        Assert.Equal(2, review.Rounds.Count);
        Assert.All(review.Rounds, round => Assert.Equal(5, round.Verdicts.Count));
        Assert.Equal(3, review.JuryVerdicts.Count);
        Assert.NotNull(proposal.Synthesis);
        Assert.True(proposal.Grounding!.Result.Complete);
        Assert.True(proposal.Grounding.Result.ContentSupported);
        Assert.Equal(16, run.ModelObservations.Count);
        Assert.Equal(["gpt", "deepseek", "grok"], review.JuryVerdicts.Select(v => v.Model!.Family));
        Assert.Equal(3, fixture.Models.Requests.Count(r => r.Route.StartsWith("juror-", StringComparison.Ordinal)));
        Assert.All(fixture.Models.Requests.Where(r => r.Route != "embedding"), request =>
        {
            Assert.Contains("Coordinate volunteer shifts without double booking.", request.Input);
            Assert.Contains("Business success measures are unknown.", request.Input);
            Assert.Contains("/applications/shifts", request.Input);
            Assert.Contains("overlapping volunteer assignment was rejected", request.Input);
        });
        Assert.Empty(fixture.Filer.Filed);
        Assert.Empty(run.FiledIssues);
        Assert.DoesNotContain(proposal.Labels, label => label != "dsf:proposal");
        var payload = JsonSerializer.SerializeToNode(proposal)!["IntendedPublication"]!;
        Assert.Equal(proposal.Title, (string?)payload?["Title"]);
        Assert.Contains(proposal.Body!, (string?)payload?["Body"]);
        var body = proposal.IntendedPublication!.Body!;
        Assert.Contains("## Supporting observations", body);
        Assert.Contains("## Material claims and semantic support", body);
        Assert.Contains("## Uncertainty", body);
        Assert.Contains("Business success measures are unknown.", body);
        Assert.Contains("## Deliberation", body);
        Assert.All(review.JuryVerdicts, vote => Assert.Contains($"{vote.JurorName} ({vote.Model!.Family}): {vote.Position}", body));
        Assert.Contains($"Disposition: {disposition}", body);
        Assert.Empty(payload!["Assignees"]!.AsArray());
        Assert.Equal("dsf:proposal", (string?)Assert.Single(payload["Labels"]!.AsArray()));
        if (disposition == ProposalVerdict.Proceed)
            Assert.Equal("dsf:proposal", Assert.Single(Assert.Single(run.PreviewedIssues).Labels));
        else Assert.DoesNotContain("s6_routing", run.Checkpoints);
    }

    [Fact]
    public async Task An_existing_citation_does_not_semantically_support_an_invented_prevalence_claim()
    {
        var fixture = new EvaluationFixture();
        fixture.Models.SynthesisAnswer = EvaluationFixture.Synthesis.Replace("An overlapping assignment was rejected.",
            "Most coordinators abandon their shifts after assignment conflicts.", StringComparison.Ordinal);
        fixture.Models.GroundingAnswer = """
            {"complete":true,"contentSupported":false,"explanation":"One rejection does not establish abandonment or prevalence.",
            "checks":[{"claimId":"conflict","support":"Unsupported","explanation":"The record contains no abandonment or prevalence observation.",
            "citations":[{"reference":"telemetry:assignment-1","excerpt":"overlapping volunteer assignment was rejected"}],
            "counterevidence":[]}],"limitations":["Prevalence and abandonment are unknown."]}
            """;

        var run = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.DoesNotContain("s4_grounding", run.Checkpoints);
        Assert.DoesNotContain("s5_council", run.Checkpoints);
        Assert.Contains(run.ModelObservations, observation => observation.Step.StartsWith("grounding:", StringComparison.Ordinal));
        Assert.Contains("Unsupported", JsonSerializer.Serialize(run.Proposals.Single()));
        Assert.Empty(run.PreviewedIssues);
        Assert.Empty(fixture.Filer.Filed);
        Assert.DoesNotContain(fixture.Models.Requests, request => request.Route.StartsWith("juror-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Structured_synthesis_becomes_the_proposal_content_not_the_first_evidence_summary()
    {
        var fixture = new EvaluationFixture();

        var run = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);

        var proposal = Assert.Single(run.Proposals);
        Assert.Equal("Explain volunteer assignment conflicts", proposal.Title);
        Assert.Equal("Show which existing assignment conflicts before a coordinator submits another shift.", proposal.Body);
        Assert.DoesNotContain(run.Audit, entry => entry.Message.Contains(EvaluationFixture.Synthesis, StringComparison.Ordinal));
    }
}

internal sealed class EvaluationFixture
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
    internal static readonly Guid Factory = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly string Revision = new('a', 64);
    internal static readonly EvidenceItem Record = new("azuremonitor", "telemetry:assignment-1",
        "A coordinator's overlapping volunteer assignment was rejected by the deployed assignment endpoint.");
    internal const string Synthesis = """
        {"complete":true,"candidate":{"title":"Explain volunteer assignment conflicts",
        "body":"Show which existing assignment conflicts before a coordinator submits another shift.",
        "claims":[{"id":"conflict","text":"An overlapping assignment was rejected.",
        "evidenceReferences":["telemetry:assignment-1"]}],
        "uncertainties":["The frequency of assignment conflicts is not established."]},"reason":"Observed rejection motivates explaining the existing conflict."}
        """;
    internal readonly EvaluationWorkloads Models = new();
    internal readonly RecordingIssueFiler Filer = new();
    internal IRunStore Store = new RecordingRunStore();
    internal EvidenceItem[] Items = [Record];

    internal ConveyorRun Run() => new()
    {
        Profile = ConveyorProfile.OnboardingPreview,
        EvaluationContext = Context() with
        {
            Evidence = Items.Select(e => new CouncilEvidenceProvenance(e.SourceKind, e.Reference,
                FactoryModelsValidation.Hash(e.Summary), Revision, "private:telemetry", Now)).ToArray(),
        },
        ProductHints = ["acme"],
        SourceKinds = ["azuremonitor"],
        DryRun = true,
    };

    internal ConveyorServices Services() => ConveyorDoubles.Services(
        gatherers: [new CountingEvidenceGatherer("azuremonitor", Items)], issueFiler: Filer, runStore: Store,
        modelClient: new RecordingModelClient(Synthesis)) with { EvaluationModels = Models };

    internal static CouncilEvaluationContext Context()
    {
        var draft = new GroundedCharterDraft("acme", "Coordinate volunteer shifts without double booking.",
            "Volunteer coordinators arranging staffed shifts.", ["Prevent volunteers from receiving overlapping assignments."],
            new(new(CharterKnowledgeState.Unknown, "No authoritative non-goals have been recorded."), []),
            new(new(CharterKnowledgeState.Unknown, "No authoritative success measures have been recorded."), []));
        var charter = OnboardingCharterMarkdown.Render(draft);
        var blob = CharterMarkdown.GitBlobSha(charter);
        var snapshot = new SourceSnapshot(1, Factory, Factory, Revision, 913, 42, "acme/shifts", "release/2026",
            new('b', 40), new('c', 40), Now, Revision);
        var progress = new IntentProgress(1, Factory, 1, snapshot, Revision, Revision, 1)
        {
            State = IntentState.DraftReady, AnalyzedChunks = 1, CheckedChunks = 1, ChainSha256 = Revision,
            ModelAttempts = 2, ModelOutcome = IntentModelOutcome.Confirmed,
        };
        var claim = new IntentClaim(IntentClaimField.Goal, IntentClaimKind.ImplementedBehavior,
            draft.Goals[0], "Pinned repository", "Current complete source",
            [new(1, 0, "rejects overlapping assignments", "The source enforces non-overlapping assignments.")]);
        var source = new SourceChunkReference(Revision, 1, 0, Revision);
        var capability = new ModelCapabilityReference(Factory, Revision, "private:models", Revision);
        var observation = Observation("synthesis", "gpt-4.1", Synthesis);
        var candidate = new IntentCandidate(true, "Assignment validation rejects overlapping shifts.", [claim],
            draft.NonGoals.Knowledge, draft.SuccessMeasures.Knowledge, ["Deployment correlation is unknown."]);
        var verification = new IntentVerification(true,
            [new(0, IntentSupportVerdict.Supports, "The check rejects an overlap.", "rejects overlapping assignments")], []);
        var analysis = new IntentResult(IntentState.DraftReady, "Complete pinned source analysis.", progress,
            new(snapshot) { State = SourceState.Acquired, CompleteRootTree = true, IntegritySha256 = Revision },
            draft, [new(0, claim, true, false, [new(source, "Assignments.cs", "private:source", 0, 100)])],
            [new(Factory, new(Factory, 0, false, Revision, capability), source, null, observation, candidate, null, []),
                new(Factory, new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 0, true, Revision, capability),
                    source, Revision, observation, null, verification, [])],
            ["Deployment correlation is unknown.", "Business success measures are unknown."], null);
        var adoption = new CharterAdoption(1, Factory, Factory, 1, "UnchangedExisting", Revision, blob,
            FactoryModelsValidation.Hash(charter), snapshot.Commit, snapshot.RootTree, progress.Revision,
            Revision, snapshot.Id, Revision, Revision, 56, Now, null, null, 1, Revision);
        return new(Factory, Revision, adoption,
            new(analysis, new(snapshot, charter, blob) { AnalyzedCharterBlob = blob, Reconciliation = "ExactAnalyzed" }),
            new("production", ["/applications/shifts"], ["/evidence/shifts"]),
            [new(Record.SourceKind, Record.Reference, FactoryModelsValidation.Hash(Record.Summary), Revision, "private:telemetry", Now)],
            new(Revision, Routes()),
            new(2, 0.6, ModelDeliberationLens.Default().Select(l => new CouncilLensConfiguration(l.Name, l.Weight)).ToArray()));
    }

    internal static ModelRouteSelection[] Routes() =>
        [Route("synthesis", "OpenAI", "gpt", "gpt-4.1"), Route("embedding", "OpenAI", "text-embedding", "text-embedding-3-small", 1536),
            Route("juror-1", "OpenAI", "gpt", "gpt-4.1"), Route("juror-2", "DeepSeek", "deepseek", "deepseek-v3"),
            Route("juror-3", "xAI", "grok", "grok-3")];

    private static ModelRouteSelection Route(string name, string format, string family, string model, int dimensions = 0) =>
        new(name, name is "synthesis" or "embedding", "/models/" + name, name, format, family, model, "1",
            "Standard", 1, dimensions, Factory.ToString(), "eastus", "https://" + name + ".services.ai.azure.com",
            true, "Reviewed private connectivity", "/roles/inference");

    internal static ModelWorkloadObservation Observation(string route, string model, string output, int dimensions = 0,
        IReadOnlyList<IReadOnlyList<string>>? groups = null) =>
        new(route, Revision, ModelCapabilityState.Verified, ModelCapabilityReason.None, model, Factory.ToString(),
            FactoryModelsValidation.Hash(output), output.Length, dimensions, groups ?? []);
}

internal sealed class EvaluationWorkloads : IFactoryModelWorkloads
{
    internal readonly List<(string Route, string Input)> Requests = [];
    internal string SynthesisAnswer = EvaluationFixture.Synthesis;
    internal int GoVotes = 3;
    internal string Grouping = "complete";
    internal Func<string, string, Exception?>? Failure { get; set; }
    internal Func<string, string, string?>? AnswerOverride { get; set; }
    internal Func<ModelWorkloadObservation, ModelWorkloadObservation>? ObservationOverride { get; set; }
    internal string GroundingAnswer = """
        {"complete":true,"contentSupported":true,"explanation":"The proposed change responds to the observed rejected overlap.",
        "checks":[{"claimId":"conflict","support":"Supported","explanation":"The record reports an overlapping assignment rejected.",
        "citations":[{"reference":"telemetry:assignment-1","excerpt":"overlapping volunteer assignment was rejected"}],
        "counterevidence":[]}],"limitations":["One record cannot establish prevalence."]}
        """;

    public Task<ModelTextResult> SynthesizeAsync(string input, CancellationToken token)
    {
        RecordRequest("synthesis", input);
        var answer = input.Contains("SEMANTIC_GROUNDING", StringComparison.Ordinal) ? GroundingAnswer
            : input.Contains("PROPOSAL_SYNTHESIS", StringComparison.Ordinal) ? SynthesisAnswer
            : "GO: Explaining the observed conflict supports coordinators without assuming conflict prevalence or changing assignment policy.";
        return Result("synthesis", "gpt-4.1", input, answer);
    }

    public Task<ModelTextResult> JudgeAsync(string juror, string input, CancellationToken token)
    {
        RecordRequest(juror, input);
        var route = EvaluationFixture.Routes().Single(r => r.Name == juror);
        var answer = int.Parse(juror[^1..]) <= GoVotes
            ? "GO: The observed rejection supports this bounded proposal; uncertainty about frequency remains explicit for human review."
            : "NO-GO: The single observation is genuine but the expected value is too uncertain to justify this change.";
        return Result(juror, route.Model, input, answer);
    }

    public Task<ModelDeduplicationResult> DeduplicateAsync(IReadOnlyList<ModelText> input, CancellationToken token)
    {
        RecordRequest("embedding", JsonSerializer.Serialize(input));
        IReadOnlyList<IReadOnlyList<string>> groups = Grouping switch
        {
            "dropped" => [[input[0].Id]],
            "duplicate" => [[input[0].Id], input.Select(i => i.Id).ToArray()],
            "invented" => [input.Select(i => i.Id).Append("invented-id").ToArray()],
            _ => [input.Select(i => i.Id).ToArray()],
        };
        return Task.FromResult(new ModelDeduplicationResult(groups,
            EvaluationFixture.Observation("embedding", "text-embedding-3-small", JsonSerializer.Serialize(groups), 1536, groups)));
    }

    private void RecordRequest(string route, string input)
    {
        Requests.Add((route, input));
        if (Failure?.Invoke(route, input) is { } exception) throw exception;
    }

    private Task<ModelTextResult> Result(string route, string model, string input, string answer)
    {
        answer = AnswerOverride?.Invoke(route, input) ?? answer;
        var observation = EvaluationFixture.Observation(route, model, answer);
        return Task.FromResult(new ModelTextResult(answer, ObservationOverride?.Invoke(observation) ?? observation));
    }
}
