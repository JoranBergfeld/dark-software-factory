namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// Lifecycle of a conveyor run, mirroring the Python <c>RunStatus</c> terminal
/// states (<c>core/src/dsf/contracts/enums.py</c>): <see cref="Killed"/>,
/// <see cref="Filed"/> and <see cref="Error"/> are terminal and are never
/// re-driven. <see cref="Previewed"/> is the terminal state of a <c>--dry-run</c>
/// line: every station ran, filing was deliberately skipped.
/// </summary>
public enum RunStatus
{
    Open,
    Killed,
    Previewed,
    Filed,
    Error,
}

/// <summary>What started a run: a manual <c>--signal</c> or the scheduled sweep.</summary>
public enum TriggerKind
{
    Signal,
    Scheduled,
}

/// <summary>One line of a run's audit trail, attributed to the station that wrote it.</summary>
public sealed record AuditRecord(string Station, string Message);

/// <summary>
/// A single piece of evidence gathered from a source agent. <paramref name="Reference"/>
/// is the source-side identifier (issue id, alert id, query URL) a proposal must be
/// able to point at for the grounding station to keep it.
/// </summary>
public sealed record EvidenceItem(string SourceKind, string Reference, string Summary);

/// <summary>
/// A candidate unit of work synthesized from evidence. Mutable across the later
/// stations: grounding may drop it, the council scores and accepts or rejects it,
/// and routing labels it.
/// </summary>
public sealed class Proposal(string id, string title, string sourceKind, IReadOnlyList<string> evidenceReferences)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string SourceKind { get; } = sourceKind;

    public IReadOnlyList<string> EvidenceReferences { get; } = evidenceReferences;

    /// <summary>
    /// Durable identity of what this proposal asks for, stable across runs of the
    /// same scope (the run's fingerprint plus the source kind). The filing station
    /// stamps it into the filed issue so a later run that reaches the same
    /// conclusion resolves to the existing issue instead of filing a duplicate.
    /// </summary>
    public string IntentKey { get; set; } = string.Empty;

    /// <summary>Council confidence in [0, 1]; set by the council station.</summary>
    public double Confidence { get; set; }

    /// <summary>Whether the council accepted the proposal for routing and filing.</summary>
    public bool Accepted { get; set; }

    public List<string> Labels { get; } = [];
}

/// <summary>
/// An issue a dry run would have filed: the title, labels and filing intent key
/// the real filing station would have used, reported without creating anything.
/// </summary>
public sealed record IssuePreview(string Title, string IntentKey, IReadOnlyList<string> Labels);

/// <summary>
/// The unit of work the conveyor drives from station to station: what was asked
/// for, what was found, what was decided, and the audit trail and station
/// checkpoints that make all of it inspectable afterwards.
/// </summary>
public sealed class ConveyorRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    public TriggerKind Trigger { get; init; } = TriggerKind.Signal;

    public IReadOnlyList<string> ProductHints { get; init; } = [];

    public IReadOnlyList<string> SourceKinds { get; init; } = [];

    /// <summary>A user-invoked preview: the line runs, filing is skipped.</summary>
    public bool DryRun { get; init; }

    public RunStatus Status { get; set; } = RunStatus.Open;

    /// <summary>Debounce/dedup fingerprint of the run's scope, set by triage.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public List<EvidenceItem> Evidence { get; } = [];

    public List<Proposal> Proposals { get; } = [];

    public List<AuditRecord> Audit { get; } = [];

    /// <summary>Names of the stations that completed, in completion order.</summary>
    public List<string> Checkpoints { get; } = [];

    /// <summary>Issue URLs the filing station created (empty on a dry run).</summary>
    public List<string> FiledIssues { get; } = [];

    /// <summary>
    /// What a dry run would have filed: one entry per accepted, routed proposal
    /// the filing station deliberately did not file. Empty on a run that files
    /// for real -- there, <see cref="FiledIssues"/> is the record.
    /// </summary>
    public List<IssuePreview> PreviewedIssues { get; } = [];

    /// <summary>
    /// Why the run ended in <see cref="RunStatus.Error"/>: the station that failed
    /// and what it failed with. Set once, by the first failure, so a later
    /// telemetry or persistence failure cannot displace the cause an operator
    /// needs to see. <c>null</c> on a run that did not fail.
    /// </summary>
    public string? FailureReason { get; set; }

    public void Record(string station, string message) => Audit.Add(new AuditRecord(station, message));
}
