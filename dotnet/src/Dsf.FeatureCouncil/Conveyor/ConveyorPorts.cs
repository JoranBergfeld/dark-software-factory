namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// A source agent's evidence-gathering seam, one implementation per
/// <see cref="SourceKind"/>. The A2A-served source agents that implement this in
/// production are tracked in #144; until one is registered for a kind, the
/// investigation station audits its absence rather than inventing evidence.
/// </summary>
public interface IEvidenceGatherer
{
    /// <summary>The source kind this gatherer serves (lower-case, e.g. <c>sentry</c>).</summary>
    string SourceKind { get; }

    Task<IReadOnlyList<EvidenceItem>> GatherAsync(ConveyorRun run, CancellationToken cancellationToken);
}

/// <summary>
/// The filing seam: turns an accepted, routed proposal into a tracked issue and
/// returns its URL. The GitHub-backed implementation is tracked in #143; the
/// filing station fails at this boundary -- after the rest of the line has run --
/// when there is something to file and no filer is wired.
/// </summary>
public interface IIssueFiler
{
    Task<string> FileAsync(Proposal proposal, CancellationToken cancellationToken);
}

/// <summary>
/// The collaborators a conveyor line needs: the product it is scoped to, the
/// source agents it can gather evidence from, and the filer it hands accepted
/// proposals to. <paramref name="IssueFiler"/> is deliberately nullable -- an
/// unwired filer is a real, reportable condition at the filing boundary, not
/// something to paper over with a do-nothing implementation.
/// </summary>
public sealed record ConveyorServices(
    string Product,
    IReadOnlyList<IEvidenceGatherer> EvidenceGatherers,
    IIssueFiler? IssueFiler)
{
    public IEvidenceGatherer? GathererFor(string sourceKind) =>
        EvidenceGatherers.FirstOrDefault(
            gatherer => string.Equals(gatherer.SourceKind, sourceKind, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One conveyor station: a named, ordered step that advances a run.</summary>
public interface IStation
{
    string Name { get; }

    Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken);
}
