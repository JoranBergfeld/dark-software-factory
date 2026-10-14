using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryActivationService(RepositoryAttachmentReviewer reviewer, IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryActivationResult> ExecuteAsync(FactoryReservationResult admission, Guid? operation,
        string? approval, CancellationToken token)
    {
        FactoryActivationReview? reviewed = null;
        FactoryActivationApproval? receipt = null;
        ConfigurationSetting? coordinator = null;
        FactoryActivationOperation? intent = null;
        OwnerReservationStore? store = null;
        var dispatched = false;
        try
        {
            if (admission.State != FactoryReservationState.Reserved || admission.Identity is not { } identity
                || admission.Foundation?.ProductEndpoint is not { } endpoint)
                throw new FactoryActivationException(FactoryActivationState.Blocked, "Trusted reserved factory and protected product endpoint required.");
            store = new(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
            var history = await store.ReadNamespaceAsync(FactoryActivation.Prefix, token, maximumRecords: 32);
            if (history.Count != 0)
            {
                var retained = FactoryActivation.Read(history.ToDictionary(p => p.Key, p => p.Value.Value), identity.FactoryId, clock.GetUtcNow());
                intent = retained.Operation;
                reviewed = retained.Review;
                receipt = retained.Approval;
                coordinator = history[FactoryActivation.RootKey];
                if (operation is not null && operation != intent.OperationId)
                    throw new FactoryActivationException(FactoryActivationState.Blocked, "Original approval operation already retained; no replacement or takeover.");
                if (intent.Phase != FactoryActivationPhase.ApprovalOnly)
                    throw new FactoryActivationException(intent.Phase == FactoryActivationPhase.Rejected
                        ? FactoryActivationState.Blocked : FactoryActivationState.Partial,
                        "Original approval transition incomplete or rejected; no automatic replay, promotion or replacement.");
                if (approval is not null && approval != reviewed!.Fingerprint)
                    throw new FactoryActivationException(FactoryActivationState.Blocked, "Exact original review fingerprint required.");
            }
            if (operation is null && intent is null)
                throw new FactoryActivationException(FactoryActivationState.Blocked, "No retained approval. Review an explicit activation operation first.");
            var observed = await CaptureAsync(admission, operation ?? intent!.OperationId, token);
            if (reviewed is not null)
            {
                if (observed.Review.Fingerprint != reviewed.Fingerprint)
                    throw new FactoryActivationException(FactoryActivationState.Blocked, "Retained approval is stale against current native governance/evidence revisions.");
                return Result(FactoryActivationState.ApprovalOnly, "Original operator approval retained and currently ready; factory remains inactive.", observed, true);
            }
            reviewed = observed.Review;
            if (approval is null)
                return Result(FactoryActivationState.Reviewed, "Review complete exact retained inputs, unknowns and council result. No writes or activation.", observed, false);
            if (approval != reviewed.Fingerprint)
                throw new FactoryActivationException(FactoryActivationState.Blocked, "Stale or mismatched review fingerprint; no approval dispatched.");
            var audit = new FactoryAuditService(azure, http, clock);
            await audit.ProtectAnalysisAsync(admission, reviewed.AnalysisRevision, reviewed.AnalysisChain, reviewed.Fingerprint, token);
            intent = new(1, identity.FactoryId, reviewed.OperationId, reviewed.Fingerprint, identity.AzureActorId,
                clock.GetUtcNow(), FactoryActivationPhase.Intended);
            var reviewValue = Serialize(reviewed);
            OwnerReservationStore.CheckSize(FactoryActivation.ReviewKey(reviewed.OperationId), ProductConfigurationKeys.NoLabel, reviewValue);
            await RequireUnpausedAsync(store, identity, token);
            dispatched = true;
            coordinator = await store.WriteAsync(FactoryActivation.RootKey, Serialize(intent), null, token);
            await store.WriteAsync(FactoryActivation.ReviewKey(reviewed.OperationId), reviewValue, null, token);
            observed = await CaptureAsync(admission, reviewed.OperationId, token);
            if (observed.Review.Fingerprint != reviewed.Fingerprint)
                throw new FactoryActivationException(FactoryActivationState.Blocked, "Reviewed authority changed during conditional approval admission.");
            var intendedReceipt = new FactoryActivationApproval(1, identity.FactoryId, reviewed.OperationId, reviewed.Fingerprint, identity.AzureActorId,
                clock.GetUtcNow(), observed.ObservedAt);
            await RequireUnpausedAsync(store, identity, token);
            await store.WriteAsync(FactoryActivation.ApprovalKey(reviewed.OperationId), Serialize(intendedReceipt), null, token);
            receipt = intendedReceipt;
            intent = intent with { Phase = FactoryActivationPhase.ApprovalOnly, ApprovalSha256 = FactoryModelsValidation.Hash(receipt) };
            coordinator = await store.WriteAsync(FactoryActivation.RootKey, Serialize(intent), coordinator.ETag, token);
            observed = await CaptureAsync(admission, reviewed.OperationId, token);
            var final = await store.ReadNamespaceAsync(FactoryActivation.Prefix, token, maximumRecords: 32);
            _ = FactoryActivation.Read(final.ToDictionary(p => p.Key, p => p.Value.Value), identity.FactoryId, clock.GetUtcNow());
            if (final[FactoryActivation.RootKey].ETag != coordinator.ETag || observed.Review.Fingerprint != reviewed.Fingerprint)
                throw new FactoryActivationException(FactoryActivationState.Blocked, "Final approval or current authority changed; no current approval released.");
            await RequireUnpausedAsync(store, identity, token);
            return Result(FactoryActivationState.ApprovalOnly, "Exact operator approval retained; live promotion blocked. Approval is not activation or historical completion.", observed, true);
        }
        catch (Exception e) when (e is FactoryActivationException or FactoryAuditException or ReservationStoreException
            or PreviewCapabilityException or ModelCapabilityException or CharterOperationException or CharterAdoptionEvidenceException
            or JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException
            or HttpRequestException or IOException or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException)
        {
            var state = e switch
            {
                FactoryActivationException a => a.State,
                FactoryAuditException a when a.State == FactoryAuditState.Blocked => FactoryActivationState.Blocked,
                ReservationStoreException r when r.State == FactoryReservationState.Blocked => FactoryActivationState.Blocked,
                ReservationStoreException r when r.State == FactoryReservationState.Failed => FactoryActivationState.Failed,
                PreviewCapabilityException p when p.State == FactoryCharterState.Blocked => FactoryActivationState.Blocked,
                OperationCanceledException => dispatched ? FactoryActivationState.Unknown : FactoryActivationState.Canceled,
                _ => FactoryActivationState.Unknown,
            };
            if (dispatched && coordinator is not null && intent is not null && state == FactoryActivationState.Blocked)
            {
                try
                {
                    await store!.WriteAsync(FactoryActivation.RootKey,
                        Serialize(intent with { Phase = FactoryActivationPhase.Rejected }), coordinator.ETag, token);
                }
                catch (Exception rejection) when (rejection is ReservationStoreException or OperationCanceledException) { state = FactoryActivationState.Unknown; }
            }
            return new(state, e is FactoryActivationException ? e.Message : e.ToString())
            { Review = reviewed, Approval = receipt };
        }

        FactoryActivationResult Result(FactoryActivationState state, string detail, ActivationObservation observation, bool current) =>
            new(state, detail) { Review = reviewed, Approval = receipt, CurrentReady = true, ApprovalCurrent = current, Evaluation = observation.Evaluation };
    }

    private async Task<ActivationObservation> CaptureAsync(FactoryReservationResult admission, Guid operation, CancellationToken token)
    {
        var identity = admission.Identity!;
        var endpoint = admission.Foundation!.ProductEndpoint!;
        var store = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
        await RequireUnpausedAsync(store, identity, token);
        var manifest = FactoryGovernance.ReadInactiveManifest((await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token))?.Value,
            (await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token))?.Value, identity.ProductKey, endpoint);
        if (manifest.Identity != identity || manifest.PreviewOperationId is not { } preview || manifest.PreviewApprovalFingerprint is not { } pin)
            throw new FactoryActivationException(FactoryActivationState.Blocked, "Complete configured current preview required.");
        var observation = await new FactoryPreviewPreparationService(reviewer, azure, http, clock)
            .EvaluateAsync(admission, preview, pin, readOnly: true, token);
        if (!observation.CurrentReady || observation.Evaluation is null || observation.ObservedAt is null)
            throw new FactoryActivationException(observation.Readiness switch
            {
                PreviewReadiness.Unknown => FactoryActivationState.Unknown,
                PreviewReadiness.Canceled => FactoryActivationState.Canceled,
                PreviewReadiness.Failed => FactoryActivationState.Failed,
                PreviewReadiness.Partial => FactoryActivationState.Partial,
                _ => FactoryActivationState.Blocked,
            }, "Current complete readiness required; retained evaluation is not authority. " + observation.Reason);
        var graph = await new FactoryAuditService(azure, http, clock).ReadAsync(admission, token);
        await RequireUnpausedAsync(store, identity, token);
        var review = FactoryActivation.Review(graph, observation.Evaluation.Value, operation, identity.AzureActorId);
        using var nativePreview = JsonDocument.Parse(graph.Records.Single(r => r.Identity == review.References.Single(i => i.Store == AuditStoreKind.Preview)).Content);
        var retained = PreviewEvaluationDocument.ReadNative(nativePreview.RootElement);
        using var nativeRun = JsonDocument.Parse(graph.Records.Single(r => r.Identity == review.References.Single(i => i.Store == AuditStoreKind.Run)).Content);
        // Review the hash-verified native payload, not the runtime's independently rendered run JSON.
        var evaluation = JsonSerializer.SerializeToElement(new
        {
            Readiness = PreviewReadiness.ReadyAwaitingActivation, retained.Inputs, retained.Proof,
            Receipt = retained.Run, retained.Dispatches, Run = nativeRun.RootElement
        }, new JsonSerializerOptions(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false });
        return new(review, evaluation, observation.ObservedAt.Value);
    }

    private async Task RequireUnpausedAsync(OwnerReservationStore store, FactoryReservationIdentity identity, CancellationToken token)
    {
        try
        {
            FactoryPause.RequireAdmissionOpen((await store.ReadNamespaceAsync(FactoryPause.Key, token)).Keys);
            FactoryPause.RequireAdmissionOpen((await store.ReadNamespaceAsync(FactoryPause.HistoryPrefix, token, maximumRecords: 128)).Keys);
            await FactoryReconfigurationGate.RequireAsync(store, identity, clock, token,
                FactoryReconfigurationPhase.AwaitingActivation);
        }
        catch (PreviewCapabilityException e)
        { throw new FactoryActivationException(FactoryActivationState.Blocked, e.Message + " Approval never clears pause."); }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
    private sealed record ActivationObservation(FactoryActivationReview Review, JsonElement Evaluation, DateTimeOffset ObservedAt);
}
