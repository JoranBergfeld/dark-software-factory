namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S5 — council. Weighs each grounded proposal through every configured
/// deliberation lens (<see cref="ConveyorServices.DeliberationLenses"/>): an
/// initial round where each lens judges the proposal independently, then a
/// see-and-revise round where every lens judges again having seen where the
/// others initially landed. The final round's verdicts are combined by <see
/// cref="LensSynthesizer.Synthesize"/> -- a deterministic, pure function of the
/// verdicts and the governed confidence threshold -- into a proceed/reject
/// recommendation and confidence.
///
/// A proposal the lens synthesizer does not recommend proceeding with is
/// simply <see cref="ProposalVerdict.Rejected"/>: the jury is never consulted
/// for it. A proposal the lenses do recommend proceeding with is instead
/// handed to every configured validation juror (<see
/// cref="ConveyorServices.ValidationJurors"/>), each reviewing that
/// recommendation independently; <see cref="JuryVerdictRules.Decide"/> then
/// combines their verdicts -- together with the product's creation maturity --
/// into the proposal's final <see cref="ProposalVerdict.Proceed"/>/<see
/// cref="ProposalVerdict.Escalate"/>/<see cref="ProposalVerdict.Kill"/>. This
/// is the station's actual verdict authority: <see cref="Proposal.Confidence"/>
/// and <see cref="Proposal.Verdict"/> (and the <see cref="Proposal.Accepted"/>
/// convenience read over it) are driven entirely by the lenses' weighted
/// judgment and the jury's review of it, not by how much of the run's evidence
/// a proposal happened to cite.
///
/// A juror that returns a malformed result, or whose model call fails or times
/// out, throws rather than being caught here: the exception propagates to the
/// conveyor line, which turns it into an audited, terminal <see
/// cref="RunStatus.Error"/> -- never a silent pass-through. A <see
/// cref="ProposalVerdict.Kill"/> verdict on any proposal kills the whole run
/// (<see cref="RunStatus.Killed"/>); a <see cref="ProposalVerdict.Escalate"/>
/// verdict on any proposal (with no kill also present) escalates the whole run
/// (<see cref="RunStatus.Escalated"/>) -- either way the run's persisted state,
/// including every lens and jury verdict recorded to the audit trail, is the
/// review package a human acts on: the line never reaches S6/S7 filing for a
/// killed or escalated run.
/// </summary>
public sealed class S5Council : IStation
{
    public const string StationName = "s5_council";

    /// <summary>
    /// Confidence a proposal must reach to be accepted when the product's own
    /// App Configuration store carries no <c>threshold.&lt;product&gt;</c> entry,
    /// matching the Python <c>DEFAULT_THRESHOLD</c> fallback in
    /// <c>dsf.config.flags</c> and Control Center's own documented default. The
    /// governed value -- read per run via <see cref="ConveyorServices.ConfidenceThresholdReader"/>
    /// -- is what the council actually compares the lens synthesis's confidence
    /// against; this constant is only the fallback a reader falls back to when
    /// nothing is configured.
    /// </summary>
    public const double DefaultThreshold = 0.6;

    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        var threshold = await services.ConfidenceThresholdReader.ReadThresholdAsync(cancellationToken);
        var lenses = services.DeliberationLenses;
        var jurors = services.ValidationJurors;

        foreach (var proposal in run.Proposals)
        {
            var initialVerdicts = new List<LensVerdict>(lenses.Count);
            foreach (var lens in lenses)
            {
                initialVerdicts.Add(
                    await lens.DeliberateAsync(proposal, run, [], services.ModelClient, cancellationToken));
            }

            var finalVerdicts = new List<LensVerdict>(lenses.Count);
            foreach (var lens in lenses)
            {
                finalVerdicts.Add(
                    await lens.DeliberateAsync(proposal, run, initialVerdicts, services.ModelClient, cancellationToken));
            }

            var synthesis = LensSynthesizer.Synthesize(finalVerdicts, threshold);
            proposal.Confidence = synthesis.Confidence;

            foreach (var verdict in finalVerdicts)
            {
                run.Record(
                    StationName,
                    $"proposal '{proposal.Id}' lens '{verdict.LensName}' position={verdict.Position} "
                    + $"rationale: {verdict.Rationale}");
            }

            if (!synthesis.Proceed)
            {
                proposal.Verdict = ProposalVerdict.Rejected;
                run.Record(
                    StationName,
                    $"proposal '{proposal.Id}' confidence={proposal.Confidence:F2} verdict=rejected "
                    + "(lens synthesis did not recommend proceeding; jury not consulted)"
                    + (synthesis.Disagreement ? " (lenses disagreed)" : string.Empty) + ".");
                continue;
            }

            var jurorVerdicts = new List<JurorVerdict>(jurors.Count);
            foreach (var juror in jurors)
            {
                jurorVerdicts.Add(await juror.ValidateAsync(proposal, run, synthesis, cancellationToken));
            }

            foreach (var verdict in jurorVerdicts)
            {
                run.Record(
                    StationName,
                    $"proposal '{proposal.Id}' juror '{verdict.JurorName}' position={verdict.Position} "
                    + $"rationale: {verdict.Rationale}");
            }

            proposal.Verdict = JuryVerdictRules.Decide(services.ProductMaturity, jurorVerdicts);
            run.Record(
                StationName,
                $"proposal '{proposal.Id}' confidence={proposal.Confidence:F2} verdict={proposal.Verdict}"
                + (synthesis.Disagreement ? " (lenses disagreed)" : string.Empty) + ".");
        }

        if (run.Proposals.Any(proposal => proposal.Verdict == ProposalVerdict.Kill))
        {
            run.Status = RunStatus.Killed;
            run.Record(
                StationName,
                "killed: the jury unanimously voted no-go on at least one proposal the lenses recommended "
                + "proceeding with.");
        }
        else if (run.Proposals.Any(proposal => proposal.Verdict == ProposalVerdict.Escalate))
        {
            run.Status = RunStatus.Escalated;
            run.Record(
                StationName,
                "escalated: at least one proposal needs a human decision (low creation maturity, or a split jury) "
                + "before this run can proceed to routing and filing.");
        }

        run.Record(
            StationName,
            $"council complete: {run.Proposals.Count(p => p.Verdict == ProposalVerdict.Proceed)} of "
            + $"{run.Proposals.Count} proposal(s) proceeding.");
    }
}
