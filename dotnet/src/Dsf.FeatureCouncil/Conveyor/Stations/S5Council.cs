namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S5 — council. Scores each grounded proposal on the weight of evidence behind
/// it and accepts the ones that clear the confidence bar, then asks the model
/// client to reason over the verdict it reached and records both the score and
/// what the model answered for every proposal it saw. The accept/reject verdict
/// itself is the deterministic evidence-weight calculation -- reproducible and
/// auditable independent of the model -- the model's answer is recorded as the
/// council's stated rationale, not substituted for the verdict.
/// </summary>
public sealed class S5Council : IStation
{
    public const string StationName = "s5_council";

    /// <summary>
    /// Confidence a proposal must reach to be accepted, matching the Python
    /// <c>DEFAULT_THRESHOLD</c> fallback in <c>dsf.config.flags</c>.
    /// </summary>
    public const double DefaultThreshold = 0.6;

    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        var total = run.Evidence.Count;
        foreach (var proposal in run.Proposals)
        {
            proposal.Confidence = total == 0 ? 0d : (double)proposal.EvidenceReferences.Count / total;
            proposal.Accepted = proposal.Confidence >= DefaultThreshold;
            run.Record(
                StationName,
                $"proposal '{proposal.Id}' confidence={proposal.Confidence:F2} "
                + $"verdict={(proposal.Accepted ? "accept" : "reject")}.");

            var rationale = await services.ModelClient.CompleteAsync(CouncilPrompt(proposal), cancellationToken);
            run.Record(StationName, $"proposal '{proposal.Id}' rationale: {rationale}");
        }

        run.Record(
            StationName,
            $"council complete: {run.Proposals.Count(p => p.Accepted)} of {run.Proposals.Count} proposal(s) accepted.");
    }

    private static string CouncilPrompt(Proposal proposal) =>
        $"Explain the council {(proposal.Accepted ? "acceptance" : "rejection")} of proposal '{proposal.Title}' "
        + $"(confidence {proposal.Confidence:F2}) backed by evidence: {string.Join(", ", proposal.EvidenceReferences)}";
}
