namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// A source agent's evidence-gathering seam, one implementation per source kind.
/// The runtime composes one per configured source agent endpoint; a run that asks
/// for a kind with no gatherer fails at the investigation station rather than
/// reporting an empty, successful investigation.
/// </summary>
public interface IEvidenceGatherer
{
    /// <summary>The source kind this gatherer serves (lower-case, e.g. <c>sentry</c>).</summary>
    string SourceKind { get; }

    Task<IReadOnlyList<EvidenceItem>> GatherAsync(ConveyorRun run, CancellationToken cancellationToken);
}

/// <summary>
/// The filing seam: turns an accepted, routed proposal into a tracked issue and
/// returns its URL. Implementations key off <see cref="Proposal.IntentKey"/> so
/// re-filing the same intent resolves to the issue that already exists.
/// </summary>
public interface IIssueFiler
{
    Task<string> FileAsync(Proposal proposal, CancellationToken cancellationToken);
}

/// <summary>
/// The blackboard persistence seam. The conveyor writes the run through this port
/// after every station, so the run's checkpoints, evidence, decisions and audit
/// trail outlive the process that produced them and a resumed run can skip the
/// stations that already completed.
/// </summary>
public interface IRunStore
{
    Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken);
}

/// <summary>
/// The collaborators a conveyor line needs: the product it is scoped to, the
/// source agents it can gather evidence from, the filer it hands accepted
/// proposals to, and the store its state is persisted through.
/// <paramref name="IssueFiler"/> is nullable so the filing station can report an
/// unwired filer at the real boundary; <paramref name="RunStore"/> is required --
/// a run the factory cannot persist is a run it cannot govern.
/// </summary>
public sealed record ConveyorServices(
    string Product,
    IReadOnlyList<IEvidenceGatherer> EvidenceGatherers,
    IIssueFiler? IssueFiler,
    IRunStore RunStore)
{
    public IRunStore RunStore { get; } = RunStore ?? throw new ArgumentNullException(nameof(RunStore));

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
