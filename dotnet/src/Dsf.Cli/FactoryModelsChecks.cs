using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryModelsService
{
    private static async Task<(ConfigurationSetting Record, FactoryManifest Manifest)> ReadGovernanceAsync(
        FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var record = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token);
        var profile = await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token);
        var identity = admission.Identity!;
        var manifest = FactoryGovernance.ReadInactiveManifest(record?.Value, profile?.Value, identity.ProductKey,
            admission.Foundation!.ProductEndpoint!, admission.Foundation.ProfilePin);
        if (manifest.Identity != identity || admission.Foundation.State != FactoryFoundationState.Inactive
            || manifest.ReviewedPlanSha256 != FactoryReservationRecords.Hash(BoundaryPlanFingerprint.Canonical(
                JsonSerializer.SerializeToElement(admission.Plan ?? throw new JsonException(), FactoryReservationRecords.Json))))
            throw FactoryModelsValidation.Failure(ModelCapabilityReason.AuthorityChanged, "governance", "Current product manifest/profile differs from admitted factory, plan or pin; no model effects authorized.");
        return (record!, manifest);
    }

    private async Task EnsureCurrentAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token, string? expected = null)
    {
        var (_, manifest) = await ReadGovernanceAsync(admission, store, token);
        if (governance != FactoryReservationRecords.Serialize(manifest)) throw new JsonException();
        if (root is null) return;
        var record = await store.ReadAsync(ProductConfigurationKeys.FactoryModels, token);
        if (record is null || record.ETag != root.ETag || record.Value != (expected ?? root.Value)) throw new JsonException();
        _ = FactoryModelsValidation.ReadPreparation(manifest, await ValuesAsync(store, current!, token));
    }

    private async Task CheckPrerequisitesAsync(FactoryReservationResult admission, FactoryModels selected, FactoryPreparationArm arm, CancellationToken token)
    {
        foreach (var binding in selected.Bindings!)
        {
            using var native = await ReadNativeAsync(arm, binding.Target, binding.ApiVersion, token);
            var resource = native.RootElement;
            var created = resource.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset();
            if (!Same(resource.GetProperty("id").GetString(), binding.Target) || created != DateTimeOffset.Parse(binding.Incarnation!, System.Globalization.CultureInfo.InvariantCulture)
                || resource.GetProperty("systemData").TryGetProperty("lastModifiedAt", out var modified) && modified.GetDateTimeOffset() != created)
                throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, "foundation", "Current foundation incarnation differs from creation receipt.");
            var properties = resource.GetProperty("properties");
            if (binding.PrincipalId is not null && (properties.GetProperty("principalId").GetString() != binding.PrincipalId
                || properties.GetProperty("clientId").GetString() != binding.ClientId || properties.GetProperty("tenantId").GetString() != admission.Identity!.TenantId))
                throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, "model-workload", "Actual workload principal/client/tenant differs; no operator-credential proof.");
            if (binding == selected.Bindings[2] && properties.GetProperty("vnetConfiguration").GetProperty("internal").ValueKind != JsonValueKind.True)
                throw FactoryModelsValidation.Failure(ModelCapabilityReason.UnsupportedTarget, "hosting", "Proven private factory environment required; no shared-network relaxation.");
        }
        foreach (var reviewed in selected.Routes!)
        {
            var route = reviewed.Selection;
            using var account = await GetAsync(arm, route.AccountResourceId, route.Name, token);
            if (FactoryModelsValidation.ObserveAccount(route, account.RootElement) != reviewed.AccountObservation)
                throw FactoryModelsValidation.Failure(ModelCapabilityReason.AuthorityChanged, route.Name, "Reviewed account incarnation/network/location changed.");
            var effect = effects.SingleOrDefault(e => e.Target == FactoryModelsValidation.DeploymentTarget(route)
                && e.State == FoundationOperationState.Completed);
            if (route.Create && effect is null) continue;
            using var deployment = await GetAsync(arm, FactoryModelsValidation.DeploymentTarget(route), route.Name, token);
            var observation = FactoryModelsValidation.ObserveDeployment(route, deployment.RootElement);
            if (route.Create ? observation.Revision != effect!.NativeRevision || observation.Incarnation != effect.Incarnation
                : observation != reviewed.DeploymentObservation)
                throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, route.Name, "Deployment was modified or replaced; effective inference cannot adopt a new binding.");
        }
        foreach (var grant in selected.Grants!)
        {
            using var role = await ReadNativeAsync(arm, grant.RoleDefinitionId, "2022-04-01", token);
            if (FactoryModelsValidation.ObserveRole(grant.RoleDefinitionId, role.RootElement) != grant.RoleRevision)
                throw FactoryModelsValidation.Failure(ModelCapabilityReason.AuthorityChanged, "role", "Reviewed inference/config role definition changed.");
            var effect = effects.SingleOrDefault(e => e.Target == grant.Target && e.State == FoundationOperationState.Completed);
            if (effect is not null)
            {
                using var native = await ReadNativeAsync(arm, grant.Target, "2022-04-01", token);
                var observed = ObserveGrant(native.RootElement, grant, admission.Identity!.AzureActorId);
                if (observed.Incarnation != effect.Incarnation || observed.Revision != effect.NativeRevision)
                    throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, "grant", "Exact current grant principal/role/scope/condition/version/incarnation no longer matches receipt.");
            }
            else
            {
                using var permissions = await ReadNativeAsync(arm, grant.Scope + "/providers/microsoft.authorization/permissions", "2022-04-01", token);
                var allowed = CompleteValues(permissions.RootElement).Any(p =>
                    FactoryModelsValidation.Strings(p, "actions").Any(a => ActionMatches(a, "Microsoft.Authorization/roleAssignments/write"))
                    && !FactoryModelsValidation.Strings(p, "notActions").Any(a => ActionMatches(a, "Microsoft.Authorization/roleAssignments/write")));
                using var denies = await ReadNativeAsync(arm, grant.Scope + "/providers/microsoft.authorization/denyassignments", "2022-04-01", token);
                if (!allowed || CompleteValues(denies.RootElement).Length != 0)
                    throw FactoryModelsValidation.Failure(ModelCapabilityReason.PermissionDenied, "grant", "Azure grantor must separately delegate exact role assignment at " + grant.Scope + "; no grant expansion.");
            }
        }
        var principal = selected.Bindings[0].PrincipalId;
        var preview = await FactoryPreviewMetadataProof.ReadAsync(admission, Store(admission), arm, principal!, clock, token);
        var paths = new[] { "/subscriptions/" + admission.Identity!.SubscriptionId + "/providers/microsoft.authorization/roleassignments?$filter=assignedTo('" + principal + "')" }
            .Concat(selected.Grants.Select(g => g.Scope + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + principal + "')"));
        foreach (var path in paths)
        {
            using var roles = await ReadNativeAsync(arm, path, "2022-04-01", token);
            foreach (var role in CompleteValues(roles.RootElement))
            {
                if (preview.Includes(role)) continue;
                var grant = selected.Grants.SingleOrDefault(g => Same(g.Target, role.GetProperty("id").GetString()));
                if (grant is null || !effects.Any(e => e.Target == grant.Target && e.State == FoundationOperationState.Completed))
                    throw FactoryModelsValidation.Failure(ModelCapabilityReason.PermissionDenied, "model-workload", "Unreviewed inherited/group/preexisting authority; grantor must reconcile, never adopted.");
                _ = ObserveGrant(role, grant, admission.Identity.AzureActorId);
            }
        }
    }

    private static bool ActionMatches(string pattern, string action) => System.Text.RegularExpressions.Regex.IsMatch(action,
        "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static ModelResourceObservation ObserveGrant(JsonElement resource, ModelAccessGrant grant, string actor)
    {
        var properties = resource.GetProperty("properties");
        if (!Same(resource.GetProperty("id").GetString(), grant.Target) || properties.GetProperty("principalId").GetString() != grant.PrincipalId
            || properties.GetProperty("principalType").GetString() != "ServicePrincipal"
            || !Same(properties.GetProperty("scope").GetString(), grant.Scope) || !Same(properties.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId)
            || properties.GetProperty("createdBy").GetString() != actor || Optional(properties, "condition") is not null
            || Optional(properties, "conditionVersion") is not (null or "2.0")
            || properties.TryGetProperty("updatedOn", out var updated) && updated.ValueKind != JsonValueKind.Null
                && updated.GetDateTimeOffset() != properties.GetProperty("createdOn").GetDateTimeOffset())
            throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, "grant", "Current grant differs from exact approved binding; working access is not permission to replace it.");
        return new(grant.Target, FactoryModelsValidation.Hash(properties), properties.GetProperty("createdOn").GetDateTimeOffset().ToString("O"));
    }

    private static ModelResourceObservation ObserveHostResource(JsonElement resource, string target) =>
        FactoryHostedWorkloadPolicy.ObserveModelHost(resource, target);

    private async Task<string> CheckHostAsync(FactoryReservationResult admission, FactoryPreparationArm arm, CancellationToken token)
    {
        var config = current!.Configuration;
        var target = FactoryModelsValidation.HostTarget(current);
        using var host = await ReadNativeAsync(arm, target, "2025-01-01", token);
        using var auth = await ReadNativeAsync(arm, target + "/authconfigs/current", "2025-01-01", token);
        return FactoryHostedWorkloadPolicy.Validate(host.RootElement, auth.RootElement, current.Bindings![0], current.Bindings[2],
            config.RuntimeImage, "serve-models", admission.Identity!.ProductKey, config.ProductEndpoint, config.ProfilePin,
            admission.Identity.TenantId, config.AudienceClientId,
            [admission.Identity.AzureActorId, current.Bindings[1].PrincipalId!, current.Bindings[0].PrincipalId!]);
    }

    private static async Task<Dictionary<string, string>> ValuesAsync(OwnerReservationStore store, FactoryModels authority, CancellationToken token)
    {
        if (authority.OperationCount is < 0 or > 10 || authority.Configuration.GrantHashes.Count is < 2 or > 6) throw new JsonException();
        var keys = new[] { ProductConfigurationKeys.FactoryManifest, ProductConfigurationKeys.FactoryProfile, ProductConfigurationKeys.FactoryModels }
            .Concat(Enumerable.Range(0, 5).Select(i => ProductConfigurationKeys.ModelRoute(authority.OperationId, i)))
            .Concat(FactoryModelsValidation.BindingNames.Select(n => ProductConfigurationKeys.ModelBinding(authority.OperationId, n)))
            .Concat(Enumerable.Range(0, authority.Configuration.GrantHashes.Count).Select(i => ProductConfigurationKeys.ModelGrant(authority.OperationId, i)))
            .Concat(Enumerable.Range(0, authority.OperationCount).Select(i => ProductConfigurationKeys.ModelOperation(authority.OperationId, i)));
        var values = new Dictionary<string, string>();
        foreach (var key in keys)
            if (await store.ReadAsync(key, token) is { } record) values.Add(key, record.Value);
        return values;
    }

    internal static async Task<FactoryReservationResult> InspectAsync(FactoryReservationResult admission, OwnerReservationStore store, TimeProvider clock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (_, manifest) = await ReadGovernanceAsync(admission, store, token);
        if (manifest.ModelsOperationId is null) return admission;
        var root = await store.ReadAsync(ProductConfigurationKeys.FactoryModels, token);
        if (root is null) return admission with { State = FactoryReservationState.Unknown, Detail = "Model intent held without complete authority; no automatic retry." };
        var stored = FactoryReservationRecords.Parse<FactoryModels>(root.Value);
        var authority = FactoryModelsValidation.ReadPreparation(manifest, await ValuesAsync(store, stored, token));
        if (authority.Effects!.Any(e => e.State != FoundationOperationState.Completed))
            authority = authority with { State = ModelCapabilityState.Unknown, Detail = "Model effects incomplete/unknown; inspect retained intent, never recreate." };
        if (authority.LastProofReference is { } reference)
        {
            if (!Guid.TryParse(reference.Split('/').Last(), out var checkId)
                || reference != ProductConfigurationKeys.ModelProof(authority.OperationId, checkId)) throw new JsonException();
            var proof = FactoryReservationRecords.Parse<ModelCapabilityProof>((await store.ReadAsync(reference, token))?.Value ?? throw new JsonException());
            if (FactoryModelsValidation.Hash(proof) != authority.LastProofSha256) throw new JsonException();
            if (authority.State == ModelCapabilityState.Verified)
            {
                FactoryModelsValidation.ValidateProof(authority, proof, clock.GetUtcNow(), requireCurrent: false);
                if (authority.VerifiedUntil != proof.ExpiresAt || proof.State != ModelCapabilityState.Verified) throw new JsonException();
                if (proof.ExpiresAt <= clock.GetUtcNow())
                    authority = authority with { State = ModelCapabilityState.Blocked, VerifiedUntil = null, Detail = "Model proof expired; historical only. Run models --verify-only." };
            }
            authority = authority with { Proof = proof };
        }
        else if (authority.State == ModelCapabilityState.Verified) throw new JsonException();
        return admission with { Models = authority, Foundation = Pending(admission.Foundation, authority.State),
            Detail = "Historical model preparation only; actual hosted proof required. " + admission.Detail };
    }
}
