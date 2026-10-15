using Dsf.Core.Charters;
using Dsf.Core.Onboarding;

namespace Dsf.FeatureCouncil.Conveyor;

internal static partial class OnboardingEvaluation
{
    internal static void ValidateInputs(ConveyorRun run, ConveyorServices services)
    {
        if (run.Profile == ConveyorProfile.Greenfield && run.EvaluationContext is null && run.EvaluationRevision is null) return;
        if (run.Profile != ConveyorProfile.OnboardingPreview || !run.DryRun)
            throw new EvaluationException("ExplicitOnboardingPreviewProfileAndDryRunRequired");
        var context = Context(run);
        if (context.FactoryId == Guid.Empty || !FactorySourceValidation.Hash(context.ManifestRevision)
            || context.Adoption is not { Protocol: 1 } adoption || adoption.Revision == Guid.Empty
            || adoption.FactoryId != context.FactoryId || context.Intent is not { Source.Content: not null } intent
            || intent.Source.Snapshot.FactoryId != context.FactoryId
            || intent.Source.Blob != adoption.CharterBlob
            || CharterMarkdown.GitBlobSha(intent.Source.Content) != adoption.CharterBlob
            || FactoryModelsValidation.Hash(intent.Source.Content) != adoption.CharterSha256)
            throw new EvaluationException("ValidAdoptedCharterContextRequired");
        _ = OnboardingCharterMarkdown.Parse(intent.Source.Content, services.Product);
        var analysis = intent.Analysis;
        if (analysis is not { State: IntentState.DraftReady, Governs: false, NextPage: null, NextExclusionPage: null, Draft: not null }
            || analysis.Progress is not { State: IntentState.DraftReady, Dispatch: null, PendingSelection: null,
                ModelOutcome: IntentModelOutcome.Confirmed } progress
            || progress.Revision == Guid.Empty || progress.Snapshot.FactoryId != context.FactoryId
            || progress.Snapshot.RepositoryId != intent.Source.Snapshot.RepositoryId
            || progress.AvailableChunks is < 1 or > FactoryIntentLimits.MaximumChunks
            || progress.AnalyzedChunks != progress.AvailableChunks || progress.CheckedChunks != progress.AvailableChunks
            || analysis.Acquisition is not { State: SourceState.Acquired, CompleteRootTree: true, NecessaryGaps: 0 } acquisition
            || acquisition.Snapshot != progress.Snapshot || acquisition.IntegritySha256 != progress.SourceIntegritySha256
            || analysis.Chunks.Count != 2 * progress.AvailableChunks || analysis.Claims.Count == 0
            || analysis.Claims.Any(c => c is null || !c.Grounded || c.Conflicted)
            || analysis.Chunks.Any(c => c is null || c.Revision != progress.Revision
                || c.Source.SnapshotId != progress.Snapshot.Id
                || (c.Dispatch.Checking ? c.Verification?.Complete != true : c.Candidate?.Complete != true))
            || analysis.Chunks.Count(c => c.Dispatch.Checking) != progress.AvailableChunks
            || analysis.Limitations.Any(string.IsNullOrWhiteSpace))
            throw new EvaluationException("CurrentCompleteAnalysisAndExplicitUnknownsRequired");
        analysis.Draft.Validate();
        if (context.Application is not { } scope || string.IsNullOrWhiteSpace(scope.Environment)
            || scope.Environment.Length > 128 || !Identifiers(scope.ApplicationResourceIds) || !Identifiers(scope.EvidenceBackendIds))
            throw new EvaluationException("SelectedApplicationAndEvidenceScopeRequired");
        if (context.Models is not { } models || !FactorySourceValidation.Hash(models.Revision)
            || progress.ModelConfigurationRevision != models.Revision || services.EvaluationModels is null)
            throw new EvaluationException("RequiredEvaluationModelWorkloadsMissingOrChanged");
        _ = JuryConfiguration(run);
        if (context.Judgment is not { } judgment || judgment.Rounds is < 1 or > 2
            || judgment.Rounds != services.DeliberationRounds || !double.IsFinite(judgment.Threshold)
            || judgment.Threshold is < 0 or > 1 || judgment.Lenses.Count is < 1 or > 5
            || judgment.Lenses.Any(l => l is null || string.IsNullOrWhiteSpace(l.Name) || !double.IsFinite(l.Weight) || l.Weight <= 0)
            || judgment.Lenses.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count() != judgment.Lenses.Count
            || !judgment.Lenses.Select(l => (l.Name, l.Weight)).SequenceEqual(services.DeliberationLenses.Select(l => (l.Name, l.Weight))))
            throw new EvaluationException("IntendedJudgmentConfigurationChanged");
        if (context.Evidence is null || context.Evidence.Count > 32
            || context.Evidence.Any(e => e is null || string.IsNullOrWhiteSpace(e.SourceKind)
                || string.IsNullOrWhiteSpace(e.Reference) || e.Reference.Length > 512
                || !FactorySourceValidation.Hash(e.ContentSha256) || !FactorySourceValidation.Hash(e.ConfigurationRevision)
                || string.IsNullOrWhiteSpace(e.ObservationReference) || e.ObservationReference.Length > 512 || e.ObservedAt == default)
            || context.Evidence.Select(e => e.Reference).Distinct(StringComparer.Ordinal).Count() != context.Evidence.Count)
            throw new EvaluationException("CompleteEvidenceProvenanceRequired");
        var revision = Revision(run);
        if (run.EvaluationRevision is not null && run.EvaluationRevision != revision)
            throw new EvaluationException("OnboardingEvaluationContextChanged");
        run.EvaluationRevision = revision;
    }

