using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class NativePreviewBindings
{
    internal async Task<string> CheckTelemetryAsync(bool sourceOnly, CancellationToken token)
    {
        var selection = capability.Configuration.Telemetry ?? throw new JsonException();
        var config = telemetry!.Configuration;
        foreach (var binding in new[] { selection.SourceIdentity, bindings["council"], bindings["hosting"] })
        {
            var value = await GetAsync(binding.Target, binding.ApiVersion, token);
            RequireIncarnation(value, binding);
            var p = value.GetProperty("properties");
            if (binding.PrincipalId is not null && (p.GetProperty("principalId").GetString() != binding.PrincipalId
                || p.GetProperty("clientId").GetString() != binding.ClientId || p.GetProperty("tenantId").GetString() != manifest.Identity.TenantId))
                throw Changed("TelemetryWorkloadIdentityChanged");
            if (binding == bindings["hosting"] && p.GetProperty("vnetConfiguration").GetProperty("internal").ValueKind != JsonValueKind.True)
                throw Changed("PrivateTelemetryHostingRequired");
        }
        if (FactoryModelsValidation.Hash(config.HostingEnvironment) != FactoryModelsValidation.Hash(bindings["hosting"]))
            throw Changed("TelemetryHostingBindingChanged");
        var target = FactoryTelemetryValidation.HostTarget(config);
        var originalHost = await GetAsync(target, "2025-01-01", token);
        var originalAuth = await GetAsync(target + "/authconfigs/current", "2025-01-01", token);
        RequireIncarnation(originalHost, telemetryEffects.Single(e => e.Target == target));
        RequireIncarnation(originalAuth, telemetryEffects.Single(e => e.Target == target + "/authconfigs/current"));
        if (FactoryHostedWorkloadPolicy.Validate(originalHost, originalAuth, config.SourceIdentity, config.HostingEnvironment,
            config.RuntimeImage, "serve-telemetry", Product, ProductEndpoint, manifest.ProfilePin, manifest.Identity.TenantId,
            config.AudienceClientId, [manifest.Identity.AzureActorId]) != telemetry.HostEndpoint)
            throw Changed("OriginalTelemetryHostChanged");

        var endpoint = await NewHostAsync(PreviewHostKind.TelemetryEvidence, token);
        var previewTarget = FactoryPreviewValidation.HostTarget(Group, manifest.Identity.FactoryId, PreviewHostKind.TelemetryEvidence);
        var role = await GetAsync(previewTarget + "/providers/microsoft.authorization/roledefinitions/"
            + selection.MetadataRoleDefinitionId.Split('/').Last(), "2022-04-01", token);
        var properties = role.GetProperty("properties");
        if (!Same(role.GetProperty("id").GetString(), selection.MetadataRoleDefinitionId)
            || properties.GetProperty("type").GetString() != "CustomRole"
            || FactoryModelsValidation.Hash(properties) != selection.MetadataRoleRevision
            || properties.GetProperty("permissions").GetArrayLength() != 1) throw Changed("TelemetryMetadataRoleChanged");
        Exact(properties.GetProperty("permissions")[0], "actions", FactoryPreviewTelemetryValidation.MetadataActions);
        foreach (var field in new[] { "notActions", "dataActions", "notDataActions" }) Exact(properties.GetProperty("permissions")[0], field, []);

        var configGrant = config.AccessGrants.Single(g => g.Scope.Contains("/microsoft.appconfiguration/", StringComparison.Ordinal));
        if (sourceOnly)
        {
            var governance = await GetAsync(configGrant.Scope, "2024-06-01", token);
            var p = governance.GetProperty("properties");
            if (p.GetProperty("disableLocalAuth").ValueKind != JsonValueKind.True || p.GetProperty("provisioningState").GetString() != "Succeeded"
                || FactoryGovernance.NormalizeEndpoint(p.GetProperty("endpoint").GetString()!) != ProductEndpoint)
                throw Changed("ProtectedTelemetryGovernanceRequired");
            FactoryPreviewTelemetryValidation.RequireGrant(await GetAsync(configGrant.Target, "2022-04-01", token), configGrant,
                telemetryEffects.Single(e => e.Target == configGrant.Target), manifest.Identity.AzureActorId);
            FactoryPreviewTelemetryValidation.RequireRole(await GetAsync(configGrant.Scope + "/providers/microsoft.authorization/roledefinitions/"
                + configGrant.RoleDefinitionId.Split('/').Last(), "2022-04-01", token), configGrant.RoleDefinitionId,
                selection.AccessRoleRevisions[configGrant.RoleDefinitionId]);
        }
        var scopes = FactoryPreviewTelemetryValidation.MetadataScopes(manifest, models, capability.OperationId, selection);
        foreach (var scope in scopes.Where(scope => sourceOnly || !selection.Applications.Contains(scope)))
        {
            var grant = FactoryModelsValidation.Grant(scope, selection.MetadataRoleDefinitionId, config.SourceIdentity.PrincipalId!, "");
            var effect = effects.Single(e => e.Target == grant.Target);
            RequireAssignment(await GetAsync(effect.Target, effect.ApiVersion, token), effect, scope, grant.RoleDefinitionId, grant.PrincipalId);
            if (!sourceOnly) continue;
            var assignments = Complete(await GetAsync(scope + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('"
                + grant.PrincipalId + "')", "2022-04-01", token));
            var permitted = scopes.Select(s => FactoryModelsValidation.Grant(s, selection.MetadataRoleDefinitionId, grant.PrincipalId, "").Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!assignments.Any(a => Same(a.GetProperty("id").GetString(), grant.Target))) throw Changed("TelemetryMetadataGrantMissing");
            foreach (var assignment in assignments)
            {
                var id = assignment.GetProperty("id").GetString()!;
                if (Same(id, configGrant.Target))
                    FactoryPreviewTelemetryValidation.RequireGrant(assignment, configGrant,
                        telemetryEffects.Single(e => e.Target == id), manifest.Identity.AzureActorId);
                else if (!permitted.Contains(id)) throw Changed("TelemetryMetadataScopeExpanded");
                else
                {
                    var receipt = effects.Single(e => Same(e.Target, id));
                    RequireAssignment(assignment, receipt, assignment.GetProperty("properties").GetProperty("scope").GetString()!,
                        selection.MetadataRoleDefinitionId, grant.PrincipalId);
                }
            }
        }
        if (sourceOnly)
        {
            foreach (var effect in effects.Where(e => e.Target == previewTarget || e.Target == previewTarget + "/authconfigs/current"
                || scopes.Any(s => e.Target == FactoryModelsValidation.Grant(s, selection.MetadataRoleDefinitionId, config.SourceIdentity.PrincipalId!, "").Target)))
            {
                var operation = await GetAsync(effect.DeploymentTarget! + "/operations/" + effect.ProviderOperationId, "2022-09-01", token);
                var p = operation.GetProperty("properties");
                if (operation.GetProperty("operationId").GetString() != effect.ProviderOperationId
                    || !Same(p.GetProperty("targetResource").GetProperty("id").GetString(), effect.Target)
                    || p.GetProperty("provisioningOperation").GetString() != "Create" || p.GetProperty("provisioningState").GetString() != "Succeeded"
                    || p.GetProperty("trackingId").GetString() != effect.ProviderRequestId || p.GetProperty("statusCode").GetString() is not ("Created" or "201"))
                    throw Changed("TelemetryPreviewDeploymentReceiptChanged");
            }
        }
        return endpoint;
    }
}
