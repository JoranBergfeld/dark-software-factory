using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed record FactoryDecisionInspection(FactoryLiveExecution Live, PreviewExecutionStore Store, FactoryInspectionAuthority Access)
{
    internal static async Task<FactoryDecisionInspection> OpenAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        FactoryDecisionRequest request, TokenCredential credential, HttpClient http, CancellationToken token, string? rollout = null,
        bool requireCurrentReadiness = false)
    {
        if (request.InvocationId == Guid.Empty) throw new JsonException("Original explicit invocation required.");
        var access = new FactoryInspectionAuthority();
        var publisherAuthority = new FactoryPublisherAuthority(settings, true, credential, http, dependencies.PreviewClock, access);
        var publisher = await publisherAuthority.CheckAsync(token);
        var values = publisher.Records.ToDictionary(p => p.Key, p => p.Value.Value);
        var promotion = FactoryPromotion.RequireApproved(values, publisher.Records.Keys.ToDictionary(k => k, publisher.Identity),
            publisher.Capability.Plan, dependencies.PreviewClock.GetUtcNow());
        if (rollout is not null && rollout != promotion.LiveFingerprint)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "OriginalInvocationRolloutRequired");
        var liveRequest = new FactoryLiveRequest(request.InvocationId, promotion.LiveFingerprint);
        var liveAuthority = new FactoryLiveAuthority(settings, dependencies, credential, http, access);
        var snapshot = await liveAuthority.CheckAsync(liveRequest, false, token);
        var live = new FactoryLiveExecution(snapshot, liveRequest, liveAuthority);
        var scope = new FactoryPublicationDecisionScope(request.InvocationId, publisher.Identity(FactoryPromotion.RootKey),
            publisher.Identity(FactoryPublisher.RootKey), request.DryRun);
        var seeds = JsonSerializer.Deserialize<FactoryPromotionSeeds>(values[promotion.NativeIndices!.Key], FactorySourceValidation.RecordJson)
            ?? throw new JsonException();
        var activity = new FactoryActivityStore(publisher.Manifest, promotion, false, seeds.Decisions.Identity,
            publisher.Credential, http, dependencies.PreviewClock);
        var index = await activity.ReadAsync(token);
        var id = FactoryPromotion.DecisionId(publisher.Manifest.Identity.FactoryId, request.InvocationId);
        var entry = index.Document.Entries.SingleOrDefault(e => e.Id == id)
            ?? throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIndexedInvocationRequiredNoCreation");
        if (entry.NativeResourceId is null || entry.Decision != scope || entry.IntentSha256 != FactoryModelsValidation.Hash(scope))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalInvocationScopeOrAdmissionUnconfirmed");
        var store = new PreviewExecutionStore(snapshot.Preview, credential, http, dependencies.PreviewClock, live, scope);
        await store.ReadDocumentAsync(token);
        if (store.NativeIdentity?.NativeResourceId != entry.NativeResourceId
            || (await publisherAuthority.CheckAsync(token)).Revision != publisher.Revision
            || (await liveAuthority.CheckAsync(liveRequest, false, token)).Revision != snapshot.Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalInvocationAuthorityOrIncarnationChanged");
        if (requireCurrentReadiness && !access.AdmissionClosed)
            _ = await new FactoryLiveAuthority(settings, dependencies, credential, http).CheckAsync(liveRequest, true, token);
        return new(live, store, access);
    }
}
