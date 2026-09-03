using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

/// <summary>
/// S5 council must score against the governed, per-product confidence threshold
/// -- read through <see cref="ConveyorServices.ConfidenceThresholdReader"/> -- not
/// a value baked into the station. A Control Center write to a product's
/// <c>threshold.&lt;product&gt;</c> App Configuration entry is only real if the
/// value the reader answers actually changes which proposals the council accepts.
/// </summary>
public sealed class S5CouncilTests
{
    private static ConveyorRun RunWithOneProposal(double confidence)
    {
        var evidenceCount = 10;
        var referencedCount = (int)Math.Round(confidence * evidenceCount);
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"] };
        for (var i = 0; i < evidenceCount; i++)
        {
            run.Evidence.Add(new EvidenceItem("sentry", $"SENTRY-{i}", "evidence"));
        }

        var references = Enumerable.Range(0, referencedCount).Select(i => $"SENTRY-{i}").ToArray();
        run.Proposals.Add(new Proposal("p1", "checkout errors spiking", "sentry", references));
        return run;
    }

    [Fact]
    public async Task Accepts_a_proposal_whose_confidence_clears_the_configured_threshold()
    {
        var run = RunWithOneProposal(confidence: 0.7);
        var services = ConveyorDoubles.Services(confidenceThresholdReader: new FixedConfidenceThresholdReader(0.5));

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        Assert.True(run.Proposals.Single().Accepted);
    }

    [Fact]
    public async Task Rejects_the_same_proposal_once_the_configured_threshold_is_raised_above_its_confidence()
    {
        var run = RunWithOneProposal(confidence: 0.7);
        var services = ConveyorDoubles.Services(confidenceThresholdReader: new FixedConfidenceThresholdReader(0.9));

        await new S5Council().RunAsync(run, services, CancellationToken.None);

        Assert.False(run.Proposals.Single().Accepted);
    }

    [Fact]
    public async Task Falls_back_to_the_documented_default_threshold_when_none_is_configured()
    {
        var justBelowDefault = RunWithOneProposal(confidence: 0.5);
        var justAboveDefault = RunWithOneProposal(confidence: 0.7);
        var services = ConveyorDoubles.Services();

        await new S5Council().RunAsync(justBelowDefault, services, CancellationToken.None);
        await new S5Council().RunAsync(justAboveDefault, services, CancellationToken.None);

        Assert.False(justBelowDefault.Proposals.Single().Accepted);
        Assert.True(justAboveDefault.Proposals.Single().Accepted);
    }
}
