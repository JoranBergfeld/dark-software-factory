using System.Text.Json.Serialization;
using Dsf.Core.Onboarding;

namespace Dsf.FeatureCouncil.Conveyor;

[JsonConverter(typeof(JsonStringEnumConverter<ClaimSupport>))]
public enum ClaimSupport { Supported, Unsupported, Contradicted, Uncertain }

public sealed record GroundingCitation(string Reference, string Excerpt);
public sealed record GroundedClaim(
    string ClaimId, ClaimSupport Support, string Explanation,
    IReadOnlyList<GroundingCitation> Citations, IReadOnlyList<GroundingCitation> Counterevidence);
public sealed record SemanticGroundingResult(
    bool Complete, bool ContentSupported, string Explanation,
    IReadOnlyList<GroundedClaim> Checks, IReadOnlyList<string> Limitations);
public sealed record GroundingEvaluation(SemanticGroundingResult Result, ModelWorkloadObservation Model);

internal static class SemanticGrounding
{
    internal static void RequireSupported(Proposal proposal, ConveyorRun run)
    {
        if (proposal.Synthesis is not { } draft || proposal.Grounding is not { } evaluation)
            throw new EvaluationException("RequiredSynthesisAndSemanticGroundingMissing");
        var synthesized = run.SynthesisEvaluations.Where(item =>
            item.EvidenceReferences.SequenceEqual(proposal.EvidenceReferences)).ToArray();
        if (proposal.Title != draft.Title || proposal.Body != draft.Body || synthesized.Length != 1
            || !synthesized[0].Result.Complete || synthesized[0].Result.Candidate is not { } original
            || FactoryModelsValidation.Hash(original) != FactoryModelsValidation.Hash(draft)
            || !proposal.ClusterEvidence.Select(item => item.Reference).SequenceEqual(proposal.EvidenceReferences)
            || proposal.ClusterEvidence.Any(item => !run.Evidence.Any(supplied => supplied.Reference == item.Reference
                && FactoryModelsValidation.Hash(supplied) == FactoryModelsValidation.Hash(item))))
            throw new EvaluationException("CandidateChangedSinceStructuredSynthesis");
        var result = evaluation.Result;
        if (!result.Complete || !OnboardingEvaluation.Text(result.Explanation, 1024)
            || result.Checks.Count != draft.Claims.Count
            || result.Checks.Any(c => c is null || !Enum.IsDefined(c.Support) || !OnboardingEvaluation.Text(c.Explanation, 1024))
            || !result.Checks.Select(c => c.ClaimId).Order(StringComparer.Ordinal)
                .SequenceEqual(draft.Claims.Select(c => c.Id).Order(StringComparer.Ordinal))
            || result.Limitations.Count > 16 || result.Limitations.Any(l => !OnboardingEvaluation.Text(l, 1024)))
            throw new EvaluationException("IncompleteSemanticGrounding");

        foreach (var check in result.Checks)
        {
            var claim = draft.Claims.Single(c => c.Id == check.ClaimId);
            if (check.Citations.Count == 0 || check.Citations.Count > run.Evidence.Count
                || check.Counterevidence.Count > run.Evidence.Count
                || check.Citations.Any(c => c is null || !claim.EvidenceReferences.Contains(c.Reference, StringComparer.Ordinal)))
                throw new EvaluationException("InvalidSemanticGroundingCitations");
            foreach (var citation in check.Citations.Concat(check.Counterevidence))
            {
                if (citation is null || string.IsNullOrWhiteSpace(citation.Excerpt) || citation.Excerpt.Length > 1024
                    || !run.Evidence.Any(e => e.Reference == citation.Reference
                        && e.Summary.Contains(citation.Excerpt, StringComparison.Ordinal)))
                    throw new EvaluationException("GroundingExcerptDoesNotMatchOriginalEvidence");
            }
        }
        if (!result.ContentSupported || result.Checks.Any(c => c.Support != ClaimSupport.Supported))
            throw new EvaluationException("UnsupportedOrUncertainMaterialClaim");
    }
}
