using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    internal async Task<FactoryLiveResult> LiveAsync(FactoryReservationResult admission, FactoryLiveSelection? selection,
        string? approval, CancellationToken token)
    {
        FactoryLivePlan? plan = null;
        FactoryLiveCoordinator? coordinator = null;
        ConfigurationSetting? root = null;
        OwnerReservationStore? liveStore = null;
        ConfigurationSetting? pendingEffectRecord = null;
        PreviewPreparationEffect? pendingEffect = null;
        var receipts = new List<PreviewPreparationReceipt>();
        var dispatched = false;
        try
        {
            var store = Store(admission);
            liveStore = store;
            var records = await store.ReadNamespaceAsync(FactoryLive.Prefix, token, FactoryLive.MaximumRecords);
            if (records.Count != 0)
            {
                if (!records.TryGetValue(FactoryLive.RootKey, out root)) throw Unknown("OriginalLiveCoordinatorMissingNoRecreation");
                coordinator = Parse<FactoryLiveCoordinator>(root.Value);
                plan = FactoryLive.ReadPlan(coordinator, records.ToDictionary(p => p.Key, p => p.Value.Value));
                CheckCapacity(plan.Deployment, plan);
                if (plan.Deployment.WorkloadTemplateSha256 != FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-live.json"))))
                    throw Failure("OriginalPackagedLiveTemplateRequired");
                if (coordinator.ApprovedAt < plan.Deployment.Manifest.Identity.CreatedAt || coordinator.ApprovedAt > clock.GetUtcNow()
                    || selection is not null && FactoryModelsValidation.Hash(selection) != FactoryModelsValidation.Hash(plan.Selection)
                    || approval is not null && approval != plan.Fingerprint) throw Failure("ExactOriginalLiveRolloutRequired");
                if (coordinator.State != FactoryLiveState.PreparedAwaitingPublication)
                    return Result(coordinator.State is FactoryLiveState.Failed or FactoryLiveState.Canceled ? coordinator.State : FactoryLiveState.Unknown,
                        "Original live rollout is incomplete or uncertain; inspect native requests, never replay or replace.");
                _ = FactoryLive.ReadConfirmed(records.ToDictionary(p => p.Key, p => p.Value.Value), clock.GetUtcNow());
                var effects = new List<PreviewPreparationEffect>();
                var effectRecords = new List<ConfigurationSetting>();
                var expectedKeys = new HashSet<string> { FactoryLive.RootKey };
                for (var i = 0; i < coordinator.PlanChunks; i++) expectedKeys.Add(FactoryLive.ChunkKey(coordinator.OperationId, i));
                foreach (var step in plan.Deployment.Steps)
                {
                    var effectKey = FactoryLive.EffectKey(coordinator.OperationId, step.Index);
                    var record = records[effectKey];
                    expectedKeys.Add(effectKey);
                    var effect = Parse<PreviewPreparationEffect>(record.Value);
                    if (effect.Protocol != 1 || effect.FactoryId != coordinator.FactoryId || effect.OperationId != coordinator.OperationId
                        || effect.PlanFingerprint != plan.Fingerprint || effect.Index != step.Index || effect.Target != step.Target
                        || effect.BodySha256 != step.BodySha256 || effect.State != PreviewPreparationEffectState.Confirmed
                        || effect.ReceiptHashes.Count != step.ResourceTargets.Count || effect.IntendedAt < coordinator.ApprovedAt
                        || effect.IntendedAt > clock.GetUtcNow() || effect.RequestId == Guid.Empty) throw new JsonException();
                    effects.Add(effect);
                    effectRecords.Add(record);
                    for (var i = 0; i < effect.ReceiptHashes.Count; i++)
                    {
                        var key = FactoryLive.ReceiptKey(coordinator.OperationId, step.Index, i);
                        var receipt = Parse<PreviewPreparationReceipt>(records[key].Value);
                        if (FactoryModelsValidation.Hash(receipt) != effect.ReceiptHashes[i]) throw new JsonException();
                        expectedKeys.Add(key);
                        receipts.Add(receipt);
                    }
                }
                if (!expectedKeys.SetEquals(records.Keys) || coordinator.ConfirmedEffects != plan.Deployment.Steps.Count) throw new JsonException();
                await RequireLiveCurrentAsync(allowPromotion: true);
                await VerifyConfirmedAsync(NativeJournal(effects, effectRecords), token, (resources, endpoint) => ValidateLiveHost(plan, resources, endpoint));
                if ((await store.ReadAsync(FactoryLive.RootKey, token))?.ETag != root.ETag) throw Unknown("LiveCoordinatorChanged");
                return Result(FactoryLiveState.PreparedAwaitingPublication, "Original native live host and exact grants confirmed. Rollout status alone does not establish activation or authorize publication.");
            }
            if (selection is null) throw Failure("ExplicitLiveSelectionRequired");
            FactoryLive.Validate(selection);
            var approved = await new FactoryActivationService(reviewer, azure, http, clock).ExecuteAsync(admission, null, null, token);
            if (!approved.ApprovalCurrent || approved.Review is null || approved.Approval is null) throw Failure("CurrentExplicitOperatorApprovalRequired");
            var original = await ReadJournalAsync(admission, store, token) ?? throw Failure("OriginalNativePreviewRequired");
            var nativePlan = original.Plan;
            var manifest = Parse<FactoryManifest>((await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token))!.Value);
            var arm = new FactoryPreparationArm(azure, http, manifest.Identity);
            var native = new Dictionary<string, PreviewPreparationNative>();
            var role = await MetadataRoleAsync(arm, selection.MetadataRoleDefinitionId, FactoryPreviewValidation.MetadataActions, native, token);
            if (!selection.MetadataRoleDefinitionId.StartsWith("/subscriptions/" + manifest.Identity.SubscriptionId + "/", StringComparison.Ordinal))
                throw Failure("ExactReviewedSubscriptionMetadataRoleRequired");
            var host = FactoryLive.HostTarget(nativePlan);
            var account = nativePlan.Bindings["account"].Target;
            var group = account[..account.IndexOf("/providers/", StringComparison.Ordinal)];
            var grant = Grant(host, selection.MetadataRoleDefinitionId, nativePlan.Bindings["council"].PrincipalId!, role,
                FactoryPreviewValidation.MetadataActions);
            await GrantPermissionAsync(arm, grant, token);
            await OperatorPermissionAsync(arm, new(group, ["Microsoft.Resources/deployments/write", "Microsoft.App/containerApps/write",
                "Microsoft.App/containerApps/authConfigs/write"]), token);
            var template = File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-live.json"));
            using var parsed = JsonDocument.Parse(template);
            var parameters = new Dictionary<string, object>
            {
                ["namePrefix"] = FactoryPreviewValidation.Prefix(manifest.Identity.FactoryId),
                ["location"] = admission.Plan!.Selection.FactoryLocation, ["productKey"] = manifest.Identity.ProductKey,
                ["productEndpoint"] = admission.Foundation!.ProductEndpoint!, ["profilePin"] = manifest.ProfilePin,
                ["tenantId"] = manifest.Identity.TenantId, ["operatorPrincipalId"] = manifest.Identity.AzureActorId,
                ["runtimeImage"] = selection.RuntimeImage, ["audienceClientId"] = selection.AudienceClientId,
            };
            var body = Serialize(new { properties = new { mode = "Incremental", template = parsed.RootElement,
                parameters = parameters.ToDictionary(p => p.Key, p => new { value = p.Value }) } });
            var target = group + "/providers/microsoft.resources/deployments/live-" + selection.OperationId.ToString("N");
            var deploymentGrant = Grant(target, selection.MetadataRoleDefinitionId, nativePlan.Bindings["council"].PrincipalId!, role,
                FactoryPreviewValidation.MetadataActions);
            await GrantPermissionAsync(arm, deploymentGrant, token);
            var steps = new[]
            {
                new PreviewPreparationStep(0, PreviewPreparationAction.WorkloadDeployment, target, "2025-04-01",
                    FactoryAudit.TextHash(body), [host, host + "/authconfigs/current"], null),
                new PreviewPreparationStep(1, PreviewPreparationAction.MetadataAssignment, grant.Target, "2022-04-01",
                    FactoryAudit.TextHash(FactoryPreviewPreparation.GrantBody(grant)), [grant.Target], grant),
                new PreviewPreparationStep(2, PreviewPreparationAction.MetadataAssignment, deploymentGrant.Target, "2022-04-01",
                    FactoryAudit.TextHash(FactoryPreviewPreparation.GrantBody(deploymentGrant)), [deploymentGrant.Target], deploymentGrant),
            };
            var approvalRecord = await store.ReadAsync(FactoryActivation.ApprovalKey(approved.Review.OperationId), token) ?? throw new JsonException();
            plan = new(1, selection, approved.Review, new(manifest.Identity.FactoryId, AuditStoreKind.Governance, approvalRecord.Key,
                approvalRecord.ETag.ToString(), FactoryAudit.TextHash(approvalRecord.Value)), nativePlan with
                { Manifest = manifest, WorkloadBody = body, WorkloadTemplateSha256 = FactoryAudit.TextHash(Encoding.UTF8.GetString(template)), Steps = steps,
                    Prerequisites = native.Values.ToArray(),
                    OperatorPermissions = [
                        new(group, ["Microsoft.Resources/deployments/write", "Microsoft.App/containerApps/write", "Microsoft.App/containerApps/authConfigs/write"]),
                        new(host, ["Microsoft.Authorization/roleAssignments/write"]), new(target, ["Microsoft.Authorization/roleAssignments/write"])],
                    CostsAndPrerequisites = ["One separately pinned private native host, one replica, 0.25 CPU / 0.5Gi.",
                        "Existing API registration, consent, image pull, private routing and exact scoped read-only metadata role.",
                        "Explicit native exercise incurs actual source/model/telemetry costs; no automatic evaluation.",
                        "Create-once publication and a separate promotion approval are required before activation."] });
            foreach (var step in steps)
            {
                await AbsentAsync(arm, step.Target, step.ApiVersion, token);
                foreach (var resource in step.ResourceTargets) await AbsentAsync(arm, resource, TargetApi(resource), token);
            }
            var bytes = Encoding.UTF8.GetBytes(Serialize(plan));
            if (bytes.Length > FactoryPreviewPreparation.Policy.PlanBytes) throw Failure("CompleteLivePlanCapacityExceeded");
            coordinator = new(1, manifest.Identity.FactoryId, selection.OperationId, plan.Fingerprint, FactoryReservationRecords.Hash(bytes),
                bytes.Length, (bytes.Length + 4095) / 4096, manifest.Identity.AzureActorId, clock.GetUtcNow(), FactoryLiveState.Preparing, 0);
            CheckCapacity(plan.Deployment, plan);
            OwnerReservationStore.CheckSize(FactoryLive.RootKey, "", Serialize(coordinator with
                { ApprovedAt = DateTimeOffset.MaxValue, State = FactoryLiveState.PreparedAwaitingPublication, ConfirmedEffects = steps.Length }), ContentType);
            if (approval is null) return Result(FactoryLiveState.Reviewed, "Review exact live image, native identities, auth callers, narrow metadata grant and existing approval. No writes.");
            if (approval != plan.Fingerprint) throw Failure("ExactCurrentLiveRolloutApprovalRequired");
            await RequireLiveCurrentAsync();
            root = await WriteAsync(store, FactoryLive.RootKey, coordinator, null, token);
            for (var i = 0; i < coordinator.PlanChunks; i++)
                await WriteAsync(store, FactoryLive.ChunkKey(coordinator.OperationId, i),
                    Convert.ToBase64String(bytes.AsSpan(i * 4096, Math.Min(4096, bytes.Length - i * 4096))), null, token);
            var nativeEffects = new List<PreviewPreparationEffect>();
            var nativeRecords = new List<ConfigurationSetting>();
            foreach (var step in steps)
            {
                await RequireLiveCurrentAsync();
                if ((await store.ReadAsync(FactoryLive.RootKey, token))?.ETag != root.ETag) throw Unknown("LiveCoordinatorOwnershipChanged");
                await AbsentAsync(arm, step.Target, step.ApiVersion, token);
                foreach (var resource in step.ResourceTargets) await AbsentAsync(arm, resource, TargetApi(resource), token);
                if (step.Grant is not null) await GrantPermissionAsync(arm, step.Grant, token);
                var effect = new PreviewPreparationEffect(1, coordinator.FactoryId, coordinator.OperationId, plan.Fingerprint,
                    step.Index, step.Target, step.BodySha256, Guid.NewGuid(), clock.GetUtcNow(), PreviewPreparationEffectState.Intended, [], null);
                var key = FactoryLive.EffectKey(coordinator.OperationId, step.Index);
                var effectRecord = await WriteAsync(store, key, effect, null, token);
                effect = effect with { State = PreviewPreparationEffectState.Dispatched };
                effectRecord = await WriteAsync(store, key, effect, effectRecord, token);
                pendingEffect = effect;
                pendingEffectRecord = effectRecord;
                await RequireLiveCurrentAsync();
                dispatched = true;
                var result = await DispatchAsync(plan.Deployment, step, effect, arm, async acknowledgment =>
                {
                    effect = effect with { Acknowledgment = acknowledgment };
                    effectRecord = await WriteAsync(store, key, effect, effectRecord, token);
                    pendingEffect = effect;
                    pendingEffectRecord = effectRecord;
                }, token, (resources, endpoint) => ValidateLiveHost(plan, resources, endpoint));
                for (var i = 0; i < result.Receipts.Count; i++)
                    await WriteAsync(store, FactoryLive.ReceiptKey(coordinator.OperationId, step.Index, i), result.Receipts[i], null, token);
                receipts.AddRange(result.Receipts);
                effect = effect with { State = PreviewPreparationEffectState.Confirmed,
                    ReceiptHashes = result.Receipts.Select(FactoryModelsValidation.Hash).ToArray(),
                    CouncilEndpoint = result.CouncilEndpoint, DeploymentOutputsSha256 = result.OutputsSha256 };
                nativeRecords.Add(await WriteAsync(store, key, effect, effectRecord, token));
                nativeEffects.Add(effect);
                coordinator = coordinator with { ConfirmedEffects = step.Index + 1 };
                root = await WriteAsync(store, FactoryLive.RootKey, coordinator, root, token);
                dispatched = false;
                pendingEffect = null;
                pendingEffectRecord = null;
            }
            await RequireLiveCurrentAsync();
            await VerifyConfirmedAsync(NativeJournal(nativeEffects, nativeRecords), token, (resources, endpoint) => ValidateLiveHost(plan, resources, endpoint));
            coordinator = coordinator with { State = FactoryLiveState.PreparedAwaitingPublication };
            root = await WriteAsync(store, FactoryLive.RootKey, coordinator, root, token);
            await RequireLiveCurrentAsync();
            return Result(FactoryLiveState.PreparedAwaitingPublication, "Native live host and exact host/deployment read-only metadata grants provisioned. Final promotion requires create-once publication and separate approval.");

            async Task RequireLiveCurrentAsync(bool allowPromotion = false)
            {
                await RequireCurrentReviewedActivationAsync(admission, store, plan!.Activation, allowPromotion, token);
                var record = await store.ReadAsync(plan.Approval.Key, token);
                if (record is null || record.ETag.ToString() != plan.Approval.NativeRevision || FactoryAudit.TextHash(record.Value) != plan.Approval.Sha256)
                    throw Unknown("OriginalActivationReceiptChanged");
                foreach (var prerequisite in plan.Deployment.Prerequisites)
                    if (FactoryModelsValidation.Hash(await ReadNativeAsync(new FactoryPreparationArm(azure, http, plan.Deployment.Manifest.Identity),
                        prerequisite.Target, prerequisite.ApiVersion, token)) != prerequisite.Revision) throw Failure("LiveNativeRoleChanged");
            }
            Journal NativeJournal(IReadOnlyList<PreviewPreparationEffect> effects, IReadOnlyList<ConfigurationSetting> effectRecords) =>
                new(plan!.Deployment, new(1, FactoryPreviewPreparation.Kind, coordinator!.FactoryId, coordinator.OperationId,
                    plan.Fingerprint, coordinator.PlanSha256, coordinator.PlanBytes, coordinator.PlanChunks, coordinator.ApprovedBy,
                    coordinator.ApprovedAt, Guid.Empty, PreviewPreparationState.ConfirmedNotReady, coordinator.ConfirmedEffects, null),
                    root!, effects, effectRecords, receipts);
        }
        catch (Exception e) when (e is ReservationStoreException or PreviewCapabilityException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or FormatException or HttpRequestException or IOException or OperationCanceledException
            or NativePreparationRejectedException or OwnerReservationStore.AuthenticationFailedException)
        {
            var state = e is NativePreparationRejectedException ? FactoryLiveState.Failed
                : dispatched || e is ReservationStoreException { State: FactoryReservationState.Unknown } ? FactoryLiveState.Unknown
                : e is OperationCanceledException ? FactoryLiveState.Canceled
                : root is not null ? FactoryLiveState.Unknown : FactoryLiveState.Blocked;
            if (root is not null && coordinator is not null && liveStore is not null && approval is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                try
                {
                    if (pendingEffect is not null && pendingEffectRecord is not null)
                        await WriteAsync(liveStore, pendingEffectRecord.Key, pendingEffect with
                        {
                            State = e is NativePreparationRejectedException ? PreviewPreparationEffectState.KnownFailed : PreviewPreparationEffectState.Unknown,
                            Reason = e is NativePreparationRejectedException ? "NativeRequestRejectedWithoutEffect" : "OriginalNativeEffectUnconfirmedNoReplay"
                        }, pendingEffectRecord, cleanup.Token);
                    coordinator = coordinator with { State = state };
                    root = await WriteAsync(liveStore, FactoryLive.RootKey, coordinator, root, cleanup.Token);
                }
                catch (Exception failure) when (failure is ReservationStoreException or OperationCanceledException or HttpRequestException
                    or IOException or JsonException or InvalidOperationException or OwnerReservationStore.AuthenticationFailedException)
                { state = FactoryLiveState.Unknown; }
            }
            return Result(state,
                "Live rollout authority, native request or receipt unconfirmed. Retain original operation; no replay, adoption, reset or activation.");
        }
        FactoryLiveResult Result(FactoryLiveState state, string detail) => new(state, detail) { Plan = plan, Coordinator = coordinator, Receipts = receipts };
    }

    private static void ValidateLiveHost(FactoryLivePlan plan, IReadOnlyDictionary<string, JsonElement> resources, string endpoint)
    {
        var actual = FactoryLive.ValidateHost(plan, resources);
        if (actual != endpoint) throw new JsonException("Native live endpoint differs.");
    }
}
