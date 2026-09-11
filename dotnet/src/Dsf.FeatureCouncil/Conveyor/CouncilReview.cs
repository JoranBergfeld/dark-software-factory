using System.Text.Json.Serialization;
using Dsf.Core.Runtime;

namespace Dsf.FeatureCouncil.Conveyor;

public sealed record DeliberationRound(
    [property: JsonRequired] int Number,
    [property: JsonRequired] IReadOnlyList<LensVerdict> Verdicts);

/// <summary>Typed S5 checkpoint and complete human-review package; never reconstructed from audit prose.</summary>
public sealed record CouncilReview
{
    [JsonRequired] public IReadOnlyList<EvidenceItem> Evidence { get; init; } = [];
    [JsonRequired] public double Threshold { get; init; }
    [JsonRequired] public string ProductMaturity { get; init; } = "";
    [JsonRequired] public int RoundsRequested { get; init; } = 2;
    [JsonRequired] public IReadOnlyList<string> JurorNames { get; init; } = [];
    [JsonRequired] public IReadOnlyList<JurorModelSettings> Models { get; init; } = [];
    [JsonRequired] public IReadOnlyList<DeliberationRound> Rounds { get; init; } = [];
    [JsonRequired] public LensSynthesis? Recommendation { get; init; }
    [JsonRequired] public IReadOnlyList<JurorVerdict> JuryVerdicts { get; init; } = [];
    [JsonRequired] public ProposalVerdict Outcome { get; init; } = ProposalVerdict.Pending;
    public string? FailureReason { get; init; }

    internal static void RevalidateForFiling(ConveyorRun run, string currentMaturity)
    {
        if (run.Status != RunStatus.Open)
        {
            throw new InvalidOperationException($"cannot route or file a {run.Status} run");
        }

        foreach (var proposal in run.Proposals)
        {
            var review = proposal.CouncilReview;
            if (proposal.Verdict != ProposalVerdict.Proceed || review is null
                || review.Outcome != ProposalVerdict.Proceed || review.Evidence.Count == 0
                || review.RoundsRequested is < 1 or > 2 || review.Rounds.Count != review.RoundsRequested
                || review.Recommendation is null || review.JurorNames.Count != 3
                || !review.JurorNames.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(review.JuryVerdicts.Select(verdict => verdict.JurorName))
                || JuryVerdictRules.Decide(review.ProductMaturity, review.JuryVerdicts) != ProposalVerdict.Proceed)
            {
                throw new InvalidOperationException(
                    $"proposal '{proposal.Id}' has no complete typed S5 Proceed review; legacy acceptance is not filing authority");
            }

            var recommendation = LensSynthesizer.Synthesize(review.Rounds[^1].Verdicts, review.Threshold);
            if (recommendation.Proceed != review.Recommendation.Proceed
                || recommendation.Confidence != review.Recommendation.Confidence
                || recommendation.Disagreement != review.Recommendation.Disagreement
                || !recommendation.Verdicts.SequenceEqual(review.Recommendation.Verdicts))
            {
                throw new InvalidOperationException($"proposal '{proposal.Id}' has an inconsistent S5 recommendation");
            }
        }

        foreach (var proposal in run.Proposals)
        {
            var review = proposal.CouncilReview!;
            var outcome = JuryVerdictRules.Decide(currentMaturity, review.JuryVerdicts);
            if (outcome == ProposalVerdict.Escalate)
            {
                proposal.Verdict = outcome;
                proposal.CouncilReview = review with { Outcome = outcome, ProductMaturity = currentMaturity };
                run.Status = RunStatus.Escalated;
                run.Record("filing-policy",
                    $"proposal '{proposal.Id}' escalated: current maturity '{currentMaturity}' overrides checkpoint maturity '{review.ProductMaturity}'.");
            }
        }
    }
}
