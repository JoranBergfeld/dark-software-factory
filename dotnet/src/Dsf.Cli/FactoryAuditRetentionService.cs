using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryAuditService
{
    internal async Task ProtectAnalysisAsync(FactoryReservationResult admission, CharterReview review, CancellationToken token)
        => await ProtectAnalysisAsync(admission, review.AnalysisRevision, review.AnalysisChain, review.Fingerprint, token);

    internal async Task ProtectAnalysisAsync(FactoryReservationResult admission, Guid revision, string chain, string context, CancellationToken token)
    {
        var store = ProductStore(admission);
        var key = FactoryAuditRetention.ClaimKey(revision);
        var previous = await store.ReadAsync(key, token);
        if (previous is not null)
        {
            var claim = JsonSerializer.Deserialize<AnalysisRetentionClaim>(previous.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (claim.Kind != AnalysisRetentionKind.Protected || claim.FactoryId != admission.Identity!.FactoryId
                || claim.Revision != revision || claim.ChainSha256 != chain)
                throw new CharterOperationException(FactoryCharterState.Blocked, "Analysis expiry/protection already claimed; no dangling charter reference or takeover.");
            return;
        }
        await store.RequirePristineAsync(key + "/", token);
        var held = new AnalysisRetentionClaim(1, admission.Identity!.FactoryId, revision, AnalysisRetentionKind.Protected,
            Guid.NewGuid(), admission.Identity.AzureActorId, clock.GetUtcNow(), context, chain, 0);
        await store.WriteAsync(key, Serialize(held), null, token);
    }

    internal async Task<FactoryAuditResult> ApprovePolicyAsync(FactoryReservationResult admission, string? approval, CancellationToken token)
    {
        var store = ProductStore(admission);
        var identity = admission.Identity!;
        var fingerprint = FactoryModelsValidation.Hash(new { identity.FactoryId, FactoryAudit.Policy, Days = 90, identity.AzureActorId });
        if (approval is null) return new(FactoryAuditState.Reviewed, "Review 90-day otherwise-unused historical analysis policy. Approval alone cannot expire protected or younger evidence.")
            { ApprovalFingerprint = fingerprint };
        if (approval != fingerprint) throw new FactoryAuditException(FactoryAuditState.Blocked, "Exact retention policy fingerprint required.");
        var previous = await store.ReadAsync(FactoryAuditRetention.PolicyKey, token);
        if (previous is not null)
        {
            CheckPolicy(previous.Value, identity.FactoryId, identity.AzureActorId);
            return new(FactoryAuditState.Inspected, "Original explicit retention policy retained; no expiry or deletion performed.");
        }
        await store.WriteAsync(FactoryAuditRetention.PolicyKey,
            Serialize(new AuditRetentionApproval(1, identity.FactoryId, FactoryAudit.Policy, 90, identity.AzureActorId, clock.GetUtcNow())), null, token);
        return new(FactoryAuditState.Inspected, "Explicit retention policy approved separately from reached eligibility; no deletion performed.");
    }

    internal async Task<FactoryAuditResult> ExpireAsync(FactoryReservationResult admission, AuditGraph graph, Guid revision,
        string? approval, CancellationToken token)
    {
        var identity = admission.Identity!;
        var store = ProductStore(admission);
        var policy = await store.ReadAsync(FactoryAuditRetention.PolicyKey, token)
            ?? throw new FactoryAuditException(FactoryAuditState.Blocked, "Explicit --approve-policy review/approval required separately from reached expiry.");
        CheckPolicy(policy.Value, identity.FactoryId, identity.AzureActorId);
        var targets = graph.Records.Where(r => r.Identity.Store == AuditStoreKind.Analysis
            && r.Identity.Key.StartsWith($"intent/{revision:N}/steps/", StringComparison.Ordinal)).OrderBy(r => r.Identity.Key, StringComparer.Ordinal).ToArray();
        if (targets.Length == 0 || targets.Any(t => t.Status != AuditRetentionStatus.Eligible))
            throw new FactoryAuditException(FactoryAuditState.Blocked, "Only unprotected, otherwise-unused historical analysis at the reached 90-day boundary is eligible.");
        var fingerprint = FactoryModelsValidation.Hash(new { graph.FactoryId, graph.Revision, Analysis = revision,
            PolicyRevision = policy.ETag.ToString(), Targets = targets.Select(t => t.Identity) });
        if (approval is null) return Inspect(graph, 0) with { State = FactoryAuditState.Reviewed, ApprovalFingerprint = fingerprint,
            Detail = "Review exact eligible analysis records and native revisions. No source, governance, run or infrastructure deletion." };
        if (approval != fingerprint) throw new FactoryAuditException(FactoryAuditState.Blocked, "Exact current expiry fingerprint required; no deletion.");
        await CheckAnalysisStoreAsync();
        var key = FactoryAuditRetention.ClaimKey(revision);
        await store.RequirePristineAsync(key + "/", token);
        if (await store.ReadAsync(key, token) is not null)
            throw new FactoryAuditException(FactoryAuditState.Blocked, "Analysis protection or expiry already claimed; no takeover or retry.");
        var history = graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Analysis
            && r.Identity.Key.StartsWith("intent/history/", StringComparison.Ordinal)
            && JsonSerializer.Deserialize<IntentProgress>(r.Content, FactorySourceValidation.RecordJson)!.Revision == revision);
        var progress = JsonSerializer.Deserialize<IntentProgress>(history.Content, FactorySourceValidation.RecordJson)!;
        var claim = new AnalysisRetentionClaim(1, identity.FactoryId, revision, AnalysisRetentionKind.Expiring, Guid.NewGuid(),
            identity.AzureActorId, clock.GetUtcNow(), graph.Revision, progress.ChainSha256!, targets.Length);
        var root = await store.WriteAsync(key, Serialize(claim), null, token);
        var data = new OwnerReservationStore(FactorySourceValidation.ProgressEndpoint(identity.FactoryId),
            identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
        var evidenceKey = FactoryAuditRetention.EvidenceKey(revision);
        Azure.Data.AppConfiguration.ConfigurationSetting interlock;
        try
        {
            interlock = await data.WriteAsync(evidenceKey, Serialize(new AnalysisEvidenceClaim(1, identity.FactoryId, revision,
                progress.ChainSha256!, AnalysisRetentionKind.Expiring, claim.Operation, graph.Revision, targets.Length)),
                null, token, contentType: FactoryAuditRetention.EvidenceContentType);
        }
        catch (ReservationStoreException e) when (e.State == FactoryReservationState.Blocked)
        {
            var retained = await data.ReadAsync(evidenceKey, token) ?? throw new JsonException();
            var hold = JsonSerializer.Deserialize<AnalysisEvidenceClaim>(retained.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            FactoryAuditRetention.RequireEvidence(hold, identity.FactoryId, revision, progress.ChainSha256!, targets.Length);
            if (hold.Kind != AnalysisRetentionKind.Protected) throw new JsonException();
            await store.WriteAsync(key, Serialize(claim with { Kind = AnalysisRetentionKind.Protected }), root.ETag, token);
            throw new FactoryAuditException(FactoryAuditState.Blocked, "Runtime retained this exact analysis before expiry admission; protection recorded, no deletion dispatched.");
        }
        for (var i = 0; i < targets.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var effectKey = FactoryAuditRetention.EffectKey(revision, i);
            var effect = new AuditDeleteEffect(claim.Operation, i, targets[i].Identity, AuditDeleteState.Intended);
            var effectRecord = await store.WriteAsync(effectKey, Serialize(effect), null, token);
            var currentRoot = await store.ReadAsync(key, token);
            if (currentRoot?.ETag != root.ETag || currentRoot.Value != root.Value) throw new JsonException();
            var currentInterlock = await data.ReadAsync(evidenceKey, token);
            if (currentInterlock?.ETag != interlock.ETag || currentInterlock.Value != interlock.Value
                || currentInterlock.ContentType != FactoryAuditRetention.EvidenceContentType)
                throw new JsonException("Original native analysis retention interlock changed.");
            var current = await data.ReadAsync(effect.Target.Key, token);
            if (current?.ETag.ToString() != effect.Target.NativeRevision || current is null
                || FactoryAudit.TextHash(current.Value) != effect.Target.Sha256) throw new JsonException();
            effectRecord = await store.WriteAsync(effectKey, Serialize(effect with { State = AuditDeleteState.Dispatched }), effectRecord.ETag, token);
            try { await data.DeleteAsync(current, token); }
            catch (FactoryAuditException e)
            {
                using var closeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                await store.WriteAsync(effectKey, Serialize(effect with { State = e.State == FactoryAuditState.Blocked
                    ? AuditDeleteState.KnownNoEffect : AuditDeleteState.Unknown }), effectRecord.ETag, closeout.Token);
                throw new FactoryAuditException(token.IsCancellationRequested ? FactoryAuditState.Canceled
                    : i > 0 && e.State == FactoryAuditState.Blocked ? FactoryAuditState.Partial : e.State,
                    e.Message + " Original protected expiry claim retained; remaining records were not dispatched.");
            }
            await store.WriteAsync(effectKey, Serialize(effect with { State = AuditDeleteState.Confirmed }), effectRecord.ETag, token);
        }
        await store.WriteAsync(key, Serialize(claim with { Kind = AnalysisRetentionKind.Expired }), root.ETag, token);
        return new(FactoryAuditState.Expired, "Exact unused historical analysis expired through conditional record deletes; protected evidence, lineage and all infrastructure retained.")
            { Revision = graph.Revision };

        async Task CheckAnalysisStoreAsync()
        {
            var source = JsonSerializer.Deserialize<FactorySource>(graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Governance
                && r.Identity.Key == ProductConfigurationKeys.FactorySource).Content, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            var effect = JsonSerializer.Deserialize<FoundationOperation>(graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Governance
                && r.Identity.Key == ProductConfigurationKeys.SourceOperation(source.OperationId, 0)).Content, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            using var response = await new FactoryPreparationArm(azure, http, identity).SendAsync(HttpMethod.Get, effect.Target, effect.ApiVersion, token);
            if (!response.IsSuccessStatusCode) throw new FactoryAuditException(FactoryAuditState.Blocked, "Original analysis-store native incarnation cannot be verified; no deletion.");
            await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
            using var native = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (effect.State != FoundationOperationState.Completed || effect.Disposition != FoundationResourceDisposition.Created
                || !string.Equals(native.RootElement.GetProperty("id").GetString(), effect.Target, StringComparison.OrdinalIgnoreCase)
                || native.RootElement.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset()
                    != DateTimeOffset.Parse(effect.Incarnation!, System.Globalization.CultureInfo.InvariantCulture)
                || native.RootElement.GetProperty("properties").GetProperty("endpoint").GetString()
                    != FactorySourceValidation.ProgressEndpoint(identity.FactoryId))
                throw new FactoryAuditException(FactoryAuditState.Blocked, "Analysis-store incarnation changed; preserve replacement and original audit claim.");
        }
    }

    private OwnerReservationStore ProductStore(FactoryReservationResult admission) => new(admission.Foundation!.ProductEndpoint!,
        admission.Identity!.TenantId, admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
    private void CheckPolicy(string value, Guid factory, string actor)
    {
        var policy = JsonSerializer.Deserialize<AuditRetentionApproval>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (policy.Protocol != 1 || policy.FactoryId != factory || policy.Policy != FactoryAudit.Policy || policy.Days != 90
            || policy.ApprovedBy != actor || policy.ApprovedAt > clock.GetUtcNow()) throw new JsonException();
    }
}
