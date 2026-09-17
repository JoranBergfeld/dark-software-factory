using System.Net;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService(
    RepositoryAttachmentReviewer reviewer, IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<PreviewPreparationPlan> PlanAsync(FactoryReservationResult admission,
        PreviewPreparationSelection selection, CancellationToken token)
    {
        FactoryPreviewPreparation.Validate(selection);
        var store = Store(admission);
        if (await store.ReadAsync(FactoryPreviewPreparation.RootKey, token) is not null)
            throw Failure("PreviewPreparationAlreadyHeld: inspect the original operation; no replacement plan.");
        await store.RequirePristineAsync(FactoryPreviewPreparation.Namespace, token);
        var plan = await ReadPlanAsync(admission, selection, store, token);
        var arm = new FactoryPreparationArm(azure, http, plan.Manifest.Identity);
        foreach (var step in plan.Steps)
        {
            await AbsentAsync(arm, step.Target, step.ApiVersion, token);
            if (step.Action == PreviewPreparationAction.WorkloadDeployment)
                foreach (var target in step.ResourceTargets)
                    await AbsentAsync(arm, target, TargetApi(target), token);
        }
        CheckCapacity(plan);
        await RequireCurrentAsync(admission, plan, store, token);
        return plan;
    }

    private async Task<PreviewPreparationPlan> ReadPlanAsync(FactoryReservationResult admission, PreviewPreparationSelection selection,
        OwnerReservationStore store, CancellationToken token, PreviewPreparationPlan? continuation = null,
        Journal? approvedPreparation = null)
    {
        var records = await store.ReadNamespaceAsync("dsf/factory/", token);
        var values = records.Where(p => !p.Key.StartsWith(FactoryPreviewPreparation.Namespace + "/", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal);
        foreach (var key in new[] { ProductConfigurationKeys.FactoryManifest, ProductConfigurationKeys.FactoryProfile,
            ProductConfigurationKeys.FactoryGitHubAuthority, ProductConfigurationKeys.FactorySource, ProductConfigurationKeys.FactoryModels,
            ProductConfigurationKeys.FactoryTelemetry })
            if (!values.ContainsKey(key)) throw Failure("RequiredPreviewPredecessorMissing:" + key);
        var manifest = FactoryGovernance.ReadInactiveManifest(values[ProductConfigurationKeys.FactoryManifest],
            values[ProductConfigurationKeys.FactoryProfile], admission.Identity!.ProductKey, admission.Foundation!.ProductEndpoint!, admission.Foundation.ProfilePin);
        if (manifest.Identity != admission.Identity || (continuation is null
            ? manifest.PreviewOperationId is not null || manifest.PreviewApprovalFingerprint is not null
                || values.Keys.Any(k => k.StartsWith("dsf/factory/preview", StringComparison.Ordinal))
            : manifest.PreviewOperationId is not null && manifest.PreviewOperationId != continuation.Selection.OperationId
                || FactoryPreviewValidation.ManifestBasis(manifest) != FactoryPreviewValidation.ManifestBasis(continuation.Manifest)))
            throw Failure("CurrentInactiveUnpublishedPreviewAuthorityRequired");
        if (continuation is not null)
        {
            manifest = continuation.Manifest;
            foreach (var key in values.Keys.Where(k => k.StartsWith("dsf/factory/preview", StringComparison.Ordinal)).ToArray()) values.Remove(key);
            values[ProductConfigurationKeys.FactoryManifest] = Serialize(manifest);
        }
        RequireSupplementalNamespace(manifest, selection, values);
        var rolePrefix = "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/";
        if (new[] { selection.MetadataRoleDefinitionId, selection.Telemetry.MetadataRoleDefinitionId }
            .Concat(selection.Supplemental.Select(s => s.MetadataRoleDefinitionId)).Any(role => !role.StartsWith(rolePrefix, StringComparison.Ordinal)))
            throw Failure("PreviewMetadataRolesMustBelongToTheReviewedSubscription");
        var github = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
        _ = FactoryGitHubAuthorityValidation.ReadCompletedEffects(github, values);
        FactoryGitHubAuthorityValidation.ValidateVerified(github);
        var (source, sourceEffects) = FactorySourceValidation.Read(manifest, github, values);
        var models = FactoryModelsValidation.ReadPreparation(manifest, values);
        var proofTime = ObservationTime(ProductConfigurationKeys.FactoryModels);
        if (models.State != ModelCapabilityState.Verified || models.Stage != "observed" || models.VerifiedUntil is null || models.VerifiedUntil <= proofTime)
            throw Failure("CurrentVerifiedModelCapabilitiesRequired");
        models = FactoryModelsValidation.ReadVerified(manifest, values,
            new(models.OperationId, models.ApprovalFingerprint, models.LastProofReference ?? "", models.LastProofSha256 ?? ""), proofTime);
        var telemetry = FactoryTelemetryValidation.ReadPreparation(manifest, values, requireChecking: false);
        var telemetryEffects = FactoryTelemetryValidation.ReadCompletedEffects(telemetry, values);
        var telemetryProof = FactoryTelemetryValidation.RequireVerifiedHistory(telemetry, values);
        if (!FactoryTelemetryValidation.HasFreshEvidence(telemetry with { Proof = telemetryProof }, ObservationTime(ProductConfigurationKeys.FactoryTelemetry)))
            throw Failure("CurrentFreshTelemetryProofRequired");
        var retained = await FactoryCharterAdoptionReader.ReadAsync(manifest.Identity.FactoryId, store, token)
            ?? throw Failure("CurrentAdoptedCharterRequired");
        var evidence = await new FactorySourceService(azure, http, clock).ReviewCharterAsync(admission, token);
        FactoryCharterAdoptionReader.RequireCurrent(retained.Adoption, evidence, manifest.Identity.ProductKey);
        var repositoryRevision = await RepositoryRevisionAsync(admission, token);
        var arm = new FactoryPreparationArm(azure, http, manifest.Identity);
        var native = new Dictionary<string, PreviewPreparationNative>(StringComparer.OrdinalIgnoreCase);
        var bindings = new Dictionary<string, FoundationOperation>(StringComparer.Ordinal)
            { ["council"] = models.Bindings![0], ["code"] = models.Bindings[1], ["hosting"] = models.Bindings[2] };
        var group = FactoryPreviewValidation.Group(models);
        var account = FactoryPreviewValidation.Account(group, manifest.Identity.FactoryId);
        foreach (var (name, target) in new[] { ("account", account), ("database", FactoryPreviewValidation.Database(account, manifest.Identity.ProductKey)),
            ("runs", FactoryPreviewValidation.Container(account, manifest.Identity.ProductKey, "runs")) })
            bindings.Add(name, admission.Foundation.Operations.SingleOrDefault(o => o.Target == target)
                ?? throw Failure("OriginalFactoryStateReceiptRequired:" + target));
        foreach (var binding in bindings.Values.Append(telemetry.Configuration.SourceIdentity))
            await ObserveBindingAsync(arm, binding, manifest, native, token);
        foreach (var route in models.Routes!)
        {
            var value = await CaptureAsync(arm, route.Selection.AccountResourceId, FactoryModelsValidation.ProviderApi, native, token);
            if (FactoryModelsValidation.ObserveAccount(route.Selection, value) != route.AccountObservation) throw Failure("ModelAccountChanged");
            value = await CaptureAsync(arm, FactoryModelsValidation.DeploymentTarget(route.Selection), FactoryModelsValidation.ProviderApi, native, token);
            var observed = FactoryModelsValidation.ObserveDeployment(route.Selection, value);
            var created = models.Effects!.SingleOrDefault(e => e.Target == FactoryModelsValidation.DeploymentTarget(route.Selection));
            if (route.Selection.Create ? created is null || observed.Incarnation != created.Incarnation || observed.Revision != created.NativeRevision
                : observed != route.DeploymentObservation) throw Failure("ModelDeploymentChanged");
        }
        foreach (var grant in models.Grants!)
        {
            var role = await CaptureAsync(arm, grant.RoleDefinitionId, "2022-04-01", native, token);
            if (FactoryModelsValidation.ObserveRole(grant.RoleDefinitionId, role) != grant.RoleRevision) throw Failure("ModelRoleChanged");
            var receipt = models.Effects!.Single(e => e.Target == grant.Target);
            RequireGrant(await CaptureAsync(arm, grant.Target, "2022-04-01", native, token),
                new(grant.Target, grant.PrincipalId, grant.RoleDefinitionId, grant.Scope, grant.RoleRevision), manifest.Identity.AzureActorId, receipt.Incarnation);
        }
        var councilRole = await MetadataRoleAsync(arm, selection.MetadataRoleDefinitionId, FactoryPreviewValidation.MetadataActions, native, token);
        var telemetryRole = await MetadataRoleAsync(arm, selection.Telemetry.MetadataRoleDefinitionId,
            FactoryPreviewTelemetryValidation.MetadataActions, native, token);
        var accessRoles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var grant in telemetry.Configuration.AccessGrants)
        {
            var role = await CaptureAsync(arm, grant.RoleDefinitionId, "2022-04-01", native, token);
            var revision = FactoryModelsValidation.Hash(role.GetProperty("properties"));
            FactoryPreviewTelemetryValidation.RequireRole(role, grant.RoleDefinitionId, revision);
            accessRoles[grant.RoleDefinitionId] = revision;
            FactoryPreviewTelemetryValidation.RequireGrant(await CaptureAsync(arm, grant.Target, "2022-04-01", native, token), grant,
                telemetryEffects.Single(e => e.Target == grant.Target), manifest.Identity.AzureActorId);
        }
        foreach (var backend in telemetry.Configuration.Backends)
        {
            var workspace = await CaptureAsync(arm, backend.ResourceId, FactoryTelemetryValidation.BackendApiVersion, native, token);
            if (!FactoryTelemetryValidation.HasSupportedWorkspaceAuthorization(workspace)
                || workspace.GetProperty("properties").GetProperty("customerId").GetString() != backend.WorkspaceId)
                throw Failure("CurrentReviewedTelemetryBackendRequired");
            foreach (var table in backend.Tables)
                if (!FactoryTelemetryValidation.HasSupportedTableAuthorization(await CaptureAsync(arm,
                    backend.ResourceId + "/tables/" + table, FactoryTelemetryValidation.BackendApiVersion, native, token)))
                    throw Failure("SupportedTelemetryTableRequired:" + table);
            foreach (var app in backend.ApplicationResourceIds)
            {
                var component = await CaptureAsync(arm, app, "2020-02-02", native, token);
                if (!Same(component.GetProperty("properties").GetProperty("WorkspaceResourceId").GetString(), backend.ResourceId))
                    throw Failure("CurrentApplicationTelemetryMappingChanged");
            }
        }
        var projection = new PreviewTelemetrySelection(FactoryPreviewTelemetryValidation.Purpose, telemetry.ApprovalFingerprint,
            FactoryModelsValidation.Hash(telemetryEffects), telemetry.Configuration.SourceIdentity,
            telemetry.Configuration.Backends.SelectMany(b => b.ApplicationResourceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            selection.Telemetry.RuntimeImage, selection.Telemetry.AudienceClientId, FactoryPreviewTelemetryValidation.Projection,
            selection.Telemetry.RowsPerTable, telemetry.Configuration.Queries.Keys.ToDictionary(t => t,
                t => FactoryPreviewTelemetryValidation.Query(t, selection.Telemetry.RowsPerTable)),
            selection.Telemetry.MetadataRoleDefinitionId, telemetryRole, accessRoles);
        var configuration = new FactoryPreviewConfiguration(1, FactoryPreviewValidation.Purpose, manifest.Identity.FactoryId, manifest.ProfilePin,
            manifest.Identity.PlanFingerprint, FactoryPreviewValidation.ManifestBasis(manifest), source.ApprovalFingerprint,
            FactoryModelsValidation.Hash(sourceEffects), models.ApprovalFingerprint, FactoryModelsValidation.Hash(models.Effects),
            retained.Adoption.Revision, selection.RuntimeImage, selection.AudienceClientId, selection.MetadataRoleDefinitionId,
            councilRole, bindings.ToDictionary(p => p.Key, p => FactoryModelsValidation.Hash(p.Value)))
        {
            Telemetry = projection, ModelEvaluation = new(PreviewModelHostConfiguration.PurposeName, selection.Models.RuntimeImage, selection.Models.AudienceClientId),
            Inputs = new(FactoryPreviewInputValidation.Policy, selection.Judgment),
        };
        _ = FactoryPreviewTelemetryValidation.Read(manifest, configuration, values);
        var (supplemental, supplementalEffects, optional) = await SupplementalAsync(arm, manifest, selection, values, native,
            approvedPreparation is not null && OriginalObservation(ProductConfigurationKeys.FactorySupplemental)
                ? approvedPreparation.Coordinator.ApprovedAt : null, token);
        configuration = configuration with { Supplemental = optional, SupplementalRevision = optional is null ? null : FactoryModelsValidation.Hash(optional) };
        _ = FactoryPreviewSupplementalValidation.Read(manifest, configuration, values);
        foreach (var (target, image, verb, identity, audience, callers) in new[]
        {
            (FactorySourceValidation.HostTarget(github), source.Configuration.Selection.RuntimeImage, "serve-source", bindings["code"],
                source.Configuration.Selection.AudienceClientId, new[] { manifest.Identity.AzureActorId, bindings["code"].PrincipalId! }),
            (FactoryModelsValidation.HostTarget(models), models.Configuration.RuntimeImage, "serve-models", bindings["council"],
                models.Configuration.AudienceClientId, new[] { manifest.Identity.AzureActorId, bindings["code"].PrincipalId!, bindings["council"].PrincipalId! }),
            (FactoryTelemetryValidation.HostTarget(telemetry.Configuration), telemetry.Configuration.RuntimeImage, "serve-telemetry", telemetry.Configuration.SourceIdentity,
                telemetry.Configuration.AudienceClientId, new[] { manifest.Identity.AzureActorId }),
        })
            _ = FactoryHostedWorkloadPolicy.Validate(await CaptureAsync(arm, target, "2025-01-01", native, token),
                await CaptureAsync(arm, target + "/authconfigs/current", "2025-01-01", native, token), identity, bindings["hosting"],
                image, verb, manifest.Identity.ProductKey, admission.Foundation.ProductEndpoint!, manifest.ProfilePin, manifest.Identity.TenantId, audience, callers);
        var grants = FactoryPreviewValidation.MetadataScopes(manifest, models, github, selection.OperationId, configuration)
            .Select(scope => Grant(scope, selection.MetadataRoleDefinitionId, bindings["council"].PrincipalId!, councilRole, FactoryPreviewValidation.MetadataActions))
            .Concat(FactoryPreviewTelemetryValidation.MetadataScopes(manifest, models, selection.OperationId, projection)
                .Select(scope => Grant(scope, projection.MetadataRoleDefinitionId, projection.SourceIdentity.PrincipalId!, telemetryRole, FactoryPreviewTelemetryValidation.MetadataActions)))
            .Concat(optional?.Hosts.Select(h => Grant(h.MetadataScope, h.MetadataRoleDefinitionId, h.PrincipalId, h.MetadataRoleRevision,
                FactoryPreviewSupplementalValidation.MetadataActions(h.Kind))) ?? []).ToArray();
        foreach (var grant in grants) await GrantPermissionAsync(arm, grant, token);
        var operatorPermissions = new[]
        {
            new PreviewPreparationPermission(group, ["Microsoft.Resources/deployments/write", "Microsoft.App/containerApps/write", "Microsoft.App/containerApps/authConfigs/write"]),
            new PreviewPreparationPermission(account, ["Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions/write",
                "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments/write", "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/write"]),
        }.Concat(grants.Select(g => new PreviewPreparationPermission(g.Scope, ["Microsoft.Authorization/roleAssignments/write"]))).ToArray();
        foreach (var permission in operatorPermissions.Take(2)) await OperatorPermissionAsync(arm, permission, token);
        var templateBytes = File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-preview-workloads.json"));
        var body = WorkloadBody(admission, manifest, selection, configuration, bindings, supplemental, supplementalEffects, templateBytes);
        var deployment = FactoryPreviewValidation.Deployment(group, selection.OperationId);
        var workloadTargets = FactoryPreviewValidation.Targets(manifest, models, github, selection.OperationId, selection.MetadataRoleDefinitionId, configuration)
            .Where(t => !t.Target.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal)).Select(t => t.Target).ToArray();
        var steps = new[] { new PreviewPreparationStep(0, PreviewPreparationAction.WorkloadDeployment, deployment, "2025-04-01",
            FactoryReservationRecords.Hash(body), workloadTargets, null) }.Concat(grants.Select((g, i) =>
                new PreviewPreparationStep(i + 1, PreviewPreparationAction.MetadataAssignment, g.Target, "2022-04-01",
                    FactoryReservationRecords.Hash(FactoryPreviewPreparation.GrantBody(g)), [g.Target], g))).ToArray();
        var progress = evidence.Analysis.Progress!;
        var hostBindings = new Dictionary<string, FoundationOperation>(bindings) { ["telemetry"] = telemetry.Configuration.SourceIdentity };
        foreach (var selected in selection.Supplemental)
            hostBindings.Add(selected.Kind, supplementalEffects.Single(e =>
                e.Target == FactorySupplementalValidation.IdentityTarget(supplemental!.Configuration!, selected.Kind)));
        return new(1, FactoryPreviewPreparation.Policy, selection, manifest, configuration, optional, hostBindings,
            new(retained.Adoption.Revision, retained.Revision, evidence.Source.Content!, evidence.Source.Snapshot.Id,
                evidence.Source.Snapshot.Commit, evidence.Source.Snapshot.RootTree, progress.Revision, progress.ChainSha256!,
                progress.SourceIntegritySha256, progress.ModelConfigurationRevision, evidence.Analysis.Limitations),
            repositoryRevision, values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => FactoryReservationRecords.Hash(p.Value)),
            native.Values.OrderBy(n => n.Target, StringComparer.Ordinal).ToArray(), steps, FactoryReservationRecords.Hash(templateBytes), body,
            ["Creates four to six new always-on 0.25 CPU/0.5 GiB hosts; existing images and shared/private infrastructure are not upgraded.",
                "Additional Cosmos state and ARM metadata grants incur residual costs. Only runs and council-preview item read/create/replace; no query/delete.",
                "Existing reviewed metadata role definitions, Entra API registrations/consent, registry access and private operator/workload reachability are prerequisites.",
                "Native preparation only: no capability or manifest publication, source repair, issue/PR/label mutation, activation or readiness."])
        { OperatorPermissions = operatorPermissions };

        bool OriginalObservation(string key) => approvedPreparation is not null
            && approvedPreparation.Plan.RecordRevisions.TryGetValue(key, out var reviewed)
            && values.TryGetValue(key, out var current) && FactoryReservationRecords.Hash(current) == reviewed;
        DateTimeOffset ObservationTime(string key) => OriginalObservation(key)
            ? approvedPreparation!.Coordinator.ApprovedAt : clock.GetUtcNow();
    }

    private OwnerReservationStore Store(FactoryReservationResult admission)
    {
        if (admission.State != FactoryReservationState.Reserved || admission.Identity is null || admission.Plan is null
            || admission.Foundation is not { State: FactoryFoundationState.Inactive, ProductEndpoint: not null })
            throw new ReservationStoreException(admission.State is FactoryReservationState.Unknown or FactoryReservationState.Canceled
                ? admission.State : FactoryReservationState.Blocked, "ProvenInactiveOwnerRegistryAndFoundationRequired");
        return new(admission.Foundation.ProductEndpoint, admission.Identity.TenantId, admission.Identity.SubscriptionId,
            azure, http, admission.Identity.AzureActorId);
    }

    private async Task<string> RepositoryRevisionAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var selection = admission.Plan!.Selection;
        var review = await reviewer.ReviewAsync(new(selection.Repository, selection.OwnerAppConfigEndpoint, selection.OwnerKeyVaultUri)
        { DurableRepositoryId = admission.RepositoryId, AutomationDeclarations = selection.AutomationDeclarations, AutomationOwner = selection.AutomationOwner }, token);
        if (review.Repository?.Id != admission.Identity!.RepositoryId || review.Operator?.Id != admission.Identity.GitHubActorId
            || review.Findings.Any(f => f.State != RepositoryReviewState.Confirmed)
            || review.Automation.SingleOrDefault(a => a.Mutation == RepositoryMutationClass.ProposalIssue)?.State != RepositoryReviewState.Confirmed)
            throw Failure("CurrentRepositoryOperatorConsentAndProposalCompatibilityRequired");
        return FactoryModelsValidation.Hash(new { review.Repository, review.Operator, review.App, review.Installation, review.Labels, review.Automation });
    }

    private static PreviewPreparationGrant Grant(string scope, string role, string principal, string revision, IReadOnlyList<string> actions) =>
        new(FactoryModelsValidation.Grant(scope, role, principal, revision).Target, principal, role, scope, revision) { Actions = actions };
    private static ReservationStoreException Failure(string reason) => new(FactoryReservationState.Blocked, reason);
    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static string TargetApi(string target) => target.Contains("/microsoft.authorization/", StringComparison.Ordinal) ? "2022-04-01"
        : target.Contains("/microsoft.documentdb/", StringComparison.Ordinal) ? "2024-11-15" : "2025-01-01";
}
