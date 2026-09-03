using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// The operator-facing result of a conveyor run: what it was scoped to, what it
/// found, which stations checkpointed, and the audit trail. Rendered as text by
/// the runtime CLI and as JSON by the orchestrator host, so both surfaces report
/// exactly the same run.
/// </summary>
public sealed record RuntimeRunSummary(
    string RunId,
    string Trigger,
    string Status,
    bool DryRun,
    string Fingerprint,
    IReadOnlyList<string> ProductHints,
    IReadOnlyList<string> SourceKinds,
    int EvidenceCount,
    int ProposalCount,
    int AcceptedProposalCount,
    IReadOnlyList<string> Checkpoints,
    IReadOnlyList<string> FiledIssues,
    IReadOnlyList<string> Audit)
{
    public static RuntimeRunSummary From(ConveyorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RuntimeRunSummary(
            RunId: run.Id,
            Trigger: run.Trigger.ToString().ToLowerInvariant(),
            Status: run.Status.ToString().ToLowerInvariant(),
            DryRun: run.DryRun,
            Fingerprint: run.Fingerprint,
            ProductHints: run.ProductHints,
            SourceKinds: run.SourceKinds,
            EvidenceCount: run.Evidence.Count,
            ProposalCount: run.Proposals.Count,
            AcceptedProposalCount: run.Proposals.Count(proposal => proposal.Accepted),
            Checkpoints: run.Checkpoints,
            FiledIssues: run.FiledIssues,
            Audit: run.Audit.Select(record => $"[{record.Station}] {record.Message}").ToArray());
    }

    /// <summary>Renders the summary the way the runtime CLI prints it.</summary>
    public IEnumerable<string> ToLines()
    {
        yield return $"[dsf] run {RunId} -> status={Status} (dry_run={DryRun.ToString().ToLowerInvariant()})";
        yield return $"[dsf]   sources=[{string.Join(", ", SourceKinds)}] evidence={EvidenceCount} "
            + $"proposals={ProposalCount} accepted={AcceptedProposalCount}";
        yield return $"[dsf]   checkpoints=[{string.Join(", ", Checkpoints)}]";
        foreach (var line in Audit)
        {
            yield return $"[dsf]   audit{line}";
        }

        foreach (var issue in FiledIssues)
        {
            yield return $"[dsf]   filed {issue}";
        }
    }
}
