using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class CouncilReviewPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Resuming_an_approved_checkpoint_reapplies_current_low_maturity(bool alreadyRouted, bool dryRun)
    {
        var gateway = new DocumentGateway();
        var store = Store(gateway);
        var run = ProposalRun();
        run.DryRun = dryRun;
        var high = Services(store, new Completion(), Jury(), "high");
        await new S5Council().RunAsync(run, high, CancellationToken.None);
        run.Checkpoints.Add(S5Council.StationName);
        if (alreadyRouted)
        {
            await new S6Routing().RunAsync(run, high, CancellationToken.None);
            run.Checkpoints.Add(S6Routing.StationName);
        }
        await store.SaveAsync(run, alreadyRouted ? S6Routing.StationName : S5Council.StationName, CancellationToken.None);
        var restored = Assert.IsType<ConveyorRun>(await store.LoadAsync(run.Id, CancellationToken.None));

        await ConveyorLine.RunAsync(restored, Services(store, new ForbiddenCompletion(), Jury(), "low"), CancellationToken.None);

        Assert.Equal(RunStatus.Escalated, restored.Status);
        Assert.Empty(restored.FiledIssues);
        Assert.Empty(restored.PreviewedIssues);
        var review = Assert.IsType<CouncilReview>(Assert.Single(restored.Proposals).CouncilReview);
        Assert.Equal(ProposalVerdict.Escalate, review.Outcome);
        Assert.Equal(3, review.JuryVerdicts.Count);
        var saved = Assert.IsType<ConveyorRun>(await store.LoadAsync(run.Id, CancellationToken.None));
        Assert.Equal(RunStatus.Escalated, saved.Status);
    }

    [Theory]
    [InlineData("jury-position")]
    [InlineData("lens-position")]
    [InlineData("threshold")]
    public async Task Incomplete_authorization_fields_in_saved_reviews_are_audited_errors(string field)
    {
        var gateway = new DocumentGateway();
        var store = Store(gateway);
        var run = ProposalRun();
        await new S5Council().RunAsync(run, Services(store, new Completion(), Jury(), "high"), CancellationToken.None);
        run.Checkpoints.Add(S5Council.StationName);
        await store.SaveAsync(run, S5Council.StationName, CancellationToken.None);
        var document = JsonNode.Parse(gateway.Documents[run.Id])!;
        var review = document["proposals"]![0]!["councilReview"]!;
        switch (field)
        {
            case "jury-position":
                review["juryVerdicts"]![0]!.AsObject().Remove("position");
                break;
            case "lens-position":
                review["rounds"]![0]!["verdicts"]![0]!.AsObject().Remove("position");
                break;
            case "threshold":
                review.AsObject().Remove("threshold");
                break;
        }
        gateway.Documents[run.Id] = document.ToJsonString();

        var restored = Assert.IsType<ConveyorRun>(await store.LoadAsync(run.Id, CancellationToken.None));

        Assert.Equal(RunStatus.Error, restored.Status);
        Assert.Equal(ProposalVerdict.Error, Assert.Single(restored.Proposals).Verdict);
        Assert.Contains("review", restored.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(restored.Audit);
        Assert.Empty(restored.PreviewedIssues);
    }

    [Fact]
    public async Task A_new_process_resumes_completed_rounds_and_jurors_from_the_cosmos_checkpoint()
    {
        var gateway = new DocumentGateway();
        var store = Store(gateway);
        var run = ProposalRun();
        using var cancellation = new CancellationTokenSource();
        var firstJury = Jury([new Completion(), new CancellingCompletion(cancellation), new Completion()]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConveyorLine.RunAsync(run, Services(store, new Completion(), firstJury, "high"), cancellation.Token));

        var restored = Assert.IsType<ConveyorRun>(await Store(gateway).LoadAsync(run.Id, CancellationToken.None));
        Assert.Equal(RunStatus.Open, restored.Status);
        Assert.DoesNotContain(S5Council.StationName, restored.Checkpoints);
        Assert.Single(restored.Proposals.Single().CouncilReview!.JuryVerdicts);
        var resumedJury = Jury([new ForbiddenCompletion(), new Completion(), new Completion()]);

        await ConveyorLine.RunAsync(restored,
            Services(Store(gateway), new ForbiddenCompletion(), resumedJury, "high"), CancellationToken.None);

        Assert.Equal(RunStatus.Previewed, restored.Status);
        Assert.Equal(ProposalVerdict.Proceed, restored.Proposals.Single().Verdict);
        Assert.Contains("creation:ready", Assert.Single(restored.PreviewedIssues).Labels);
        Assert.Equal(2, restored.Proposals.Single().CouncilReview!.Rounds.Count);
        Assert.Equal(3, restored.Proposals.Single().CouncilReview!.JuryVerdicts.Count);
    }

    [Fact]
    public async Task A_failed_juror_persists_the_typed_error_and_completed_review_results()
    {
        var gateway = new DocumentGateway();
        var run = ProposalRun();
        var jury = Jury([new Completion(), new ForbiddenCompletion(), new Completion()]);

        await ConveyorLine.RunAsync(run, Services(Store(gateway), new Completion(), jury, "high"), CancellationToken.None);

        var restored = Assert.IsType<ConveyorRun>(await Store(gateway).LoadAsync(run.Id, CancellationToken.None));
        var review = Assert.IsType<CouncilReview>(restored.Proposals.Single().CouncilReview);
        Assert.Equal(RunStatus.Error, restored.Status);
        Assert.Equal(ProposalVerdict.Error, review.Outcome);
        Assert.Contains("second", review.FailureReason);
        Assert.Equal(2, review.Rounds.Count);
        Assert.Single(review.JuryVerdicts);
        Assert.Empty(restored.FiledIssues);
    }

    [Fact]
    public async Task Escalation_round_trips_the_complete_typed_review_and_source_qualified_cluster()
    {
        var gateway = new DocumentGateway();
        var store = Store(gateway);
        var run = ProposalRun();

        await ConveyorLine.RunAsync(run, Services(store, new Completion(), Jury(), "low"), CancellationToken.None);

        var loaded = Assert.IsType<ConveyorRun>(await Store(gateway).LoadAsync(run.Id, CancellationToken.None));
        var proposal = Assert.Single(loaded.Proposals);
        var review = Assert.IsType<CouncilReview>(proposal.CouncilReview);
        Assert.Equal(RunStatus.Escalated, loaded.Status);
        Assert.Equal(ProposalVerdict.Escalate, review.Outcome);
        Assert.Equal(run.Evidence, proposal.ClusterEvidence);
        Assert.Equal(run.Evidence, review.Evidence);
        Assert.Equal(2, review.Rounds.Count);
        Assert.All(review.Rounds, round => Assert.Equal(5, round.Verdicts.Count));
        Assert.True(review.Recommendation!.Proceed);
        Assert.Equal(3, review.JuryVerdicts.Count);
        Assert.Equal(["gpt", "deepseek", "grok"], review.JuryVerdicts.Select(verdict => verdict.Model!.Family));
        Assert.DoesNotContain(S6Routing.StationName, loaded.Checkpoints);
        Assert.Empty(loaded.FiledIssues);
    }

    [Fact]
    public async Task A_legacy_accepted_flag_loads_as_an_audited_error_not_authoritative_proceed()
    {
        var gateway = new DocumentGateway();
        gateway.Documents["legacy"] = """
            {
              "id":"legacy", "trigger":"signal", "status":"open", "dryRun":false,
              "productHints":["acme"], "sourceKinds":["azuremonitor"],
              "checkpoints":["s1_triage","s2_investigation","s3_synthesis","s4_grounding","s5_council"],
              "proposals":[{
                "id":"proposal","title":"legacy","intentKey":"intent","confidence":1,
                "sourceKinds":["azuremonitor"],"evidenceReferences":["reference"],"accepted":true,"labels":[]
              }]
            }
            """;

        var loaded = Assert.IsType<ConveyorRun>(await Store(gateway).LoadAsync("legacy", CancellationToken.None));

        Assert.Equal(RunStatus.Error, loaded.Status);
        Assert.Equal(ProposalVerdict.Error, Assert.Single(loaded.Proposals).Verdict);
        Assert.Contains("typed", loaded.FailureReason);
        Assert.NotEmpty(loaded.Audit);
        Assert.Empty(loaded.FiledIssues);
    }

    private static ConveyorRun ProposalRun()
    {
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["azuremonitor", "webiq"], DryRun = true };
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(4));
        run.Evidence.Add(new EvidenceItem("azuremonitor", "reference", "checkout timeout"));
        run.Evidence.Add(new EvidenceItem("webiq", "reference", "checkout timeout"));
        run.Proposals.Add(new Proposal("proposal", "Resolve checkout timeout", run.SourceKinds, ["reference"])
        {
            IntentKey = "checkout",
            ClusterEvidence = run.Evidence.ToArray(),
        });
        return run;
    }

    private static CosmosRunStore Store(DocumentGateway gateway) =>
        new("https://cosmos.example", "dsf", "runs", "acme", gateway);

    private static readonly JurorModelSettings[] Models =
    [
        new("first", "openai", "gpt", "https://models.example", "gpt-4o"),
        new("second", "deepseek", "deepseek", "https://models.example", "DeepSeek-V3"),
        new("third", "xai", "grok", "https://models.example", "grok-3"),
    ];

    private static IReadOnlyList<IValidationJuror> Jury(IReadOnlyList<IModelClient>? clients = null) =>
        Models.Select((model, index) => (IValidationJuror)new ModelValidationJuror(
            model.Name, "verify evidence", clients?[index] ?? new Completion(), model)).ToArray();

    private static ConveyorServices Services(IRunStore store, IModelClient model, IReadOnlyList<IValidationJuror> jury, string maturity) =>
        new("acme", [], null, store, model, new Tracer(), new Threshold(), ValidationJurors: jury, ProductMaturity: maturity);

    private sealed class Completion : IModelClient
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult("GO: the evidence justifies delivery");
    }

    private sealed class CancellingCompletion(CancellationTokenSource cancellation) : IModelClient
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("cancellation was not propagated");
        }
    }

    private sealed class ForbiddenCompletion : IModelClient
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("completed judgment must not be invoked again");
    }

    private sealed class Threshold : IConfidenceThresholdReader
    {
        public Task<double> ReadThresholdAsync(CancellationToken cancellationToken) => Task.FromResult(0.6);
    }

    private sealed class Tracer : ITracer
    {
        public Task TraceAsync(string name, IReadOnlyDictionary<string, string?> properties, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class DocumentGateway : ICosmosDocumentGateway
    {
        public Dictionary<string, string> Documents { get; } = [];

        public Task UpsertAsync(string endpoint, string database, string container, string partitionKey,
            string id, string json, CancellationToken cancellationToken)
        {
            Documents[id] = json;
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(string endpoint, string database, string container, string partitionKey,
            string id, CancellationToken cancellationToken) => Task.FromResult(Documents.GetValueOrDefault(id));
    }
}
