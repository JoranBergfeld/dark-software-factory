namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S5 — council. Weighs each grounded proposal through every configured
/// deliberation lens (<see cref="ConveyorServices.DeliberationLenses"/>): an
/// initial round where each lens judges the proposal independently, then a
/// see-and-revise round where every lens judges again having seen where the
/// others initially landed. The final round's verdicts are combined by <see
/// cref="LensSynthesizer.Synthesize"/> -- a deterministic, pure function of the
/// verdicts and the governed confidence threshold -- into the station's
/// proceed/reject recommendation and confidence. This replaces the prior
/// evidence-count ratio: <see cref="Proposal.Confidence"/> and <see
/// cref="Proposal.Accepted"/> are now driven entirely by the lenses' weighted
/// judgment, not by how much of the run's evidence a proposal happened to cite.
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
            proposal.Accepted = synthesis.Proceed;

            foreach (var verdict in finalVerdicts)
            {
                run.Record(
                    StationName,
                    $"proposal '{proposal.Id}' lens '{verdict.LensName}' position={verdict.Position} "
                    + $"rationale: {verdict.Rationale}");
            }

            run.Record(
                StationName,
                $"proposal '{proposal.Id}' confidence={proposal.Confidence:F2} "
                + $"verdict={(proposal.Accepted ? "accept" : "reject")}"
                + (synthesis.Disagreement ? " (lenses disagreed)" : string.Empty) + ".");
        }

        run.Record(
            StationName,
            $"council complete: {run.Proposals.Count(p => p.Accepted)} of {run.Proposals.Count} proposal(s) accepted.");
    }
}
