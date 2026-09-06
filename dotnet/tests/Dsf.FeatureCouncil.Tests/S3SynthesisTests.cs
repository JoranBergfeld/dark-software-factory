using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

/// <summary>
/// S3 synthesis clusters evidence before turning it into proposals: a cluster
/// that spans more than one source kind becomes exactly one proposal, carrying
/// every contributing kind's evidence references forward; a single-kind cluster
/// still synthesizes exactly as before clustering could span kinds.
/// </summary>
public sealed class S3SynthesisTests
{
    private static ConveyorRun RunWith(params EvidenceItem[] evidence)
    {
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = evidence.Select(e => e.SourceKind).Distinct().ToArray() };
        run.Evidence.AddRange(evidence);
        return run;
    }

    [Fact]
    public async Task Evidence_from_two_kinds_describing_the_same_problem_synthesizes_one_proposal()
    {
        var run = RunWith(
            new EvidenceItem("azuremonitor", "AM-1", "checkout 500s spiked after release 4.2"),
            new EvidenceItem("foundryiq", "FIQ-1", "checkout 500s spiked, same release 4.2"));
        var services = ConveyorDoubles.Services();

        await new S3Synthesis().RunAsync(run, services, CancellationToken.None);

        var proposal = Assert.Single(run.Proposals);
        Assert.Equal(["azuremonitor", "foundryiq"], proposal.SourceKinds);
        Assert.Equal(["AM-1", "FIQ-1"], proposal.EvidenceReferences);
    }

    [Fact]
    public async Task Evidence_describing_unrelated_problems_still_synthesizes_separate_proposals()
    {
        var run = RunWith(
            new EvidenceItem("azuremonitor", "AM-1", "checkout 500s spiked after release 4.2"),
            new EvidenceItem("azuremonitor", "AM-2", "database failover latency doubled"));
        var services = ConveyorDoubles.Services();

        await new S3Synthesis().RunAsync(run, services, CancellationToken.None);

        Assert.Equal(2, run.Proposals.Count);
        Assert.All(run.Proposals, proposal => Assert.Equal(["azuremonitor"], proposal.SourceKinds));
    }

    [Fact]
    public async Task A_single_kind_cluster_carries_exactly_one_source_kind()
    {
        var run = RunWith(new EvidenceItem("azuremonitor", "AM-1", "checkout 500s spiked after release 4.2"));
        var services = ConveyorDoubles.Services();

        await new S3Synthesis().RunAsync(run, services, CancellationToken.None);

        var proposal = Assert.Single(run.Proposals);
        Assert.Equal(["azuremonitor"], proposal.SourceKinds);
    }
}
