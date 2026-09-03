namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S5 — council. Scores each grounded proposal on the weight of evidence behind it
/// and accepts the ones that clear the confidence bar, then records the score and
/// verdict it reached for every proposal it saw.
/// </summary>
public sealed class S5Council : IStation
{
    public const string StationName = "s5_council";

    /// <summary>
    /// Confidence a proposal must reach to be accepted, matching the Python
    /// <c>DEFAULT_THRESHOLD</c> fallback in <c>dsf.config.flags</c>.
    /// </summary>
    public const double DefaultThreshold = 0.6;

    public string Name => StationName;

    public Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        var total = run.Evidence.Count;
        foreach (var proposal in run.Proposals)
        {
            proposal.Confidence = total == 0 ? 0d : (double)proposal.EvidenceReferences.Count / total;
            proposal.Accepted = proposal.Confidence >= DefaultThreshold;
            run.Record(
                StationName,
                $"proposal '{proposal.Id}' confidence={proposal.Confidence:F2} "
                + $"verdict={(proposal.Accepted ? "accept" : "reject")}.");
        }

        run.Record(
            StationName,
            $"council complete: {run.Proposals.Count(p => p.Accepted)} of {run.Proposals.Count} proposal(s) accepted.");
        return Task.CompletedTask;
    }
}
