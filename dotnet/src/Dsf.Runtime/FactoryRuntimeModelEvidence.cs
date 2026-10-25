using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

internal static partial class RuntimeWorkflow
{
    // A current native topology read permits attempting evaluation; it does not renew model health.
    private static void RequireRecentModelEvaluation(PreviewEvaluationDocument document, FactoryModels models, DateTimeOffset now)
    {
        if (models.Proof!.ExpiresAt > now) return;
        if (document.CreatedAt.AddSeconds(models.Configuration.ValidForSeconds) <= now
            || document.Proof is null || document.Phase != PreviewExecutionPhase.Evaluated || document.Failure is not null
            || document.PendingRun is not null || document.Dispatches.Any(d => d.State != PreviewModelDispatchState.Confirmed || d.Result is null)
            || !document.Dispatches.Select(d => d.Request.Route).Distinct().Order().SequenceEqual(FactoryModelsValidation.RouteNames.Order()))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "CurrentModelEvaluationRequired; original capability observation is historical, native topology is not inference health.");
    }

    private static async Task RequireCurrentPromotedModelEvidenceAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        FactoryPromotionApproval promotion, PreviewInputAcquisition current, TokenCredential credential, HttpClient http, CancellationToken token)
    {
        var clock = dependencies.PreviewClock;
        var publisherAuthority = new FactoryPublisherAuthority(settings, true, credential, http, clock);
        var publisher = await publisherAuthority.CheckAsync(token);
        var values = publisher.Records.ToDictionary(p => p.Key, p => p.Value.Value);
        var confirmed = FactoryPromotion.RequireApproved(values, publisher.Records.Keys.ToDictionary(k => k, publisher.Identity),
            publisher.Capability.Plan, clock.GetUtcNow());
        if (FactoryModelsValidation.Hash(confirmed) != FactoryModelsValidation.Hash(promotion)) throw new JsonException();
        var seeds = JsonSerializer.Deserialize<FactoryPromotionSeeds>(values[promotion.NativeIndices!.Key],
            FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var activity = new FactoryActivityStore(publisher.Manifest, promotion, false, seeds.Decisions.Identity,
            publisher.Credential, http, clock);
        await activity.RequireSettledAsync(null, token);
        var index = await activity.ReadAsync(token);
        var latest = index.Document.Entries.LastOrDefault()
            ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "CurrentModelEvaluationRequired; next admitted decision must actually exercise all five unchanged model routes.");
        var scope = latest.Decision ?? throw new JsonException();
        var inspection = await FactoryDecisionInspection.OpenAsync(settings, dependencies, new(scope.InvocationId, scope.DryRun),
            credential, http, token, promotion.LiveFingerprint);
        if (inspection.Access.AdmissionClosed) throw new PreviewCapabilityException(FactoryCharterState.Blocked, "DeliberatePauseOrUnconfirmedStop");
        var store = inspection.Store;
        var run = await store.ReadAsync(token) ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CurrentModelEvaluationRequired");
        var document = store.Document;
        var models = inspection.Live.Authority.Preview.Models!;
        RequireRecentModelEvaluation(document, models, clock.GetUtcNow());
        if (document.Admission is not { State: PreviewWorkState.Released, Readiness: PreviewReadiness.ReadyAwaitingActivation })
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "CurrentModelEvaluationReleaseUnconfirmed");
        ValidateCapturedInputs(document.Inputs!, current.Snapshot, clock.GetUtcNow());
        ValidateCapturedInputs(document.FinalInputs!, current.Snapshot, clock.GetUtcNow());
        var candidates = OnboardingReadiness.ValidateComplete(run, PreviewServices(document.Inputs!, run, current.Models, store), document.Dispatches);
        if (FactoryModelsValidation.Hash(candidates) != FactoryModelsValidation.Hash(document.Proof!.Candidates)) throw new JsonException();
        var original = store.NativeIdentity;
        await store.ReadDocumentAsync(token);
        if (original != store.NativeIdentity || index.SnapshotRevision != (await activity.ReadAsync(token)).SnapshotRevision)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "CurrentModelEvidenceChangedDuringInspection");
        await publisherAuthority.RequireSameAsync(publisher, token);
        RequireRecentModelEvaluation(document, models, clock.GetUtcNow());
    }
}
