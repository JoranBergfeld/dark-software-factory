using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

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
    /// <summary>
    /// S5 council's jury panel escalated at least one proposal to a human --
    /// either because the product's creation maturity is <c>low</c>, or because
    /// the three jurors split on whether to proceed. Terminal like <see
    /// cref="Killed"/>: the run never reaches S6/S7, and its persisted state
    /// (evidence, lens verdicts, jury verdicts, audit trail) is the review
    /// package a human acts on before anything is filed.
    /// </summary>
    Escalated,
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
/// and routing labels it. May carry evidence gathered from more than one source
/// kind (<see cref="SourceKinds"/>) -- clustering is not scoped to a single kind.
/// </summary>
public sealed class Proposal(string id, string title, IReadOnlyList<string> sourceKinds, IReadOnlyList<string> evidenceReferences)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    /// <summary>
    /// Every source kind that contributed evidence to this proposal, lower-case
    /// and de-duplicated. A single-kind cluster carries exactly one entry here,
    /// unchanged from before clustering could span kinds.
    /// </summary>
    public IReadOnlyList<string> SourceKinds { get; } = sourceKinds;

    public IReadOnlyList<string> EvidenceReferences { get; } = evidenceReferences;

    public IReadOnlyList<EvidenceItem> ClusterEvidence { get; set; } = [];

    public CouncilReview? CouncilReview { get; set; }

    /// <summary>
    /// Scope-qualified durable problem identity supplied by the identity resolver.
    /// Changing observations and source references must not create new filing intents.
    /// </summary>
    public string IntentKey { get; set; } = string.Empty;

    /// <summary>Council confidence in [0, 1]; set by the council station.</summary>
    public double Confidence { get; set; }

    /// <summary>
    /// The jury's typed action. S6/S7 require a complete matching
    /// <see cref="CouncilReview"/> before acting; neither confidence nor a legacy acceptance flag is authority.
    /// </summary>
    public ProposalVerdict Verdict { get; set; } = ProposalVerdict.Pending;

    /// <summary>
    /// Whether the council's final verdict routes this proposal to filing.
    /// Equivalent to <c><see cref="Verdict"/> == <see cref="ProposalVerdict.Proceed"/></c>
    /// -- kept as a convenience read, not a second source of truth: setting
    /// <see cref="Verdict"/> is the only way to change it.
    /// </summary>
    public bool Accepted => Verdict == ProposalVerdict.Proceed;

    public List<string> Labels { get; } = [];
}

/// <summary>
/// S5's typed action. Pending is not a decision; Rejected is retained only for
/// decoding historical records and never authorizes routing or filing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProposalVerdict>))]
public enum ProposalVerdict
{
    Pending,
    Rejected,
    Proceed,
    Escalate,
    Kill,
    Error,
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

    /// <summary>
    /// A user-invoked preview: the line runs, filing is skipped. Settable (not
    /// init-only) so a run resumed under <c>--dry-run</c> that was persisted with
    /// this <c>false</c> can be forced to <c>true</c> before it reaches S7 --
    /// dry-run must never file GitHub issues, even off a crashed non-dry-run
    /// run's checkpoints.
    /// </summary>
    public bool DryRun { get; set; }

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

/// <summary>
/// Computes the stable identity of a run's scope. Two runs asking for the same
/// trigger, product hints and source kinds compute the same identity, so the
/// runtime can look a prior run up by it before creating a new one -- a resumed
/// run finds and continues the run already persisted for that scope rather than
/// starting a fresh one that would never see its checkpoints or terminal status.
/// S1 triage recomputes the same value into <see cref="ConveyorRun.Fingerprint"/>
/// for dedup and the filing intent key, so a run's id and its fingerprint always
/// agree.
/// </summary>
public static class RunIdentity
{
    public static string Compute(
        TriggerKind trigger, IEnumerable<string> productHints, IEnumerable<string> sourceKinds)
    {
        var scope = string.Join(
            "|",
            trigger,
            string.Join(",", productHints.Select(hint => hint.ToLowerInvariant()).Order(StringComparer.Ordinal)),
            string.Join(",", sourceKinds.Select(kind => kind.ToLowerInvariant()).Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..16];
    }
}