    private static bool Identifiers(IReadOnlyList<string> values) => values is { Count: >= 1 and <= 32 }
        && values.All(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 512 && !v.Any(char.IsControl))
        && values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count;

    internal static string ErrorDetail(ConveyorRun run, Exception exception) =>
        run.Profile == ConveyorProfile.Greenfield && run.EvaluationContext is null && run.EvaluationRevision is null
            ? exception.Message
            : exception is EvaluationException ? exception.Message : "EvaluationDependencyFailed; provider details withheld.";

    internal static void ValidateReview(ConveyorRun run, CouncilReview review, bool requireComplete)
    {
        var intended = Context(run).Judgment;
        var models = JuryConfiguration(run).Jurors;
        if (review.Profile != run.Profile || review.EvaluationRevision != run.EvaluationRevision
            || review.RoundsRequested != intended.Rounds || review.Threshold != intended.Threshold
            || review.Rounds.Count > intended.Rounds || requireComplete && review.Rounds.Count != intended.Rounds
            || !review.Models.SequenceEqual(models) || !review.JurorNames.SequenceEqual(models.Select(model => model.Name))
            || review.JuryVerdicts.Count > models.Count || requireComplete && review.JuryVerdicts.Count != models.Count)
            throw new EvaluationException("SavedReviewDoesNotMatchIntendedEvaluation");
        for (var index = 0; index < review.Rounds.Count; index++)
        {
            var round = review.Rounds[index];
            if (round.Number != index + 1 || round.Verdicts.Count != intended.Lenses.Count
                || !round.Verdicts.Select(verdict => verdict.LensName).SequenceEqual(intended.Lenses.Select(lens => lens.Name))
                || !round.Verdicts.Select(verdict => verdict.Weight).SequenceEqual(intended.Lenses.Select(lens => lens.Weight)))
                throw new EvaluationException("SavedRoundMissingIntendedLensOrChangedConfiguration");
            _ = LensSynthesizer.Synthesize(round.Verdicts, intended.Threshold);
        }
        for (var index = 0; index < review.JuryVerdicts.Count; index++)
        {
            var verdict = review.JuryVerdicts[index];
            if (verdict.JurorName != models[index].Name || verdict.Model != models[index]
                || !Enum.IsDefined(verdict.Position) || string.IsNullOrWhiteSpace(verdict.Rationale))
                throw new EvaluationException("SavedJurorResultDoesNotMatchConfiguredNativeModel");
        }
    }
}
