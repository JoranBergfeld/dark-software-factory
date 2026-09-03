namespace Dsf.Runtime;

/// <summary>
/// One outcome <see cref="RuntimeVerbs.PollOutcomesAsync"/> observed on this poll:
/// the intent key and verdict it carried, and whether recording it was new
/// (<c>true</c>) or a no-op because it had already been recorded on a prior poll
/// (<c>false</c>). On a dry run every entry reports <c>false</c> -- nothing was
/// recorded at all, only previewed.
/// </summary>
public sealed record OutcomeRecord(string IntentKey, string Verdict, string IssueUrl, string Title, bool Recorded);

/// <summary>The result of one <c>poll-outcomes</c> invocation.</summary>
public sealed record OutcomeSweepResult(IReadOnlyList<OutcomeRecord> Outcomes, bool DryRun)
{
    /// <summary>How many outcomes this poll found, regardless of whether each was newly recorded.</summary>
    public int Polled => Outcomes.Count;

    /// <summary>How many outcomes were newly recorded (always 0 on a dry run).</summary>
    public int Recorded => Outcomes.Count(outcome => outcome.Recorded);

    /// <summary>Formats a human-readable summary, one line per outcome plus a totals line.</summary>
    public IEnumerable<string> ToLines()
    {
        foreach (var outcome in Outcomes)
        {
            yield return DryRun
                ? $"previewed: intent={outcome.IntentKey} verdict={outcome.Verdict} -> {outcome.IssueUrl}"
                : outcome.Recorded
                    ? $"recorded: intent={outcome.IntentKey} verdict={outcome.Verdict} -> {outcome.IssueUrl}"
                    : $"already recorded: intent={outcome.IntentKey} verdict={outcome.Verdict} -> {outcome.IssueUrl}";
        }

        yield return DryRun
            ? $"poll-outcomes complete: previewed {Polled} outcome(s), recorded none (dry run)."
            : $"poll-outcomes complete: polled {Polled} outcome(s), recorded {Recorded} new.";
    }
}
