using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>Revalidates complete typed evaluation against actual confirmed native model results; no inference or publication.</summary>
public static class OnboardingReadiness
{
    public static IReadOnlyList<PreviewCandidateProof> ValidateComplete(ConveyorRun run, ConveyorServices services,
        IReadOnlyList<PreviewModelDispatch> dispatches)
    {
        OnboardingEvaluation.ValidateInputs(run, services);
        OnboardingEvidence.ValidateCaptured(run);
        Require(run.Profile == ConveyorProfile.OnboardingPreview && run.DryRun && run.FiledIssues.Count == 0
            && run.FailureReason is null && run.Evidence.Count > 0 && run.Proposals.Count > 0
            && dispatches.All(d => d.State == PreviewModelDispatchState.Confirmed && d.Result is not null));
        var expectedStatus = run.Proposals.Any(p => p.Verdict == ProposalVerdict.Kill) ? RunStatus.Killed
            : run.Proposals.Any(p => p.Verdict == ProposalVerdict.Escalate) ? RunStatus.Escalated : RunStatus.Previewed;
        Require(run.Status == expectedStatus && run.Checkpoints.SequenceEqual(ConveyorLine.StationNames.Take(expectedStatus == RunStatus.Previewed ? 7 : 5)));
        var used = new HashSet<string>(StringComparer.Ordinal);
        var embedding = Model("embedding", "embedding");
        var groups = embedding.Result!.Deduplication!.Groups;
        var originals = run.Evidence.Select((e, i) => new ModelText("e" + i, e.Summary)).ToArray();
        Require(Same(originals, embedding.Request.Items) && groups.Count == run.SynthesisEvaluations.Count
            && groups.SelectMany(g => g).Order(StringComparer.Ordinal).SequenceEqual(originals.Select(o => o.Id).Order(StringComparer.Ordinal)));
        var byId = run.Evidence.Select((e, i) => ("e" + i, e)).ToDictionary(p => p.Item1, p => p.e);
        for (var index = 0; index < groups.Count; index++)
        {
            var synthesis = run.SynthesisEvaluations[index];
            var cluster = new EvidenceCluster(groups[index].Select(id => byId[id]).ToArray());
            OnboardingEvaluation.ValidateSynthesis(synthesis.Result, cluster);
            Require(synthesis.EvidenceReferences.SequenceEqual(cluster.Evidence.Select(e => e.Reference)));
            var result = Model("synthesis:" + index, "synthesis").Result!.Completion!;
            Require(Same(OnboardingEvaluation.Parse<ProposalSynthesis>(result.Text), synthesis.Result) && Same(result.Observation, synthesis.Model));
        }
        Require(run.Proposals.Count == run.SynthesisEvaluations.Count(s => s.Result.Candidate is not null));
        var candidates = new List<PreviewCandidateProof>();
        foreach (var proposal in run.Proposals)
        {
            SemanticGrounding.RequireSupported(proposal, run);
            var grounding = Model("grounding:" + proposal.Id, "synthesis").Result!.Completion!;
            Require(Same(OnboardingEvaluation.Parse<SemanticGroundingResult>(grounding.Text), proposal.Grounding!.Result)
                && Same(grounding.Observation, proposal.Grounding.Model));
            var review = proposal.CouncilReview ?? throw new EvaluationException("CompletePreviewEvaluationRequired");
            OnboardingEvaluation.ValidateReview(run, review, requireComplete: true);
            Require(review.Evidence.SequenceEqual(proposal.ClusterEvidence) && review.FailureReason is null);
            foreach (var round in review.Rounds)
            foreach (var lens in round.Verdicts)
            {
                var result = Model($"deliberation:{proposal.Id}:{round.Number}:{lens.LensName}", "synthesis").Result!.Completion!;
                var parsed = ModelDeliberationLens.ParseAnswer(result.Text);
                Require(parsed.Position == lens.Position && parsed.Rationale == lens.Rationale);
            }
            Require(Same(LensSynthesizer.Synthesize(review.Rounds[^1].Verdicts, review.Threshold), review.Recommendation)
                && proposal.Confidence == review.Recommendation!.Confidence);
            foreach (var juror in review.JuryVerdicts)
            {
                var result = Model($"jury:{proposal.Id}:{juror.JurorName}", juror.JurorName).Result!.Completion!;
                var parsed = JudgmentAnswer.Parse(result.Text, allowAbstain: false);
                Require((parsed.Position == LensPosition.Go ? JurorPosition.Go : JurorPosition.NoGo) == juror.Position && parsed.Rationale == juror.Rationale);
            }
            Require(review.Outcome == proposal.Verdict && proposal.Verdict == JuryVerdictRules.DecideProposalOnly(review.JuryVerdicts));
            var publication = OnboardingPublication.Preview(proposal, run);
            Require(Same(publication, proposal.IntendedPublication)
                && proposal.Labels.SequenceEqual(expectedStatus == RunStatus.Previewed ? new[] { "dsf:proposal" } : []));
            candidates.Add(new(proposal.Id, proposal.Verdict.ToString(), FactoryModelsValidation.Hash(publication)));
        }
        Require(used.Count == run.ModelObservations.Count && used.Count == dispatches.Count
            && run.ModelObservations.Select(o => o.Step).Distinct(StringComparer.Ordinal).Count() == used.Count
            && Same(run.PreviewedIssues, expectedStatus == RunStatus.Previewed ? run.Proposals.Select(p => p.IntendedPublication).ToArray() : []));
        return candidates;

        PreviewModelDispatch Model(string step, string route)
        {
            var observations = run.ModelObservations.Where(o => o.Step == step).ToArray();
            Require(observations.Length == 1);
            var observation = observations[0];
            var matches = dispatches.Where(d => d.Request.Route == route && d.Result is { } result
                && Same(result.Completion?.Observation ?? result.Deduplication?.Observation, observation.Model)
                && observation.InputSha256 == (route == "embedding" ? FactoryModelsValidation.Hash(d.Request.Items) : FactoryModelsValidation.Hash(d.Request.Input))).ToArray();
            Require(matches.Length == 1 && used.Add(matches[0].Identity));
            var match = matches[0];
            OnboardingEvaluation.ValidateObservation(run, route, observation.Model,
                match.Result!.Completion?.Text ?? JsonSerializer.Serialize(match.Result.Deduplication!.Groups));
            return match;
        }
    }

    private static bool Same(object? first, object? second) => FactoryModelsValidation.Hash(first) == FactoryModelsValidation.Hash(second);
    private static void Require(bool condition)
    {
        if (!condition) throw new EvaluationException("CompleteNativePreviewEvaluationRequired");
    }
}
