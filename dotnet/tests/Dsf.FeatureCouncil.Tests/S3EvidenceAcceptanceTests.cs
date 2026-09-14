using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

public sealed class S3EvidenceAcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_and_cross_source_clusters_keep_every_reference_and_preview_one_creation_ready_issue(bool crossSource)
    {
        var items = new[]
        {
            new EvidenceItem("azuremonitor", "monitor-1", "checkout timeout customers cannot pay"),
            new EvidenceItem(crossSource ? "webiq" : "azuremonitor", "web-1", "checkout timeout customers cannot pay"),
        };
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = items.Select(item => item.SourceKind).Distinct().ToArray(), DryRun = true };
        var services = ConveyorDoubles.Services(gatherers: run.SourceKinds.Select(kind =>
            (IEvidenceGatherer)new CountingEvidenceGatherer(kind, items.Where(item => item.SourceKind == kind).ToArray())).ToArray());

        await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(RunStatus.Previewed, run.Status);
        var proposal = Assert.Single(run.Proposals);
        Assert.Equal(["monitor-1", "web-1"], proposal.EvidenceReferences);
        Assert.Equal(run.SourceKinds, proposal.SourceKinds);
        var preview = Assert.Single(run.PreviewedIssues);
        Assert.Contains("creation:ready", preview.Labels);
        Assert.All(run.SourceKinds, kind => Assert.Contains($"source:{kind}", preview.Labels));
    }

    [Fact]
    public async Task Distinct_problems_from_the_same_sources_have_distinct_filing_intents()
    {
        var run = new ConveyorRun { Fingerprint = "scope" };
        run.Evidence.Add(new EvidenceItem("azuremonitor", "a", "checkout timeout"));
        run.Evidence.Add(new EvidenceItem("azuremonitor", "b", "inventory warehouse shortage"));
        var resolver = new RecordingProblemIdentityResolver((_, cluster) => cluster.Evidence[0].Reference switch
        {
            "a" => "checkout-problem",
            "b" => "inventory-problem",
            _ => throw new InvalidOperationException("Unexpected test evidence"),
        });

        await new S3Synthesis().RunAsync(
            run, ConveyorDoubles.Services(problemIdentityResolver: resolver), CancellationToken.None);

        Assert.Equal(["scope:checkout-problem", "scope:inventory-problem"],
            run.Proposals.Select(proposal => proposal.IntentKey));
    }

    [Fact]
    public async Task A_clusterer_cannot_drop_gathered_evidence_silently()
    {
        var run = new ConveyorRun();
        run.Evidence.Add(new EvidenceItem("azuremonitor", "a", "checkout timeout"));
        var services = ConveyorDoubles.Services(evidenceClusterer: new DroppingClusterer());

        await Assert.ThrowsAsync<InvalidOperationException>(() => new S3Synthesis().RunAsync(run, services, CancellationToken.None));
    }

    private sealed class DroppingClusterer : IEvidenceClusterer
    {
        public IReadOnlyList<EvidenceCluster> Cluster(IReadOnlyList<EvidenceItem> evidence) => [];
    }
}
