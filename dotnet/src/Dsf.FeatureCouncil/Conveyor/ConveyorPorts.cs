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

    /// <summary>
    /// Loads the persisted run for <paramref name="runId"/>, or <c>null</c> if no
    /// run has ever been saved under that identity. The runtime looks a run up
    /// this way before creating a new one, so a signal or sweep that matches a
    /// prior, still-in-flight run resumes it -- seeing its checkpoints and
    /// terminal status -- instead of starting a fresh run blind to what already
    /// happened.
    /// </summary>
    Task<ConveyorRun?> LoadAsync(string runId, CancellationToken cancellationToken);
}

/// <summary>
/// The reasoning seam synthesis and council draw on: a single free-text
/// completion over a prompt. Production wires a real Azure OpenAI-backed
/// implementation; tests substitute a deterministic double so station behaviour
/// stays predictable without a live model call.
/// </summary>
public interface IModelClient
{
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>
/// The observability seam: one telemetry event per station/run boundary the
/// conveyor crosses. Production wires a real Application Insights-backed
/// implementation; tests substitute a recording double.
/// </summary>
public interface ITracer
{
    Task TraceAsync(
        string name, IReadOnlyDictionary<string, string?> properties, CancellationToken cancellationToken);
}

/// <summary>
/// The collaborators a conveyor line needs: the product it is scoped to, the
/// source agents it can gather evidence from, the filer it hands accepted
/// proposals to, the store its state is persisted through, the model it reasons
/// with, and the tracer it reports its progress through.
/// <paramref name="IssueFiler"/> is nullable so the filing station can report an
/// unwired filer at the real boundary; <paramref name="RunStore"/>,
/// <paramref name="ModelClient"/> and <paramref name="Tracer"/> are required --
/// a run the factory cannot persist, cannot reason over, or cannot trace is not
/// one composition should ever hand to the line.
/// </summary>
public sealed record ConveyorServices(
    string Product,
    IReadOnlyList<IEvidenceGatherer> EvidenceGatherers,
    IIssueFiler? IssueFiler,
    IRunStore RunStore,
    IModelClient ModelClient,
    ITracer Tracer)
{
    public IRunStore RunStore { get; } = RunStore ?? throw new ArgumentNullException(nameof(RunStore));

    public IModelClient ModelClient { get; } = ModelClient ?? throw new ArgumentNullException(nameof(ModelClient));

    public ITracer Tracer { get; } = Tracer ?? throw new ArgumentNullException(nameof(Tracer));

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
