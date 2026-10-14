using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    internal async Task<FactoryPublisherResult> PublisherAsync(FactoryReservationResult admission, FactoryLiveSelection? selection,
        string? approval, CancellationToken token)
    {
        FactoryPublisherPlan? plan = null;
        FactoryPublisherCoordinator? coordinator = null;
        ConfigurationSetting? root = null, pendingRecord = null;
        PreviewPreparationEffect? pendingEffect = null;
        OwnerReservationStore? store = null;
        var receipts = new List<PreviewPreparationReceipt>();
        var issued = false;
        FactoryPublisherObservation? observation = null;
        try
        {
            store = Store(admission);
            var records = await store.ReadNamespaceAsync(FactoryPublisher.Prefix, token, FactoryPublisher.MaximumRecords);
            if (records.Count != 0)
            {
                if (!records.TryGetValue(FactoryPublisher.RootKey, out root)) throw Unknown("OriginalPublicationCoordinatorMissingNoRecreation");
                coordinator = Parse<FactoryPublisherCoordinator>(root.Value);
                plan = FactoryPublisher.ReadPlan(coordinator, Values(records));
                CheckCapacity(plan.Deployment, publisher: plan);
                if (plan.Deployment.WorkloadTemplateSha256 != FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-publisher.json")))
                    || selection is not null && FactoryModelsValidation.Hash(selection) != FactoryModelsValidation.Hash(plan.Selection)
                    || approval is not null && approval != plan.Fingerprint) throw Failure("ExactOriginalPublicationRolloutRequired");
                if (coordinator.State != FactoryPublisherState.PublicationPreparedAwaitingPromotion)
                    return Result(coordinator.State is FactoryPublisherState.Canceled or FactoryPublisherState.Failed ? coordinator.State : FactoryPublisherState.Unknown,
                        "Original native publication rollout incomplete or uncertain; retain original requests, no replay or replacement.");
                var capability = FactoryPublisher.ReadConfirmed(Values(records), clock.GetUtcNow());
                receipts.AddRange(capability.Receipts);
                await CurrentAsync(readiness: true, allowPromotion: true);
                await VerifyConfirmedAsync(Journal(capability.Effects, capability.Effects.Select(e =>
                    records[FactoryPublisher.EffectKey(coordinator.OperationId, e.Index)]).ToArray()), token, Validate);
                if ((await store.ReadAsync(FactoryPublisher.RootKey, token))?.ETag != root.ETag) throw Unknown("PublicationCoordinatorChanged");
                if (coordinator.NativeProviderProof is not { } proof
                    || !records.TryGetValue(proof.Key, out var retainedProof) || retainedProof.ETag.ToString() != proof.NativeRevision)
                    throw Unknown("OriginalPublisherProviderProofMissingOrChangedNoRecreation");
                observation = await ObservePublisherAsync(capability, token);
                return Result(FactoryPublisherState.PublicationPreparedAwaitingPromotion,
                    "Original isolated publisher, context, journal container and exact grants confirmed. Rollout status alone does not establish activation or authorize publication.");
            }
            if (selection is null) throw Failure("ExplicitPublicationRolloutSelectionRequired");
            FactoryLive.Validate(selection);
            var approved = await new FactoryActivationService(reviewer, azure, http, clock).ExecuteAsync(admission, null, null, token);
            if (!approved.ApprovalCurrent || approved.Review is null) throw Failure("CurrentExplicitOperatorApprovalRequired");
            var live = await LiveAsync(admission, null, null, token);
            if (live.State != FactoryLiveState.PreparedAwaitingPublication || live.Plan is null) throw Failure("OriginalNativeLiveRolloutRequired");
            var authorityRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryGitHubAuthority, token) ?? throw Failure("OriginalAuthModuleRequired");
            var authority = Parse<FactoryGitHubAuthority>(authorityRecord.Value);
            FactoryPublisher.RequirePermissions(authority);
            var original = await ReadJournalAsync(admission, store, token) ?? throw Failure("OriginalNativePreviewRequired");
            var native = original.Plan;
            var manifest = native.Manifest;
            var auth = admission.Foundation!.Operations.Single(o => o.Target == authority.AuthIdentityId);
            var bindings = native.Bindings.ToDictionary();
            bindings.Add("auth", auth);
            native = native with { Bindings = bindings };
            var arm = new FactoryPreparationArm(azure, http, manifest.Identity);
            var prerequisites = new Dictionary<string, PreviewPreparationNative>();
            foreach (var binding in new[] { "auth", "council", "hosting", "account" })
                await ObserveBindingAsync(arm, bindings[binding], manifest, prerequisites, token);
            var role = await MetadataRoleAsync(arm, selection.MetadataRoleDefinitionId, FactoryPreviewValidation.MetadataActions, prerequisites, token);
            if (!selection.MetadataRoleDefinitionId.StartsWith("/subscriptions/" + manifest.Identity.SubscriptionId + "/", StringComparison.Ordinal))
                throw Failure("ExactReviewedSubscriptionMetadataRoleRequired");
            var group = FactoryPublisher.Group(native);
            var permission = new PreviewPreparationPermission(group, ["Microsoft.Resources/deployments/write", "Microsoft.App/containerApps/write",
                "Microsoft.App/containerApps/authConfigs/write", "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/write",
                "Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions/write", "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments/write"]);
            await OperatorPermissionAsync(arm, permission, token);
            var account = native.Bindings["account"].Target;
            var scope = FactoryPreviewValidation.SqlScope(account, manifest.Identity.ProductKey, FactoryPublication.Container);
            var template = File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-publisher.json"));
            using var parsed = JsonDocument.Parse(template);
            var parameters = new Dictionary<string, object>
            {
                ["namePrefix"] = FactoryPreviewValidation.Prefix(manifest.Identity.FactoryId), ["location"] = admission.Plan!.Selection.FactoryLocation,
                ["productKey"] = manifest.Identity.ProductKey, ["productEndpoint"] = admission.Foundation.ProductEndpoint!,
                ["profilePin"] = manifest.ProfilePin, ["tenantId"] = manifest.Identity.TenantId, ["operatorPrincipalId"] = manifest.Identity.AzureActorId,
                ["runtimeImage"] = selection.RuntimeImage, ["audienceClientId"] = selection.AudienceClientId,
                ["writerRoleId"] = FactoryPublisher.Role(native, true).Split('/')[^1],
                ["readerRoleId"] = FactoryPublisher.Role(native, false).Split('/')[^1],
                ["writerAssignmentId"] = FactoryPreviewValidation.SqlAssignment(account, auth.PrincipalId!, scope, FactoryPublisher.Role(native, true)).Split('/')[^1],
                ["readerAssignmentId"] = FactoryPreviewValidation.SqlAssignment(account, bindings["council"].PrincipalId!, scope, FactoryPublisher.Role(native, false)).Split('/')[^1],
            };
            var body = Serialize(new { properties = new { mode = "Incremental", template = parsed.RootElement,
                parameters = parameters.ToDictionary(p => p.Key, p => new { value = p.Value }) } });
            var target = group + "/providers/microsoft.resources/deployments/publisher-" + selection.OperationId.ToString("N");
            var steps = new List<PreviewPreparationStep> { new(0, PreviewPreparationAction.WorkloadDeployment, target, "2025-04-01",
                FactoryAudit.TextHash(body), FactoryPublisher.ResourceTargets(native), null) };
            foreach (var (grantScope, principal) in FactoryPublisher.MetadataScopes(native, selection.OperationId))
            {
                var grant = Grant(grantScope, selection.MetadataRoleDefinitionId, principal, role, FactoryPreviewValidation.MetadataActions);
                await GrantPermissionAsync(arm, grant, token);
                steps.Add(new(steps.Count, PreviewPreparationAction.MetadataAssignment, grant.Target, "2022-04-01",
                    FactoryAudit.TextHash(FactoryPreviewPreparation.GrantBody(grant)), [grant.Target], grant));
            }
            var approvalRecord = await store.ReadAsync(FactoryActivation.ApprovalKey(approved.Review.OperationId), token) ?? throw new JsonException();
            var liveRecord = await store.ReadAsync(FactoryLive.RootKey, token) ?? throw new JsonException();
            plan = new(1, selection, approved.Review, Identity(approvalRecord), Identity(liveRecord), Identity(authorityRecord), authority,
                admission.Plan.RepositoryReview.Automation.Single(a => a.Mutation == RepositoryMutationClass.ProposalIssue),
                native with { WorkloadBody = body, WorkloadTemplateSha256 = FactoryReservationRecords.Hash(template), Steps = steps,
                    Prerequisites = prerequisites.Values.ToArray(), OperatorPermissions = [permission,
                        .. steps.Skip(1).Select(s => new PreviewPreparationPermission(s.Grant!.Scope, ["Microsoft.Authorization/roleAssignments/write"]))],
                    CostsAndPrerequisites = ["Two new pinned private hosts: original AUTH publisher and original COUNCIL context, one 0.25 CPU / 0.5Gi replica each.",
                        "One non-expiring proposal-publications container. AUTH metadata/read/create/replace; COUNCIL metadata/read only. No query/delete/upsert.",
                        "Existing exact AUTH identity, original versioned key and CODE read-only token unchanged. No governance writer.",
                        "GitHub issues:write is broader than create-only; the isolated module constrains calls. Existing App-key-holder minting residual remains.",
                        "App and installation require metadata/contents/actions/repository_hooks read and issues write BEFORE initial authority approval.",
                        "Operator approval, native rollout and current effective provider checks are prerequisites, not final promotion or filing."] });
            foreach (var step in steps)
            {
                await AbsentAsync(arm, step.Target, step.ApiVersion, token);
                foreach (var resource in step.ResourceTargets) await AbsentAsync(arm, resource, TargetApi(resource), token);
            }
            CheckCapacity(plan.Deployment, publisher: plan);
            var bytes = Encoding.UTF8.GetBytes(Serialize(plan));
            coordinator = new(1, manifest.Identity.FactoryId, selection.OperationId, plan.Fingerprint, FactoryReservationRecords.Hash(bytes),
                bytes.Length, (bytes.Length + 4095) / 4096, manifest.Identity.AzureActorId, clock.GetUtcNow(), FactoryPublisherState.Preparing, 0);
            OwnerReservationStore.CheckSize(FactoryPublisher.RootKey, "", Serialize(coordinator with { ApprovedAt = DateTimeOffset.MaxValue,
                State = FactoryPublisherState.PublicationPreparedAwaitingPromotion, ConfirmedEffects = steps.Count,
                NativeProviderProof = new(manifest.Identity.FactoryId, AuditStoreKind.Governance, FactoryPublisher.ObservationKey(selection.OperationId),
                    new string('v', 256), new string('f', 64)) }), ContentType);
            if (approval is null) return Result(FactoryPublisherState.Reviewed, "Review complete native publication resources, original authorities and explicit scoped grants. No writes.");
            if (approval != plan.Fingerprint) throw Failure("ExactCurrentPublicationRolloutApprovalRequired");
            await CurrentAsync(readiness: true);
            root = await WriteAsync(store, FactoryPublisher.RootKey, coordinator, null, token);
            for (var i = 0; i < coordinator.PlanChunks; i++)
                await WriteAsync(store, FactoryPublisher.ChunkKey(coordinator.OperationId, i),
                    Convert.ToBase64String(bytes.AsSpan(i * 4096, Math.Min(4096, bytes.Length - i * 4096))), null, token);
            var effects = new List<PreviewPreparationEffect>();
            var effectRecords = new List<ConfigurationSetting>();
            foreach (var step in steps)
            {
                await CurrentAsync(readiness: true);
                if ((await store.ReadAsync(FactoryPublisher.RootKey, token))?.ETag != root.ETag) throw Unknown("PublicationCoordinatorOwnershipChanged");
                await AbsentAsync(arm, step.Target, step.ApiVersion, token);
                foreach (var resource in step.ResourceTargets) await AbsentAsync(arm, resource, TargetApi(resource), token);
                if (step.Grant is not null) await GrantPermissionAsync(arm, step.Grant, token);
                var key = FactoryPublisher.EffectKey(coordinator.OperationId, step.Index);
                var effect = new PreviewPreparationEffect(1, coordinator.FactoryId, coordinator.OperationId, coordinator.Fingerprint, step.Index,
                    step.Target, step.BodySha256, Guid.NewGuid(), clock.GetUtcNow(), PreviewPreparationEffectState.Intended, [], null);
                var effectRecord = await WriteAsync(store, key, effect, null, token);
                effect = effect with { State = PreviewPreparationEffectState.Dispatched };
                effectRecord = await WriteAsync(store, key, effect, effectRecord, token);
                pendingEffect = effect;
                pendingRecord = effectRecord;
                await CurrentAsync(readiness: true);
                issued = true;
                var created = await DispatchAsync(plan.Deployment, step, effect, arm, async ack =>
                {
                    effect = effect with { Acknowledgment = ack };
                    effectRecord = await WriteAsync(store, key, effect, effectRecord, token);
                    pendingEffect = effect;
                    pendingRecord = effectRecord;
                }, token, Validate);
                for (var i = 0; i < created.Receipts.Count; i++)
                    await WriteAsync(store, FactoryPublisher.ReceiptKey(coordinator.OperationId, step.Index, i), created.Receipts[i], null, token);
                receipts.AddRange(created.Receipts);
                effect = effect with { State = PreviewPreparationEffectState.Confirmed, ReceiptHashes = created.Receipts.Select(FactoryModelsValidation.Hash).ToArray(),
                    CouncilEndpoint = created.CouncilEndpoint, DeploymentOutputsSha256 = created.OutputsSha256 };
                effectRecords.Add(await WriteAsync(store, key, effect, effectRecord, token));
                effects.Add(effect);
                coordinator = coordinator with { ConfirmedEffects = step.Index + 1 };
                root = await WriteAsync(store, FactoryPublisher.RootKey, coordinator, root, token);
                issued = false;
                pendingEffect = null;
                pendingRecord = null;
            }
            await CurrentAsync();
            await VerifyConfirmedAsync(Journal(effects, effectRecords), token, Validate);
            coordinator = coordinator with { State = FactoryPublisherState.PublicationPreparedAwaitingPromotion };
            root = await WriteAsync(store, FactoryPublisher.RootKey, coordinator, root, token);
            await CurrentAsync();
            _ = FactoryPublisher.ReadConfirmed(Values(await store.ReadNamespaceAsync(FactoryPublisher.Prefix, token, FactoryPublisher.MaximumRecords)), clock.GetUtcNow());
            observation = await ObservePublisherAsync(new(plan, coordinator, effects, receipts), token);
            var observedRecord = await WriteAsync(store, FactoryPublisher.ObservationKey(coordinator.OperationId), observation, null, token);
            coordinator = coordinator with { NativeProviderProof = Identity(observedRecord) };
            root = await WriteAsync(store, FactoryPublisher.RootKey, coordinator, root, token);
            await CurrentAsync();
            return Result(FactoryPublisherState.PublicationPreparedAwaitingPromotion,
                "Original isolated publisher, context, non-expiring journal and exact AUTH/COUNCIL grants provisioned. No activation or issue dispatch.");

            AuditRecordIdentity Identity(ConfigurationSetting record) => new(manifest.Identity.FactoryId, AuditStoreKind.Governance,
                record.Key, record.ETag.ToString(), FactoryAudit.TextHash(record.Value));
            void Validate(IReadOnlyDictionary<string, JsonElement> resources, string endpoint) => FactoryPublisher.ValidateResources(plan!, resources, endpoint);
            async Task CurrentAsync(bool readiness = false, bool allowPromotion = false)
            {
                FactoryPause.RequireAdmissionOpen((await store.ReadNamespaceAsync(FactoryPause.Key, token)).Keys);
                FactoryPause.RequireAdmissionOpen((await store.ReadNamespaceAsync(FactoryPause.HistoryPrefix, token, maximumRecords: 128)).Keys);
                FactoryPause.RequireAdmissionOpen((await store.ReadNamespaceAsync(FactoryReconfigurationOperationRecords.Prefix, token,
                    FactoryReconfigurationOperationRecords.MaximumRecords)).Keys);
                var currentAuthority = await store.ReadFactoryAuthorityAsync(token);
                var revision = FactoryModelsValidation.Hash(currentAuthority.Values.OrderBy(r => r.Key, StringComparer.Ordinal)
                    .Select(r => new AuditRecordIdentity(plan!.Deployment.Manifest.Identity.FactoryId, AuditStoreKind.Governance,
                        r.Key, r.ETag.ToString(), FactoryAudit.TextHash(r.Value))).ToArray());
                if (revision != plan!.Activation.AuthorityRevision) throw Failure("ReviewedPublicationConfigurationChanged");
                if (readiness)
                    await RequireCurrentReviewedActivationAsync(admission, store, plan.Activation, allowPromotion, token);
                foreach (var reference in new[] { plan.Approval, plan.Live, plan.GitHub })
                {
                    var record = await store.ReadAsync(reference.Key, token);
                    if (record is null || record.ETag.ToString() != reference.NativeRevision || FactoryAudit.TextHash(record.Value) != reference.Sha256)
                        throw Unknown("OriginalPublicationAuthorityReferenceChanged");
                }
                foreach (var prerequisite in plan.Deployment.Prerequisites)
                    if (FactoryModelsValidation.Hash(await ReadNativeAsync(new FactoryPreparationArm(azure, http, plan.Deployment.Manifest.Identity),
                        prerequisite.Target, prerequisite.ApiVersion, token)) != prerequisite.Revision) throw Failure("OriginalPublicationNativePrerequisiteChanged");
            }
            Journal Journal(IReadOnlyList<PreviewPreparationEffect> effects, IReadOnlyList<ConfigurationSetting> effectRecords) =>
                new(plan!.Deployment, new(1, FactoryPreviewPreparation.Kind, coordinator!.FactoryId, coordinator.OperationId, plan.Fingerprint,
                    coordinator.PlanSha256, coordinator.PlanBytes, coordinator.PlanChunks, coordinator.ApprovedBy, coordinator.ApprovedAt,
                    Guid.Empty, PreviewPreparationState.ConfirmedNotReady, coordinator.ConfirmedEffects, null), root!, effects, effectRecords, receipts);
        }
        catch (Exception e) when (e is ReservationStoreException or PreviewCapabilityException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or FormatException or HttpRequestException or IOException or OperationCanceledException
            or NativePreparationRejectedException or OwnerReservationStore.AuthenticationFailedException)
        {
            var state = e is NativePreparationRejectedException ? FactoryPublisherState.Failed
                : issued || e is ReservationStoreException { State: FactoryReservationState.Unknown } ? FactoryPublisherState.Unknown
                : e is OperationCanceledException ? FactoryPublisherState.Canceled : root is not null ? FactoryPublisherState.Unknown : FactoryPublisherState.Blocked;
            if (root is not null && coordinator is not null && store is not null && approval is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                try
                {
                    if (pendingRecord is not null && pendingEffect is not null)
                        await WriteAsync(store, pendingRecord.Key, pendingEffect with
                        { State = e is NativePreparationRejectedException ? PreviewPreparationEffectState.KnownFailed : PreviewPreparationEffectState.Unknown,
                            Reason = e is NativePreparationRejectedException ? "NativeRequestRejectedWithoutEffect" : "OriginalNativeEffectUnconfirmedNoReplay" },
                            pendingRecord, cleanup.Token);
                    coordinator = coordinator with { State = state };
                    root = await WriteAsync(store, FactoryPublisher.RootKey, coordinator, root, cleanup.Token);
                }
                catch (Exception failure) when (failure is ReservationStoreException or OperationCanceledException or HttpRequestException
                    or IOException or JsonException or InvalidOperationException or OwnerReservationStore.AuthenticationFailedException)
                { state = FactoryPublisherState.Unknown; }
            }
            return Result(state, e is JsonException && e.Message.StartsWith("GitHubAppAdministratorPrerequisite", StringComparison.Ordinal) ? e.Message
                : "Publication authority, native request or original receipt unconfirmed. No replay, adoption, reset or activation.");
        }
        FactoryPublisherResult Result(FactoryPublisherState state, string detail) => new(state, detail)
            { Plan = plan, Coordinator = coordinator, Receipts = receipts, Observation = observation };
        static Dictionary<string, string> Values(IReadOnlyDictionary<string, ConfigurationSetting> records) => records.ToDictionary(p => p.Key, p => p.Value.Value);
    }
}
