namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S4 — grounding. Drops any proposal whose evidence references do not all resolve
/// to evidence actually gathered on this run, so nothing downstream can be filed
/// on a claim the line cannot trace back to a source.
/// </summary>
public sealed class S4Grounding : IStation
{
    public const string StationName = "s4_grounding";

    public string Name => StationName;

    public Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        var gathered = run.Evidence.Select(item => item.Reference).ToHashSet(StringComparer.Ordinal);
        var ungrounded = run.Proposals
            .Where(proposal => proposal.EvidenceReferences.Count == 0
                || proposal.EvidenceReferences.Any(reference => !gathered.Contains(reference)))
            .ToList();

        foreach (var proposal in ungrounded)
        {
            run.Proposals.Remove(proposal);
            run.Record(StationName, $"dropped ungrounded proposal '{proposal.Id}'.");
        }

        run.Record(StationName, $"grounding complete: {run.Proposals.Count} grounded proposal(s).");
        return Task.CompletedTask;
    }
}
