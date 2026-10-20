using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.FeatureCouncil.Conveyor;

internal static class OnboardingEvidence
{
    internal static async Task<IReadOnlyList<EvidenceCluster>> ClusterAsync(
        ConveyorRun run, ConveyorServices services, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OnboardingEvaluation.ValidateInputs(run, services);
        ValidateCaptured(run);
        if (run.Evidence.Count == 0) return [];
        var input = run.Evidence.Select((e, i) => new ModelText("e" + i, e.Summary)).ToArray();
        var grouped = await services.EvaluationModels!.DeduplicateAsync(input, token);
        token.ThrowIfCancellationRequested();
        OnboardingEvaluation.ValidateInputs(run, services);
        OnboardingEvaluation.ValidateObservation(run, "embedding", grouped.Observation, JsonSerializer.Serialize(grouped.Groups));
        if (grouped.Groups.Count == 0 || grouped.Groups.Any(g => g is null || g.Count == 0)
            || !grouped.Groups.SelectMany(g => g).Order(StringComparer.Ordinal)
                .SequenceEqual(input.Select(i => i.Id).Order(StringComparer.Ordinal))
            || JsonSerializer.Serialize(grouped.Observation.Groups) != JsonSerializer.Serialize(grouped.Groups))
            throw new EvaluationException("EmbeddingGroupsOmitDuplicateOrInventEvidence");
        run.ModelObservations.Add(new("embedding", FactoryModelsValidation.Hash(input), grouped.Observation));
        var originals = input.Select((item, i) => (item.Id, Evidence: run.Evidence[i])).ToDictionary(e => e.Id, e => e.Evidence);
        return grouped.Groups.Select(g => new EvidenceCluster(g.Select(id => originals[id]).ToArray())).ToArray();
    }

    internal static void ValidateCaptured(ConveyorRun run)
    {
        var provenance = OnboardingEvaluation.Context(run).Evidence;
        if (run.Evidence.Count != provenance.Count || run.Evidence.Count > 32
            || run.Evidence.Select(e => e.Reference).Distinct(StringComparer.Ordinal).Count() != run.Evidence.Count
            || run.Evidence.Any(e => e is null || string.IsNullOrWhiteSpace(e.Summary) || e.Summary.Length > 8192
                || !run.SourceKinds.Contains(e.SourceKind, StringComparer.Ordinal)
                || !provenance.Any(p => p.Reference == e.Reference && p.SourceKind == e.SourceKind
                    && p.ContentSha256 == FactoryModelsValidation.Hash(e.Summary))))
            throw new EvaluationException("GatheredEvidenceDoesNotMatchPinnedProvenance");
    }
}
