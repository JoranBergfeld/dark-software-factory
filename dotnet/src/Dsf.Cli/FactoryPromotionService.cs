using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryPromotionService(IAzureCliRunner azure, HttpClient http, TimeProvider clock)
{
    internal async Task<FactoryActivationResult> ExecuteAsync(FactoryReservationResult admission, Guid? operation, string? fingerprint, CancellationToken token)
    {
        FactoryPromotionApproval? review = null;
        FactoryPromotionApproval? historical = null;
        FactoryActivationApproval? originalApproval = null;
        var dispatched = false;
        OwnerReservationStore? store = null;
        ConfigurationSetting? activeRecord = null;
        var completedPromotion = false;
        bool? firstFiling = null;
        try
        {
            if (admission.State != FactoryReservationState.Reserved || admission.Identity is not { } identity
                || admission.Foundation?.ProductEndpoint is not { } endpoint)
                throw Denied("Trusted original reserved factory required.");
            store = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
            var records = await RecordsAsync(store, token);
            originalApproval = FactoryActivation.Read(records.Where(p => p.Key.StartsWith(FactoryActivation.Prefix, StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => p.Value.Value), identity.FactoryId, clock.GetUtcNow()).Approval;
            if (records.TryGetValue(FactoryPromotion.RootKey, out var retained))
            {
                historical = JsonSerializer.Deserialize<FactoryPromotionApproval>(retained.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (historical.Protocol != 1 || historical.FactoryId != identity.FactoryId || historical.ApprovedBy != identity.AzureActorId
                    || historical.ApprovedAt == default || historical.ApprovedAt > clock.GetUtcNow()
                    || historical.ReviewFingerprint != FactoryPromotion.Fingerprint(historical) || historical.NativeIndices is null
                    || historical.Phase != FactoryPromotionPhase.Active
                    || !records.TryGetValue(FactoryPromotion.CompletionKey(historical.OperationId), out var completed))
                { historical = null; throw new JsonException(); }
                var completion = JsonSerializer.Deserialize<FactoryPromotionCompletion>(completed.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (completion.Protocol != 1 || completion.FactoryId != identity.FactoryId || completion.OperationId != historical.OperationId
                    || completion.ApprovedBy != identity.AzureActorId || historical.OperationId == Guid.Empty
                    || completion.Approval != Identity(retained, identity.FactoryId) || completion.ReviewFingerprint != historical.ReviewFingerprint
                    || completion.CompletedAt.Offset != TimeSpan.Zero || completion.CompletedAt < historical.ApprovedAt || completion.CompletedAt > clock.GetUtcNow())
                { historical = null; throw new JsonException(); }
                try { firstFiling = await new FactoryAuditService(azure, http, clock).ReadFirstFilingAsync(admission, historical, records, token); }
                catch (Exception e) when (e is FactoryAuditException or JsonException or HttpRequestException or IOException or InvalidOperationException
                    or OwnerReservationStore.AuthenticationFailedException or OperationCanceledException) { firstFiling = null; }
                var current = await CaptureAsync(store, historical.OperationId, token);
                historical = FactoryPromotion.RequireApproved(current.Values, current.Identities, current.Publisher.Plan, clock.GetUtcNow());
                if (operation is not null && operation != historical.OperationId || fingerprint is not null && fingerprint != historical.ReviewFingerprint)
                    throw Denied("Original final promotion retained; no replacement or reinterpretation.");
                return Result(FactoryActivationState.Activated, "Current explicit final promotion and native readiness confirmed.", current.Observation, true);
            }
            if (records.Keys.Any(k => k.StartsWith(FactoryPromotion.Prefix, StringComparison.Ordinal)))
                throw new FactoryActivationException(FactoryActivationState.Unknown, "Original final promotion is incomplete; no bootstrap replay, adoption or replacement.");
            if (operation is null) throw Denied("Explicit final-promotion operation and review required.");
            var observed = await CaptureAsync(store, operation.Value, token);
            review = observed.Review;
            if (fingerprint is null)
                return Result(FactoryActivationState.Reviewed, "Review exact active authority and native index creation. No writes or activation.", observed.Observation, false);
            if (fingerprint != review.ReviewFingerprint) throw Denied("Current exact final-promotion review fingerprint required.");
            var plan = observed.Publisher.Plan;
            await new FactoryAuditService(azure, http, clock).ProtectAnalysisAsync(admission, plan.Activation.AnalysisRevision,
                plan.Activation.AnalysisChain, fingerprint, token);
            if ((await CaptureAsync(store, operation.Value, token)).Review.ReviewFingerprint != fingerprint)
                throw Denied("Authority changed before final promotion.");
            var intent = new FactoryPromotionIntent(1, review, clock.GetUtcNow());
            dispatched = true;
            var intended = await store.WriteAsync(FactoryPromotion.IntentKey, Serialize(intent), null, token);
            observed = await CaptureAsync(store, operation.Value, token);
            if (observed.Review.ReviewFingerprint != fingerprint) throw Denied("Authority changed during final-promotion admission.");
            var seeds = await PostAsync<FactoryPromotionSeeds>(observed.Publisher, "/prepare-activation",
                new FactoryLiveRequest(operation.Value, fingerprint), token);
            FactoryPromotion.RequireSeeds(seeds, review, plan.Deployment.Manifest, clock.GetUtcNow());
            var recorded = await store.WriteAsync(FactoryPromotion.SeedsKey(operation.Value), Serialize(seeds), null, token);
            observed = await CaptureAsync(store, operation.Value, token);
            if (observed.Review.ReviewFingerprint != fingerprint
                || observed.Records[FactoryPromotion.IntentKey].ETag != intended.ETag) throw Denied("Approval, readiness or original intent changed during native index creation.");
            var approved = review with { ApprovedAt = clock.GetUtcNow(), NativeIndices = Identity(recorded, identity.FactoryId), Phase = FactoryPromotionPhase.Pending };
            var root = await store.WriteAsync(FactoryPromotion.RootKey, Serialize(approved), null, token);
            observed = await CaptureAsync(store, operation.Value, token);
            if (observed.Records[FactoryPromotion.RootKey].ETag != root.ETag || observed.Review.ReviewFingerprint != fingerprint)
                throw Denied("Final promotion changed during closeout.");
            approved = approved with { Phase = FactoryPromotionPhase.Active };
            activeRecord = await store.WriteAsync(FactoryPromotion.RootKey, Serialize(approved), root.ETag, token);
            observed = await CaptureAsync(store, operation.Value, token);
            if (observed.Records[FactoryPromotion.RootKey].ETag != activeRecord.ETag || observed.Review.ReviewFingerprint != fingerprint)
                throw Denied("Final promotion or readiness changed before activation receipt.");
            await store.WriteAsync(FactoryPromotion.CompletionKey(operation.Value), Serialize(new FactoryPromotionCompletion(1, identity.FactoryId,
                operation.Value, Identity(activeRecord, identity.FactoryId), fingerprint, identity.AzureActorId, clock.GetUtcNow())), null, token);
            completedPromotion = true;
            historical = approved;
            observed = await CaptureAsync(store, operation.Value, token);
            _ = FactoryPromotion.RequireApproved(observed.Values, observed.Identities, observed.Publisher.Plan, clock.GetUtcNow());
            historical = approved;
            return Result(FactoryActivationState.Activated, "Explicit final promotion persisted and current native readiness verified; no proposal created by activation.", observed.Observation, true);
        }
        catch (Exception e) when (e is FactoryActivationException or FactoryAuditException or ReservationStoreException
            or PreviewCapabilityException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or FormatException or IOException or HttpRequestException or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException)
        {
            var state = e switch
            {
                FactoryActivationException activation => activation.State,
                PreviewCapabilityException => FactoryActivationState.Blocked,
                OperationCanceledException => dispatched ? FactoryActivationState.Unknown : FactoryActivationState.Canceled,
                _ => FactoryActivationState.Unknown
            };
            if (activeRecord is not null && store is not null && (!completedPromotion || e is PreviewCapabilityException
                || e is FactoryActivationException changed && changed.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var closeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                    var original = JsonSerializer.Deserialize<FactoryPromotionApproval>(activeRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
                    await store.WriteAsync(FactoryPromotion.RootKey, Serialize(original with { Phase = FactoryPromotionPhase.Rejected }),
                        activeRecord.ETag, closeout.Token);
                }
                catch (Exception rejected) when (rejected is ReservationStoreException or OperationCanceledException or JsonException) { state = FactoryActivationState.Unknown; }
                historical = null;
            }
            return new(state, e is FactoryActivationException ? e.Message : "Current native activation unconfirmed; retained completion is history, not admission. No replay.")
            { Approval = originalApproval, PromotionReview = review, Promotion = historical, HistoricallyCompleted = historical is not null,
                FirstConfirmedFiling = firstFiling == true, FirstFilingState = firstFiling is null ? "Unknown" : firstFiling.Value ? "Confirmed" : "None" };
        }

        FactoryActivationResult Result(FactoryActivationState state, string detail, FactoryPromotionObservation observation, bool active) =>
            new(state, detail) { Approval = originalApproval, PromotionReview = review, Promotion = historical, CurrentReady = observation.CurrentReady,
                IsActivated = active && observation.CurrentReady, HistoricallyCompleted = historical is not null,
                ApprovalCurrent = originalApproval is not null && observation.CurrentReady, FirstConfirmedFiling = observation.FirstConfirmedFiling,
                FirstFilingState = observation.FirstConfirmedFiling ? "Confirmed" : "None" };
    }

    private async Task<Captured> CaptureAsync(OwnerReservationStore store, Guid operation, CancellationToken token)
    {
        var records = await RecordsAsync(store, token);
        var values = records.ToDictionary(p => p.Key, p => p.Value.Value);
        var publisher = FactoryPublisher.ReadConfirmed(values.Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal)).ToDictionary(), clock.GetUtcNow());
        var plan = publisher.Plan;
        var factory = plan.Deployment.Manifest.Identity;
        FactoryPause.RequireAdmissionOpen(records.Keys.Where(key =>
            !key.StartsWith(FactoryReconfigurationOperationRecords.Prefix, StringComparison.Ordinal)));
        await FactoryReconfigurationGate.RequireAsync(store, factory, clock, token,
            FactoryReconfigurationPhase.AwaitingActivation);
        if (publisher.Coordinator.NativeProviderProof is null) throw Denied("Original authenticated native publisher readiness required.");
        var identities = records.ToDictionary(p => p.Key, p => Identity(p.Value, factory.FactoryId));
        var observation = await PostAsync<FactoryPromotionObservation>(publisher, "/activation-readiness",
            new FactoryLiveRequest(plan.Selection.OperationId, plan.Fingerprint), token);
        if (observation.Protocol != 1 || observation.FactoryId != factory.FactoryId || !observation.CurrentReady
            || observation.AuthorityRevision != plan.Activation.AuthorityRevision || observation.PublisherFingerprint != plan.Fingerprint)
            throw Denied("Actual promoted-worker/publication capability and current complete readiness required.");
        var references = new[] { plan.Approval, plan.Live, plan.GitHub, identities[FactoryPublisher.RootKey],
            identities[publisher.Coordinator.NativeProviderProof.Key], identities[ProductConfigurationKeys.FactoryManifest] }
            .OrderBy(FactoryAudit.Id, StringComparer.Ordinal).ToArray();
        var review = new FactoryPromotionApproval(1, factory.FactoryId, operation, factory.AzureActorId, clock.GetUtcNow(), "",
            observation.AuthorityRevision, observation.LiveFingerprint, observation.PublisherFingerprint, references);
        review = review with { ReviewFingerprint = FactoryPromotion.Fingerprint(review) };
        FactoryPromotion.RequireReview(review, values, identities, plan, clock.GetUtcNow());
        var current = await RecordsAsync(store, token);
        if (FactoryModelsValidation.Hash(records.Values.OrderBy(r => r.Key).Select(r => new { r.Key, r.Value, ETag = r.ETag.ToString() }))
            != FactoryModelsValidation.Hash(current.Values.OrderBy(r => r.Key).Select(r => new { r.Key, r.Value, ETag = r.ETag.ToString() })))
            throw Denied("Current protected authority changed during final-promotion readiness.");
        return new(review, observation, publisher, records, values, identities);
    }

    private static Task<IReadOnlyDictionary<string, ConfigurationSetting>> RecordsAsync(OwnerReservationStore store, CancellationToken token) =>
        store.ReadNamespaceAsync("dsf/factory/", token, FactoryAudit.MaximumRecords, FactoryAudit.MaximumBytes, 1024);

    private async Task<T> PostAsync<T>(FactoryPublisherCapability capability, string route, FactoryLiveRequest input, CancellationToken token)
    {
        var factory = capability.Plan.Deployment.Manifest.Identity;
        var credential = new OwnerReservationStore.OperatorCredential(azure, factory.TenantId, factory.SubscriptionId, factory.AzureActorId,
            "api://" + capability.Plan.Selection.AudienceClientId);
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, capability.ContextEndpoint + route)
        { Content = JsonContent.Create(input, options: FactorySourceValidation.RecordJson) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["api://" + capability.Plan.Selection.AudienceClientId + "/.default"]), pending.Token)).Token);
        using var response = await http.SendAsync(request, pending.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw route == "/prepare-activation"
                ? new FactoryActivationException(FactoryActivationState.Unknown, "Original native activation preparation or its response is unconfirmed; no replay or adoption.")
                : Denied("Current native final-promotion dependency unavailable; no activation fallback.");
        await response.Content.LoadIntoBufferAsync(64 * 1024, pending.Token);
        return await response.Content.ReadFromJsonAsync<T>(FactorySourceValidation.RecordJson, pending.Token) ?? throw new JsonException();
    }
    private static AuditRecordIdentity Identity(ConfigurationSetting record, Guid factory) =>
        new(factory, AuditStoreKind.Governance, record.Key, record.ETag.ToString(), FactoryAudit.TextHash(record.Value));
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
    private static FactoryActivationException Denied(string reason) => new(FactoryActivationState.Blocked, reason);
    private sealed record Captured(FactoryPromotionApproval Review, FactoryPromotionObservation Observation,
        FactoryPublisherCapability Publisher, IReadOnlyDictionary<string, ConfigurationSetting> Records,
        IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, AuditRecordIdentity> Identities);
}
