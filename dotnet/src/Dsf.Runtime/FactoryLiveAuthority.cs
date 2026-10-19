using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal sealed record LiveAuthoritySnapshot(FactoryLiveCapability Live, PreviewCapabilitySnapshot Preview,
    PreviewCapabilityRequest Request, string Revision);

/// <summary>Read-only governance and native authority. A rollout authorizes a nonpublishing exercise, never promotion.</summary>
internal sealed class FactoryLiveAuthority(FactoryHostSettings settings, RuntimeDependencies dependencies, TokenCredential credential, HttpClient http,
    FactoryInspectionAuthority? inspection = null)
{
    private TimeProvider Clock => dependencies.PreviewClock;
    internal bool IsInspection => inspection is not null;

    internal async Task<LiveAuthoritySnapshot> CheckAsync(FactoryLiveRequest request, bool readiness, CancellationToken token,
        bool modelEvaluationAdmission = false)
    {
        var phase = "governance";
        try
        {
            if (readiness && inspection is not null) throw Denied("OriginalInvocationInspectionCannotEstablishReadiness");
            if (request.OperationId == Guid.Empty || !FactorySourceValidation.Hash(request.RolloutFingerprint)) throw new JsonException();
            var records = await ReadAsync(token);
            var values = records.ToDictionary(p => p.Key, p => p.Value.Value);
            var live = FactoryLive.ReadConfirmed(values.Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(), Clock.GetUtcNow());
            if (live.Plan.Fingerprint != request.RolloutFingerprint) throw Denied("ExactExplicitLiveRolloutRequired");
            var manifest = FactoryGovernance.ReadInactiveManifest(values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile), settings.Product, settings.ProductEndpoint, settings.ProfilePin);
            var identity = manifest.Identity;
            var activation = FactoryActivation.Read(values.Where(p => p.Key.StartsWith(FactoryActivation.Prefix, StringComparison.Ordinal)).ToDictionary(),
                identity.FactoryId, Clock.GetUtcNow());
            if (activation.Operation.Phase != FactoryActivationPhase.ApprovalOnly || activation.Approval is null
                || activation.Review?.Fingerprint != live.Plan.Activation.Fingerprint || live.Plan.Deployment.Manifest.Identity != identity
                || activation.Review.ManifestSha256 != FactoryModelsValidation.Hash(manifest)
                || activation.Review.ManifestRevision != records[ProductConfigurationKeys.FactoryManifest].ETag.ToString()
                || Identity(records[live.Plan.Approval.Key], identity.FactoryId) != live.Plan.Approval)
                throw Denied("CurrentOriginalOperatorApprovalRequired");
            var authorityRevision = FactoryModelsValidation.Hash(records.Values.Where(r => !r.Key.StartsWith(FactoryAudit.Namespace, StringComparison.Ordinal))
                .OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => Identity(r, identity.FactoryId)).ToArray());
            if (authorityRevision != activation.Review.AuthorityRevision) throw Denied("ReviewedFactoryGovernanceChanged");
            var previewRequest = new PreviewCapabilityRequest(manifest.PreviewOperationId ?? throw new JsonException(),
                manifest.PreviewApprovalFingerprint ?? throw new JsonException(), request.OperationId);
            phase = "original-native-capability";
            var preview = await new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, Clock, inspection).CheckAsync(previewRequest, token);
            var workload = new FactoryWorkloadCredential(credential, identity.TenantId, preview.Council, Clock);
            var native = new Dictionary<string, JsonElement>();
            foreach (var receipt in live.Receipts)
            {
                phase = "native-rollout-receipt";
                var resource = await NativeAsync(receipt.Target, receipt.ApiVersion, workload, token);
                native.Add(receipt.Target, resource);
            }
            phase = "native-creation";
            var deploymentTarget = live.Plan.Deployment.Steps[0].Target;
            var deployment = await NativeAsync(deploymentTarget, "2025-04-01", workload, token);
            var operations = await NativeAsync(deploymentTarget + "/operations", "2025-04-01", workload, token, collection: true);
            FactoryLive.ValidateCreation(live, native, deployment, operations);
            if (FactoryLive.ValidateHost(live.Plan, native) != live.Endpoint) throw new JsonException();
            var grant = live.Plan.Deployment.Steps[1].Grant!;
            phase = "metadata-role";
            var role = await NativeAsync(grant.Scope + "/providers/microsoft.authorization/roledefinitions/" + grant.RoleDefinitionId.Split('/').Last(),
                "2022-04-01", workload, token, grant.RoleDefinitionId);
            if (FactoryModelsValidation.Hash(role.GetProperty("properties")) != grant.RoleRevision) throw Denied("LiveReadOnlyMetadataRoleChanged");
            phase = "native-approved-preview";
            var original = new PreviewExecutionStore(preview, credential, http, Clock);
            _ = await original.ReadAsync(token);
            var proof = original.Document.Proof;
            var approvedPreview = activation.Review.References.Single(r => r.Store == AuditStoreKind.Preview);
            if (original.NativeIdentity != approvedPreview || original.Document.Admission is not
                { State: PreviewWorkState.Released, Readiness: PreviewReadiness.ReadyAwaitingActivation }
                || original.Document.PendingRun is not null || proof is null
                || FactoryModelsValidation.Hash(proof) != activation.Review.ProofSha256
                || original.Document.Inputs?.Revision != activation.Review.InputRevision)
                throw Denied("ExactReleasedApprovedNativePreviewRequired");
            if (readiness)
            {
                phase = "current-readiness";
                FactoryPromotionApproval? promotion = null;
                if (values.TryGetValue(FactoryPromotion.RootKey, out var promoted))
                {
                    var retained = JsonSerializer.Deserialize<FactoryPromotionApproval>(promoted, FactorySourceValidation.RecordJson) ?? throw new JsonException();
                    if (values.ContainsKey(FactoryPromotion.CompletionKey(retained.OperationId)))
                    {
                        var publisher = FactoryPublisher.ReadConfirmed(values.Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal))
                            .ToDictionary(), Clock.GetUtcNow());
                        promotion = FactoryPromotion.RequireApproved(values, records.ToDictionary(p => p.Key, p => Identity(p.Value, identity.FactoryId)),
                            publisher.Plan, Clock.GetUtcNow());
                    }
                }
                var current = promotion is null
                    ? await RuntimeWorkflow.InspectPreviewAsync(settings, dependencies, previewRequest, http, token)
                    : await RuntimeWorkflow.InspectPromotedPreviewAsync(settings, dependencies, previewRequest, http, promotion, token, modelEvaluationAdmission);
                if (current.Readiness != PreviewReadiness.ReadyAwaitingActivation)
                    throw Denied("CurrentCompleteReadinessRequired:" + current.Reason);
            }
            phase = "revision-recheck";
            var currentRecords = await ReadAsync(token);
            var revision = Revision(records);
            if (Revision(currentRecords) != revision) throw Denied("LiveGovernanceChangedDuringAdmission");
            return new(live, preview, previewRequest, FactoryModelsValidation.Hash(new { GovernanceRevision = revision, CapabilityRevision = preview.Revision,
                Native = native.OrderBy(p => p.Key).Select(p => new { p.Key, Hash = FactoryModelsValidation.Hash(p.Value) }),
                Deployment = FactoryModelsValidation.Hash(deployment), Operations = FactoryModelsValidation.Hash(operations), Role = FactoryModelsValidation.Hash(role) }));
        }
        catch (PreviewCapabilityException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException
            or HttpRequestException or IOException or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException)
        { throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ProtectedLiveAuthorityUnconfirmed:" + phase); }
    }

    private async Task<Dictionary<string, ConfigurationSetting>> ReadAsync(CancellationToken token)
    {
        var result = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        var gateway = new AzureConfigurationSettingsGateway(credential, http);
        var transition = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prefix in new[] { FactoryPause.Key, FactoryPause.HistoryPrefix, FactoryReconfigurationOperationRecords.Prefix })
            await foreach (var record in gateway.ListRecordsAsync(settings.ProductEndpoint, "*", token, prefix + "*"))
                if (inspection is not null) _ = inspection.Include(record.Key, record.Value);
                else if (prefix == FactoryReconfigurationOperationRecords.Prefix) transition.Add(record.Key, record.Value);
                else RuntimeWorkflow.EnsureCouncilAdmissionOpen([record.Key]);
        inspection?.CompleteTransition(Clock.GetUtcNow());
        if (inspection is null) FactoryReconfigurationOperationRecords.RequireAdmissionOpen(transition, Clock.GetUtcNow(),
            FactoryReconfigurationPhase.AwaitingActivation, FactoryReconfigurationPhase.Completed);
        await foreach (var record in gateway.ListRecordsAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
        {
            if (inspection is not null && !inspection.Include(record.Key, record.Value)) continue;
            if (record.Key.StartsWith(FactoryReconfigurationOperationRecords.Prefix, StringComparison.Ordinal)) continue;
            RuntimeWorkflow.EnsureCouncilAdmissionOpen([record.Key]);
            if (result.Count >= FactoryPreviewPublication.Policy.MaximumRecords + FactoryAudit.GovernanceHistoryRecords
                || System.Text.Encoding.UTF8.GetByteCount(record.Key + record.Value) + 512 > FactoryCharterLimits.RecordBytes
                || record.ETag.ToString().Length == 0 || !result.TryAdd(record.Key, record)) throw new JsonException();
        }
        foreach (var prefix in new[] { FactoryLive.Prefix, FactoryActivation.Prefix })
        {
            var keys = new HashSet<string>();
            await foreach (var record in gateway.ListRecordsAsync(settings.ProductEndpoint, "*", token, prefix + "*"))
                if (record.Label is not null || !keys.Add(record.Key) || !result.TryGetValue(record.Key, out var captured)
                    || record.Value != captured.Value || record.ETag != captured.ETag) throw new JsonException();
            if (!keys.SetEquals(result.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))) throw new JsonException();
        }
        return result;
    }

    private async Task<JsonElement> NativeAsync(string target, string api, TokenCredential workload, CancellationToken token, string? identity = null,
        bool collection = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + target + "?api-version=" + api);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await workload.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), token)).Token);
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode != HttpStatusCode.OK) throw Denied("CurrentLiveNativeAuthorityUnavailable");
        using var body = await EvidencePayload.ReadAsync(response, token, 256 * 1024);
        if (!collection && !string.Equals(body.RootElement.GetProperty("id").GetString(), identity ?? target, StringComparison.OrdinalIgnoreCase))
            throw new JsonException();
        return body.RootElement.Clone();
    }

    private static AuditRecordIdentity Identity(ConfigurationSetting record, Guid factory) =>
        new(factory, AuditStoreKind.Governance, record.Key, record.ETag.ToString(), FactoryAudit.TextHash(record.Value));
    private static string Revision(Dictionary<string, ConfigurationSetting> records) => FactoryModelsValidation.Hash(
        records.Where(p => FactoryPromotion.IsLiveAuthorityKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new { p.Key, p.Value.Value, ETag = p.Value.ETag.ToString(), p.Value.ContentType }));
    private static PreviewCapabilityException Denied(string reason) => new(FactoryCharterState.Blocked, reason);
}
