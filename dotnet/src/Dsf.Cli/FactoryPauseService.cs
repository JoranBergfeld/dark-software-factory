using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryPauseService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryPauseResult> ExecuteAsync(FactoryReservationResult admission, Guid? operation, CancellationToken token)
    {
        if (admission.State != FactoryReservationState.Reserved || admission.Identity is not { } identity
            || admission.Foundation?.ProductEndpoint is not { } endpoint)
            return new(FactoryPauseState.Blocked, "Trusted reserved factory and protected product endpoint required; pause unconfirmed.");
        var store = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
        var record = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token);
        var profile = await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token);
        var manifest = FactoryGovernance.ReadInactiveManifest(record?.Value, profile?.Value, identity.ProductKey, endpoint);
        if (manifest.Identity != identity) throw new JsonException();
        var current = await store.ReadNamespaceAsync(FactoryPause.Key, token);
        var history = await store.ReadNamespaceAsync(FactoryPause.HistoryPrefix, token, maximumRecords: 128);
        if (current.Count != 0 || history.Count != 0)
        {
            if (current.Count != 1 || !current.TryGetValue(FactoryPause.Key, out var root))
                return new(FactoryPauseState.Unknown, "Pause coordinator missing or namespace inconsistent; surviving authority forbids recreation.")
                { AdmissionClosed = true };
            var retained = FactoryPause.Read(root.Value, manifest);
            if (retained.RequestedAt > clock.GetUtcNow()) throw new JsonException();
            if (history.Count != 1 || !history.TryGetValue(FactoryPause.HistoryKey(retained.OperationId), out var receipt)
                || receipt.Value != root.Value || receipt.ContentType != root.ContentType)
                return new(FactoryPauseState.Partial, "Admission closed by original durable request; audit copy unconfirmed. No automatic write replay.")
                { Request = retained, AdmissionClosed = true };
            if (operation is not null && operation != retained.OperationId)
                return new(FactoryPauseState.Blocked, "Another protected stop request already closes admission; no replacement.")
                { Request = retained, AdmissionClosed = true };
            return await ObservePublicationsAsync(admission, store, Requested(retained), token);
        }

        if (operation is null)
            return new(FactoryPauseState.NotRequested, "No deliberate pause request observed. This is not readiness, activation or runtime admission.")
            { AdmissionClosed = false };
        var request = new FactoryPauseRequest(1, FactoryPause.Kind, operation.Value, identity.FactoryId, identity.RepositoryId,
            manifest.ProfilePin, record!.ETag.ToString(), FactoryModelsValidation.Hash(manifest), manifest, identity.AzureActorId, clock.GetUtcNow());
        var value = JsonSerializer.Serialize(request, FactoryGitHubJson.Options);
        _ = FactoryPause.Read(value, manifest);
        OwnerReservationStore.CheckSize(FactoryPause.Key, ProductConfigurationKeys.NoLabel, value);
        OwnerReservationStore.CheckSize(FactoryPause.HistoryKey(operation.Value), ProductConfigurationKeys.NoLabel, value);
        var latest = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token);
        if (latest?.ETag != record.ETag || latest.Value != record.Value
            || (await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token))?.ETag != profile!.ETag)
            return new(FactoryPauseState.Blocked, "Factory/profile changed before stop dispatch. Review current identity; no write dispatched.");
        // The first confirmed native create closes admission; the audit copy is not a cross-key transaction.
        await FactoryLifecycleCoordinator.AcquireAsync(store, identity,
            FactoryLifecycleCoordinator.Pause, operation.Value, clock, token);
        await store.WriteAsync(FactoryPause.Key, value, null, token);
        try
        {
            await store.WriteAsync(FactoryPause.HistoryKey(operation.Value), value, null, token);
            var confirmed = await store.ReadAsync(FactoryPause.Key, token);
            if (confirmed?.Value != value) throw new JsonException();
        }
        catch (Exception e) when (e is ReservationStoreException or OperationCanceledException or JsonException)
        {
            return new(FactoryPauseState.Partial, "Admission closed by original durable request; audit copy or readback unconfirmed. Inspect, never reset or replay.")
            { Request = request, AdmissionClosed = true };
        }
        return await ObservePublicationsAsync(admission, store, Requested(request), token);
    }

    internal Task<FactoryPauseResult> InspectIssuedPublicationsAsync(
        FactoryReservationResult admission, CancellationToken token)
    {
        var identity = admission.Identity ?? throw new JsonException();
        var endpoint = admission.Foundation?.ProductEndpoint ?? throw new JsonException();
        var store = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
        return ObservePublicationsAsync(admission, store,
            new(FactoryPauseState.DrainingUnknown, "Issued-effect inventory for council detachment.")
            { AdmissionClosed = true }, token);
    }

    private async Task<FactoryPauseResult> ObservePublicationsAsync(FactoryReservationResult admission, OwnerReservationStore store,
        FactoryPauseResult result, CancellationToken token)
    {
        var effects = new List<FactoryIssuedPublication>();
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var charter = await new FactoryCharterService(RepositoryAttachmentReviewer.Create(azure), azure, http, clock)
                .InspectPublicationAsync(admission, pending.Token);
            if (charter.Publication is { } publication)
                effects.AddRange(publication.Effects.Select(e => new FactoryIssuedPublication("Charter",
                    $"{publication.Operation:D}/{e.Index}", e.State == CharterEffectState.Dispatched ? "Unknown" : e.State.ToString(), charter.Revision!)));
            var records = await store.ReadNamespaceAsync(FactoryPromotion.Prefix, pending.Token);
            if (!records.TryGetValue(FactoryPromotion.RootKey, out var root))
                return result with { IssuedPublications = effects, PublicationInventoryComplete = true };
            var promotion = JsonSerializer.Deserialize<FactoryPromotionApproval>(root.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (promotion.FactoryId != admission.Identity!.FactoryId || promotion.Phase != FactoryPromotionPhase.Active
                || promotion.NativeIndices?.Key != FactoryPromotion.SeedsKey(promotion.OperationId)
                || !records.TryGetValue(FactoryPromotion.CompletionKey(promotion.OperationId), out var completionRecord))
                throw new JsonException();
            var completion = JsonSerializer.Deserialize<FactoryPromotionCompletion>(completionRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (completion.FactoryId != promotion.FactoryId || completion.OperationId != promotion.OperationId
                || completion.Approval != new AuditRecordIdentity(promotion.FactoryId, AuditStoreKind.Governance,
                    root.Key, root.ETag.ToString(), FactoryAudit.TextHash(root.Value)))
                throw new JsonException();
            var publications = await new FactoryAuditService(azure, http, clock)
                .ReadPublicationOutcomesAsync(admission, promotion, records, pending.Token);
            effects.AddRange(publications.Select(p => new FactoryIssuedPublication("ProposalIssue", p.Operation!, p.State.ToString(), p.Revision!)));
            if ((await store.ReadAsync(root.Key, pending.Token))?.ETag != root.ETag) throw new JsonException();
            if (charter.Publication is not null
                && (await store.ReadAsync("dsf/factory/charter/publication", pending.Token))?.ETag.ToString() != charter.Revision)
                throw new JsonException();
            return result with { IssuedPublications = effects, PublicationInventoryComplete = true };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or HttpRequestException or IOException or ReservationStoreException or FactoryAuditException
            or OwnerReservationStore.AuthenticationFailedException || e is OperationCanceledException && !token.IsCancellationRequested)
        {
            return result with { IssuedPublications = effects, Detail = result.Detail
                + " Publication inventory incomplete/unavailable; observed outcomes are historical, not a completed stop." };
        }
    }

    private static FactoryPauseResult Requested(FactoryPauseRequest request) =>
        new(FactoryPauseState.DrainingUnknown, "Deliberate pause recorded. New council admission closed; already admitted or issued effects may still drain. No completed stop certified.")
        { Request = request, AdmissionClosed = true };
}
