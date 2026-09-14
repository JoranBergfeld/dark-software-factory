using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

public sealed class JudgmentAcceptanceTests
{
    [Theory]
    [InlineData("low", 0, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("low", 1, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("low", 2, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("low", 3, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("medium", 0, ProposalVerdict.Kill, RunStatus.Killed)]
    [InlineData("medium", 1, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("medium", 2, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("medium", 3, ProposalVerdict.Proceed, RunStatus.Open)]
    [InlineData("high", 0, ProposalVerdict.Kill, RunStatus.Killed)]
    [InlineData("high", 1, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("high", 2, ProposalVerdict.Escalate, RunStatus.Escalated)]
    [InlineData("high", 3, ProposalVerdict.Proceed, RunStatus.Open)]
    public async Task Jury_policy_covers_every_maturity_and_vote_split(
        string maturity, int goVotes, ProposalVerdict outcome, RunStatus status)
    {
        var run = ProposalRun();
        var jurors = Enumerable.Range(0, 3).Select(index =>
            (IValidationJuror)new FixedJuror($"juror-{index}", index < goVotes ? JurorPosition.Go : JurorPosition.NoGo)).ToArray();

        await new S5Council().RunAsync(run, ConveyorDoubles.Services(
            validationJurors: jurors, productMaturity: maturity), CancellationToken.None);

        Assert.Equal(outcome, run.Proposals.Single().Verdict);
        Assert.Equal(outcome, run.Proposals.Single().CouncilReview!.Outcome);
        Assert.Equal(status, run.Status);
    }

    [Fact]
    public async Task Typed_review_round_trips_as_readable_json_and_resumes_without_rejudgment()
    {
        var run = ProposalRun();
        var services = ConveyorDoubles.Services(validationJurors: Jury(JurorPosition.Go));
        await new S5Council().RunAsync(run, services, CancellationToken.None);
        var json = JsonSerializer.Serialize(run.Proposals.Single().CouncilReview);

        Assert.Contains("\"Outcome\":\"Proceed\"", json);
        run.Proposals.Single().CouncilReview = JsonSerializer.Deserialize<CouncilReview>(json);
        await new S5Council().RunAsync(run, ConveyorDoubles.Services(
            validationJurors: [new MalformedJuror("a"), new MalformedJuror("b"), new MalformedJuror("c")]),
            CancellationToken.None);

        var review = Assert.IsType<CouncilReview>(run.Proposals.Single().CouncilReview);
        Assert.Equal(2, review.Rounds.Count);
        Assert.Equal(3, review.JuryVerdicts.Count);
        Assert.Equal(2, review.Evidence.Count);
        Assert.Equal(ProposalVerdict.Proceed, review.Outcome);
    }

    [Fact]
    public async Task An_accepted_legacy_s5_checkpoint_is_not_filing_authority_without_a_typed_review()
    {
        var run = ProposalRun();
        run.DryRun = true;
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(5));
        run.Proposals.Single().Verdict = ProposalVerdict.Proceed;

        await ConveyorLine.RunAsync(run, ConveyorDoubles.Services(), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Empty(run.PreviewedIssues);
        Assert.DoesNotContain(S6Routing.StationName, run.Checkpoints);
    }

    [Fact]
    public async Task An_incomplete_saved_proceed_record_cannot_skip_validation_and_file()
    {
        var run = ProposalRun();
        run.DryRun = true;
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        run.Proposals.Single().CouncilReview = new CouncilReview { Outcome = ProposalVerdict.Proceed };

        await ConveyorLine.RunAsync(run, ConveyorDoubles.Services(), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal(ProposalVerdict.Error, run.Proposals.Single().Verdict);
        Assert.Empty(run.PreviewedIssues);
    }

    [Fact]
    public async Task A_juror_ignoring_cancellation_cannot_block_the_station_indefinitely()
    {
        var run = ProposalRun();
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        var services = ConveyorDoubles.Services(validationJurors:
            [new FixedJuror("a", JurorPosition.Go), new HangingJuror("b"), new FixedJuror("c", JurorPosition.Go)])
            with { JuryTimeout = TimeSpan.FromMilliseconds(20) };

        await ConveyorLine.RunAsync(run, services, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains("b", run.FailureReason);
    }

    [Theory]
    [InlineData("unparseable")]
    [InlineData("GOOSE: mistaken")]
    [InlineData("ABSTAIN")]
    [InlineData("GO:")]
    public async Task Malformed_lens_results_are_errors_not_silent_abstentions(string answer)
    {
        var run = ProposalRun();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelDeliberationLens.Value().DeliberateAsync(
            run.Proposals.Single(), run, [], new RecordingModelClient(answer), CancellationToken.None));
    }

    [Fact]
    public async Task See_and_revise_prompts_include_evidence_and_peer_rationales_not_just_votes()
    {
        var run = ProposalRun();
        var model = new RecordingModelClient();

        await ModelDeliberationLens.Value().DeliberateAsync(run.Proposals.Single(), run,
            [new LensVerdict("security", LensPosition.NoGo, "exposes account identifiers", 1)],
            model, CancellationToken.None);

        var prompt = Assert.Single(model.Prompts);
        Assert.Contains("exposes account identifiers", prompt);
        Assert.Contains("monitor-1", prompt);
        Assert.Contains("web-1", prompt);
        Assert.Contains("checkout timeout", prompt);
    }

    [Fact]
    public void Weighted_recommendation_is_deterministic_and_keeps_disagreement()
    {
        var verdicts = new LensVerdict[]
        {
            new("value", LensPosition.Go, "strong value", 3),
            new("cost", LensPosition.NoGo, "too costly", 1),
            new("feasibility", LensPosition.Abstain, "needs investigation", 2),
        };

        var first = LensSynthesizer.Synthesize(verdicts, 0.6);
        var reversed = LensSynthesizer.Synthesize(verdicts.Reverse().ToArray(), 0.6);

        Assert.Equal(2d / 3d, first.Confidence, 12);
        Assert.True(first.Proceed);
        Assert.True(first.Disagreement);
        Assert.Equal(first.Confidence, reversed.Confidence);
    }

    [Fact]
    public async Task Unconfigured_jury_never_falls_back_to_the_shared_deliberation_model()
    {
        var run = ProposalRun();
        run.DryRun = true;
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        var services = new ConveyorServices("acme", [], null, new RecordingRunStore(),
            new RecordingModelClient(), new RecordingTracer(), new FixedConfidenceThresholdReader(0.6));

        await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains("three", run.FailureReason);
        Assert.DoesNotContain(S6Routing.StationName, run.Checkpoints);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Configured_round_count_is_preserved_in_the_typed_review(int rounds)
    {
        var run = ProposalRun();
        var services = ConveyorDoubles.Services(validationJurors: Jury(JurorPosition.Go))
            with { DeliberationRounds = rounds };

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        Assert.Equal(rounds, run.Proposals.Single().CouncilReview!.Rounds.Count);
        Assert.Equal(rounds, run.Proposals.Single().CouncilReview!.RoundsRequested);
    }

    [Fact]
    public async Task Resume_keeps_completed_rounds_and_juror_results_without_reinvoking_them()
    {
        var run = ProposalRun();
        var verdicts = ModelDeliberationLens.Default()
            .Select(lens => new LensVerdict(lens.Name, LensPosition.Go, "evidence supports it", 1)).ToArray();
        run.Proposals.Single().CouncilReview = new CouncilReview
        {
            Evidence = run.Evidence.ToArray(),
            ProductMaturity = "high",
            Threshold = 0.6,
            JurorNames = ["a", "b", "c"],
            Rounds = [new DeliberationRound(1, verdicts), new DeliberationRound(2, verdicts)],
            Recommendation = new LensSynthesis(true, 1, false, verdicts),
            JuryVerdicts = [new JurorVerdict("a", JurorPosition.Go, "already checked")],
        };
        var jurors = new IValidationJuror[]
        {
            new MalformedJuror("a"), new FixedJuror("b", JurorPosition.Go), new FixedJuror("c", JurorPosition.Go),
        };

        await new S5Council().RunAsync(run, ConveyorDoubles.Services(
            modelClient: new RecordingModelClient("must not deliberate again"), validationJurors: jurors),
            CancellationToken.None);

        Assert.Equal(ProposalVerdict.Proceed, run.Proposals.Single().Verdict);
        Assert.Equal("already checked", run.Proposals.Single().CouncilReview!.JuryVerdicts[0].Rationale);
    }

    [Fact]
    public async Task A_juror_timeout_becomes_a_typed_error_and_retains_completed_judgments()
    {
        var run = ProposalRun();
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        var jurors = new IValidationJuror[]
        {
            new FixedJuror("a", JurorPosition.Go), new CancelledJuror("b"), new FixedJuror("c", JurorPosition.Go),
        };

        await ConveyorLine.RunAsync(run, ConveyorDoubles.Services(validationJurors: jurors), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal(ProposalVerdict.Error, run.Proposals.Single().Verdict);
        Assert.Equal(ProposalVerdict.Error, run.Proposals.Single().CouncilReview!.Outcome);
        Assert.Single(run.Proposals.Single().CouncilReview!.JuryVerdicts);
        Assert.Contains("b", run.FailureReason);
        Assert.DoesNotContain(S6Routing.StationName, run.Checkpoints);
    }

    [Fact]
    public async Task Escalation_persists_typed_evidence_all_rounds_recommendation_and_jury_results()
    {
        var run = ProposalRun();
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        var store = new RecordingRunStore();

        await ConveyorLine.RunAsync(run, ConveyorDoubles.Services(
            runStore: store, validationJurors: Jury(JurorPosition.Go), productMaturity: "low"), CancellationToken.None);

        var persisted = Assert.IsType<ConveyorRun>(await store.LoadAsync(run.Id, CancellationToken.None));
        var review = Assert.IsType<CouncilReview>(persisted.Proposals.Single().CouncilReview);
        Assert.Equal(run.Evidence, review.Evidence);
        Assert.Equal(2, review.Rounds.Count);
        Assert.All(review.Rounds, round => Assert.Equal(5, round.Verdicts.Count));
        Assert.True(review.Recommendation!.Proceed);
        Assert.Equal(3, review.JuryVerdicts.Count);
        Assert.Equal(ProposalVerdict.Escalate, review.Outcome);
        Assert.DoesNotContain(S6Routing.StationName, persisted.Checkpoints);
        Assert.DoesNotContain(S7Filing.StationName, persisted.Checkpoints);
        Assert.Empty(persisted.FiledIssues);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task An_incomplete_or_oversized_jury_errors_at_s5_instead_of_proceeding(int count)
    {
        var run = ProposalRun();
        run.DryRun = true;
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        var jurors = Enumerable.Range(0, count)
            .Select(index => (IValidationJuror)new FixedJuror($"juror-{index}", JurorPosition.Go)).ToArray();

        await ConveyorLine.RunAsync(run, ConveyorDoubles.Services(validationJurors: jurors), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains(S5Council.StationName, run.FailureReason);
        Assert.DoesNotContain(S6Routing.StationName, run.Checkpoints);
    }

    [Theory]
    [InlineData("GOOSE: looks fine")]
    [InlineData("NO-GOOD: looks wrong")]
    [InlineData("GO")]
    [InlineData("GO:")]
    [InlineData("NO-GO")]
    [InlineData("ABSTAIN: unsure")]
    public async Task Malformed_or_incomplete_model_votes_cannot_become_a_juror_verdict(string answer)
    {
        var juror = new ModelValidationJuror("a", "check evidence", new RecordingModelClient(answer));

        await Assert.ThrowsAsync<InvalidOperationException>(() => juror.ValidateAsync(
            ProposalRun().Proposals.Single(), ProposalRun(),
            new LensSynthesis(true, 1, false, []), CancellationToken.None));
    }

    [Fact]
    public async Task Low_maturity_escalates_even_when_lenses_and_jurors_recommend_no_go()
    {
        var run = ProposalRun();
        var services = ConveyorDoubles.Services(
            deliberationLenses: [new FixedLens("value", LensPosition.NoGo)],
            validationJurors: Jury(JurorPosition.NoGo), productMaturity: "low");

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        Assert.Equal(ProposalVerdict.Escalate, run.Proposals.Single().Verdict);
        Assert.Equal(RunStatus.Escalated, run.Status);
    }

    private static ConveyorRun ProposalRun()
    {
        var run = new ConveyorRun { SourceKinds = ["azuremonitor", "webiq"], ProductHints = ["acme"] };
        run.Evidence.Add(new EvidenceItem("azuremonitor", "monitor-1", "checkout timeout"));
        run.Evidence.Add(new EvidenceItem("webiq", "web-1", "checkout timeout"));
        run.Proposals.Add(new Proposal("p1", "Fix checkout timeout", ["azuremonitor", "webiq"], ["monitor-1", "web-1"]));
        return run;
    }

    private static IValidationJuror[] Jury(JurorPosition position) =>
        [new FixedJuror("a", position), new FixedJuror("b", position), new FixedJuror("c", position)];

    private sealed class CancelledJuror(string name) : IValidationJuror
    {
        public string Name => name;
        public Task<JurorVerdict> ValidateAsync(
            Proposal proposal, ConveyorRun run, LensSynthesis lensSynthesis, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("model request timed out");
    }

    private sealed class HangingJuror(string name) : IValidationJuror
    {
        public string Name => name;
        public Task<JurorVerdict> ValidateAsync(
            Proposal proposal, ConveyorRun run, LensSynthesis lensSynthesis, CancellationToken cancellationToken) =>
            new TaskCompletionSource<JurorVerdict>().Task;
    }
}
