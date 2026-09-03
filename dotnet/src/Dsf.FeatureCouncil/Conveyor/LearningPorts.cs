namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// Canonical labels a human applies to a filed issue to record its outcome. S6
/// routing labels every filed issue <c>ready-for-agent</c>; once a human resolves
/// it, applying one of these labels is the durable signal the learning loop polls
/// for -- the disposition the council's proposal actually met in reality, not
/// merely what was proposed.
/// </summary>
public static class OutcomeLabels
{
    public const string Approved = "dsf-outcome:approved";
    public const string Rejected = "dsf-outcome:rejected";
    public const string ChangesRequested = "dsf-outcome:changes-requested";

    /// <summary>Every canonical outcome label the poller recognizes.</summary>
    public static readonly IReadOnlyList<string> All = [Approved, Rejected, ChangesRequested];
}

/// <summary>
/// One human verdict observed on a filed issue: the durable intent key that ties
/// it back to the proposal that filed it (the same key <see cref="Proposal.IntentKey"/>
/// carries and the filing station stamps into the issue body), the outcome label
/// found, and the issue it was found on.
/// </summary>
public sealed record OutcomeSignal(string IntentKey, string Verdict, string IssueUrl, string Title);

/// <summary>
/// The learning-loop read seam: polls the issue tracker for issues the filing
/// station stamped with an intent key that a human has since labelled with a
/// canonical outcome label (<see cref="OutcomeLabels"/>). Implementations report
/// every matching issue found on each poll; the write side (<see cref="ILearningStore"/>)
/// is where idempotency against repeat polls is enforced.
/// </summary>
public interface IOutcomeSource
{
    Task<IReadOnlyList<OutcomeSignal>> PollAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One audited learning record: what was asked for, the verdict it received, and
/// when it was observed.
/// </summary>
public sealed record LearningRecord(string IntentKey, string Verdict, string IssueUrl, string Title, DateTimeOffset ObservedAt);

/// <summary>
/// The learning-loop write seam. Recording is idempotent by (intent key, verdict):
/// polling the same still-labelled issue again reports it was already recorded
/// rather than writing a duplicate audit entry, so a scheduled poll can run
/// indefinitely without inflating the learning store.
/// </summary>
public interface ILearningStore
{
    /// <summary>
    /// Records the outcome. Returns <c>true</c> the first time this (intent key,
    /// verdict) pair is recorded, and <c>false</c> when it had already been
    /// recorded -- a no-op write, not a duplicate.
    /// </summary>
    Task<bool> RecordAsync(LearningRecord record, CancellationToken cancellationToken);
}
