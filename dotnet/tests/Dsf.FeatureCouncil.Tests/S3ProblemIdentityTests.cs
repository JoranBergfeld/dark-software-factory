using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

public sealed class S3ProblemIdentityTests
{
    [Theory]
    [InlineData("SENTRY-1", "checkout errors: 18", false)]
    [InlineData("SENTRY-2", "checkout errors: 17", false)]
    [InlineData("SENTRY-2", "checkout errors: 18", true)]
    public async Task Evolving_observations_reuse_the_resolved_learning_and_filing_identity(
        string reference, string summary, bool additionalSource)
    {
        var resolver = new RecordingProblemIdentityResolver((_, _) => "problem-checkout");
        var learning = new RecordingLearningStore();
        var filer = new RecordingIssueFiler();
        var prior = RunWithEvidence(new EvidenceItem("sentry", "SENTRY-1", "checkout errors: 17"));
        prior.DryRun = false;
        await ConveyorLine.RunAsync(prior, ConveyorDoubles.Services(
            issueFiler: filer, learningStore: learning, problemIdentityResolver: resolver), CancellationToken.None);
        Assert.Equal(RunStatus.Filed, prior.Status);
        Assert.Equal("scope:problem-checkout", Assert.Single(filer.Filed).IntentKey);
        await learning.RecordAsync(new LearningRecord("scope:problem-checkout", OutcomeLabels.Rejected,
            "https://github.com/acme/acme/issues/7", "prior attempt", DateTimeOffset.UnixEpoch), CancellationToken.None);

        var current = RunWithEvidence(new EvidenceItem("sentry", reference, summary));
        current.DryRun = false;
        if (additionalSource)
        {
            current.Evidence.Add(new EvidenceItem("foundryiq", "FIQ-1", summary));
        }
        var model = new RecordingModelClient();
        await ConveyorLine.RunAsync(current, ConveyorDoubles.Services(
            issueFiler: filer, modelClient: model, learningStore: learning,
            problemIdentityResolver: resolver), CancellationToken.None);

        Assert.Equal(RunStatus.Filed, current.Status);
        Assert.Equal(["scope:problem-checkout", "scope:problem-checkout"], learning.Retrieved);
        Assert.Equal(2, filer.Filed.Count);
        Assert.All(filer.Filed, proposal => Assert.Equal("scope:problem-checkout", proposal.IntentKey));
        Assert.Contains(model.Prompts, prompt => prompt.Contains(OutcomeLabels.Rejected, StringComparison.Ordinal));
        var proposal = Assert.Single(current.Proposals);
        Assert.Equal(current.Evidence, proposal.ClusterEvidence);
        Assert.Contains(reference, proposal.EvidenceReferences);
        Assert.Contains("sentry", proposal.SourceKinds);
        if (additionalSource)
        {
            Assert.Contains("FIQ-1", proposal.EvidenceReferences);
            Assert.Contains("foundryiq", proposal.SourceKinds);
        }
        Assert.Equal(2, resolver.Requests.Count);
        Assert.All(resolver.Requests, request => Assert.Equal("scope", request.Scope));
        Assert.Equal(prior.Evidence, resolver.Requests[0].Cluster.Evidence);
        Assert.Equal(current.Evidence, resolver.Requests[1].Cluster.Evidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public async Task Blank_problem_keys_fail_as_audited_s3_errors(string key)
    {
        var run = RunWithEvidence(new EvidenceItem("azuremonitor", "alert-1", "checkout errors: 17"));
        var store = new RecordingRunStore();
        var services = ConveyorDoubles.Services(
            runStore: store, problemIdentityResolver: new RecordingProblemIdentityResolver((_, _) => key));

        await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains(S3Synthesis.StationName, run.FailureReason);
        Assert.Contains("problem key", run.FailureReason);
        Assert.Contains(run.Audit, record => record.Station == S3Synthesis.StationName
            && record.Message.Contains("problem key", StringComparison.Ordinal));
        Assert.Empty(run.Proposals);
        Assert.Equal(RunStatus.Error, store.Saved[^1].Status);
    }

    [Fact]
    public async Task Different_clusters_cannot_share_a_resolved_problem_key_within_a_run()
    {
        var run = RunWithEvidence(
            new EvidenceItem("azuremonitor", "alert-1", "checkout timeout"),
            new EvidenceItem("webiq", "inventory-1", "warehouse inventory shortage"));
        var store = new RecordingRunStore();
        var services = ConveyorDoubles.Services(
            runStore: store, problemIdentityResolver: new RecordingProblemIdentityResolver((_, _) => "same-problem"));

        await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains(S3Synthesis.StationName, run.FailureReason);
        Assert.Contains("same-problem", run.FailureReason);
        Assert.Contains(run.Audit, record => record.Station == S3Synthesis.StationName
            && record.Message.Contains("duplicate problem key", StringComparison.Ordinal));
        Assert.Empty(run.Proposals);
        Assert.Empty(run.PreviewedIssues);
        Assert.Equal(RunStatus.Error, store.Saved[^1].Status);
    }

    [Fact]
    public async Task Nonempty_clusters_require_a_problem_identity_resolver()
    {
        var run = RunWithEvidence(new EvidenceItem("azuremonitor", "alert-1", "checkout errors: 17"));
        var store = new RecordingRunStore();
        var services = new ConveyorServices("acme", [], null, store, new RecordingModelClient(),
            new RecordingTracer(), new FixedConfidenceThresholdReader(0.6));

        await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains(S3Synthesis.StationName, run.FailureReason);
        Assert.Contains("identity resolver", run.FailureReason);
        Assert.Contains(run.Audit, record => record.Station == S3Synthesis.StationName
            && record.Message.Contains("identity resolver", StringComparison.Ordinal));
        Assert.DoesNotContain(S3Synthesis.StationName, run.Checkpoints);
        Assert.Equal(RunStatus.Error, store.Saved[^1].Status);
    }

    [Fact]
    public async Task Empty_evidence_does_not_require_an_identity_resolver()
    {
        var run = RunWithEvidence();
        var services = ConveyorDoubles.Services() with { ProblemIdentityResolver = null };

        await new S3Synthesis().RunAsync(run, services, CancellationToken.None);

        Assert.Empty(run.Proposals);
    }

    [Fact]
    public async Task An_unavailable_resolver_stops_the_run_before_learning_synthesis_or_filing()
    {
        var run = RunWithEvidence(new EvidenceItem("azuremonitor", "alert-1", "checkout timeout"));
        run.DryRun = false;
        var learning = new RecordingLearningStore();
        var model = new RecordingModelClient();
        var filer = new RecordingIssueFiler();
        var store = new RecordingRunStore();
        var resolver = new RecordingProblemIdentityResolver(
            (_, _) => throw new InvalidOperationException("identity registry unavailable"));
        var services = ConveyorDoubles.Services(issueFiler: filer, runStore: store, modelClient: model,
            learningStore: learning, problemIdentityResolver: resolver);

        await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains(S3Synthesis.StationName, run.FailureReason);
        Assert.Contains("identity registry unavailable", run.FailureReason);
        Assert.Contains(run.Audit, record => record.Station == S3Synthesis.StationName
            && record.Message.Contains("identity registry unavailable", StringComparison.Ordinal));
        Assert.Empty(learning.Retrieved);
        Assert.Empty(model.Prompts);
        Assert.Empty(filer.Filed);
        Assert.Equal(RunStatus.Error, store.Saved[^1].Status);
    }

    [Fact]
    public async Task Problem_identity_resolution_honors_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var run = RunWithEvidence(new EvidenceItem("azuremonitor", "alert-1", "checkout timeout"));
        var services = ConveyorDoubles.Services();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new S3Synthesis().RunAsync(run, services, cancellation.Token));

        Assert.Empty(run.Proposals);
    }

    private static ConveyorRun RunWithEvidence(params EvidenceItem[] evidence)
    {
        var run = new ConveyorRun { ProductHints = ["acme"], Fingerprint = "scope", DryRun = true };
        run.Checkpoints.AddRange(ConveyorLine.StationNames.Take(2));
        run.Evidence.AddRange(evidence);
        return run;
    }
}
