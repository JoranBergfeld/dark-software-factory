namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S6 — routing. Applies the canonical triage label taxonomy (see
/// <c>docs/agents/triage-labels.md</c>) to every accepted proposal, so the filing
/// station has a routed, labelled unit of work rather than a bare title.
/// </summary>
public sealed class S6Routing : IStation
{
    public const string StationName = "s6_routing";

    /// <summary>Label every council-accepted proposal carries into filing.</summary>
    public const string ReadyForAgentLabel = "ready-for-agent";

    public string Name => StationName;

    public Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        foreach (var proposal in run.Proposals.Where(p => p.Accepted))
        {
            proposal.Labels.Add(ReadyForAgentLabel);
            proposal.Labels.Add($"source:{proposal.SourceKind}");
            run.Record(StationName, $"routed proposal '{proposal.Id}' -> [{string.Join(", ", proposal.Labels)}].");
        }

        run.Record(StationName, $"routing complete: {run.Proposals.Count(p => p.Accepted)} routed proposal(s).");
        return Task.CompletedTask;
    }
}
