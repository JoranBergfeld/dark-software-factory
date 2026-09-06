using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

/// <summary>
/// S5 council weighs a proposal through every configured deliberation lens
/// across an initial round and a see-and-revise round, then combines the final
/// round's verdicts via the deterministic weighted synthesizer into
/// <see cref="Proposal.Confidence"/>/<see cref="Proposal.Accepted"/> -- not the
/// prior evidence-count ratio. It must also score against the governed,
/// per-product confidence threshold -- read through
/// <see cref="ConveyorServices.ConfidenceThresholdReader"/> -- so a Control
/// Center write to a product's <c>threshold.&lt;product&gt;</c> App
/// Configuration entry still changes which proposals the council accepts.
/// </summary>
public sealed class S5CouncilTests
{
    private static ConveyorRun RunWithOneProposal()
    {
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"] };
        run.Evidence.Add(new EvidenceItem("sentry", "SENTRY-1", "checkout errors spiking"));
        run.Proposals.Add(new Proposal("p1", "checkout errors spiking", ["sentry"], ["SENTRY-1"]));
        return run;
    }

    [Fact]
    public async Task Accepts_a_proposal_every_lens_unanimously_endorses()
    {
        var run = RunWithOneProposal();
        var lenses = new IDeliberationLens[]
        {
            new FixedLens("value", LensPosition.Go),
            new FixedLens("cost", LensPosition.Go),
            new FixedLens("feasibility", LensPosition.Go),
            new FixedLens("security", LensPosition.Go),
            new FixedLens("strategic-fit", LensPosition.Go),
        };
        var services = ConveyorDoubles.Services(
            confidenceThresholdReader: new FixedConfidenceThresholdReader(0.6), deliberationLenses: lenses);

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        var proposal = run.Proposals.Single();
        Assert.True(proposal.Accepted);
        Assert.Equal(1.0, proposal.Confidence, precision: 6);
    }

    [Fact]
    public async Task Rejects_a_proposal_every_lens_unanimously_opposes()
    {
        var run = RunWithOneProposal();
        var lenses = new IDeliberationLens[]
        {
            new FixedLens("value", LensPosition.NoGo),
            new FixedLens("cost", LensPosition.NoGo),
            new FixedLens("feasibility", LensPosition.NoGo),
            new FixedLens("security", LensPosition.NoGo),
            new FixedLens("strategic-fit", LensPosition.NoGo),
        };
        var services = ConveyorDoubles.Services(
            confidenceThresholdReader: new FixedConfidenceThresholdReader(0.6), deliberationLenses: lenses);

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        var proposal = run.Proposals.Single();
        Assert.False(proposal.Accepted);
        Assert.Equal(0.0, proposal.Confidence, precision: 6);
    }

    [Fact]
    public async Task Records_disagreement_and_weighs_confidence_when_lenses_split()
    {
        var run = RunWithOneProposal();
        var lenses = new IDeliberationLens[]
        {
            new FixedLens("value", LensPosition.Go),
            new FixedLens("cost", LensPosition.NoGo),
            new FixedLens("feasibility", LensPosition.Go),
            new FixedLens("security", LensPosition.NoGo),
        };
        var services = ConveyorDoubles.Services(
            confidenceThresholdReader: new FixedConfidenceThresholdReader(0.5), deliberationLenses: lenses);

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        var proposal = run.Proposals.Single();
        Assert.Equal(0.5, proposal.Confidence, precision: 6);
        Assert.Contains(run.Audit, record => record.Message.Contains("lenses disagreed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accepts_a_proposal_whose_weighted_confidence_clears_the_configured_threshold()
    {
        var run = RunWithOneProposal();
        var lenses = new IDeliberationLens[]
        {
            new FixedLens("value", LensPosition.Go),
            new FixedLens("cost", LensPosition.Abstain),
        };
        var services = ConveyorDoubles.Services(
            confidenceThresholdReader: new FixedConfidenceThresholdReader(0.5), deliberationLenses: lenses);

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        Assert.True(run.Proposals.Single().Accepted);
    }

    [Fact]
    public async Task Rejects_the_same_lens_positions_once_the_configured_threshold_is_raised_above_their_confidence()
    {
        var run = RunWithOneProposal();
        var lenses = new IDeliberationLens[]
        {
            new FixedLens("value", LensPosition.Go),
            new FixedLens("cost", LensPosition.Abstain),
        };
        var services = ConveyorDoubles.Services(
            confidenceThresholdReader: new FixedConfidenceThresholdReader(0.9), deliberationLenses: lenses);

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        Assert.False(run.Proposals.Single().Accepted);
    }

    [Fact]
    public async Task Falls_back_to_the_documented_default_threshold_when_none_is_configured()
    {
        var justBelowDefault = RunWithOneProposal();
        var justAboveDefault = RunWithOneProposal();
        var lensesBelow = new IDeliberationLens[] { new FixedLens("value", LensPosition.Abstain) };
        var lensesAbove = new IDeliberationLens[] { new FixedLens("value", LensPosition.Go) };

        await new S5Council().RunAsync(
            justBelowDefault, ConveyorDoubles.Services(deliberationLenses: lensesBelow), CancellationToken.None);
        await new S5Council().RunAsync(
            justAboveDefault, ConveyorDoubles.Services(deliberationLenses: lensesAbove), CancellationToken.None);

        Assert.False(justBelowDefault.Proposals.Single().Accepted);
        Assert.True(justAboveDefault.Proposals.Single().Accepted);
    }

    [Fact]
    public async Task Runs_an_initial_round_then_a_see_and_revise_round_giving_every_lens_the_others_initial_positions()
    {
        var run = RunWithOneProposal();
        var value = new FixedLens("value", LensPosition.Go);
        var cost = new FixedLens("cost", LensPosition.NoGo);
        var services = ConveyorDoubles.Services(deliberationLenses: [value, cost]);

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        // Each lens deliberates twice: once with no prior verdicts (round 0),
        // once with both lenses' round-0 verdicts in hand (the revise round).
        Assert.Equal([0, 2], value.DeliberationCount);
        Assert.Equal([0, 2], cost.DeliberationCount);
    }

    [Fact]
    public void Default_deliberation_panel_has_the_five_documented_lenses()
    {
        var names = ModelDeliberationLens.Default().Select(lens => lens.Name).ToArray();

        Assert.Equal(["value", "cost", "feasibility", "security", "strategic-fit"], names);
    }
}
