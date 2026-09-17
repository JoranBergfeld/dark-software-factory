using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class NativePreviewBindings
{
    private async Task<string> CheckSupplementalAsync(PreviewSupplementalHost selected, string? councilEndpoint, CancellationToken token)
    {
        var identity = supplementalEffects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(supplemental!.Configuration!, selected.Kind));
        if (bindings.Values.Any(b => b.PrincipalId is not null && b.PrincipalId == identity.PrincipalId)
            || telemetry?.Configuration.SourceIdentity.PrincipalId == identity.PrincipalId) throw Changed("DistinctSupplementalIdentityRequired");
        var native = await GetAsync(identity.Target, identity.ApiVersion, token);
        RequireIncarnation(native, identity);
        var p = native.GetProperty("properties");
        var created = native.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset();
        if (p.GetProperty("principalId").GetString() != identity.PrincipalId || p.GetProperty("clientId").GetString() != identity.ClientId
            || p.GetProperty("tenantId").GetString() != manifest.Identity.TenantId
            || FactoryTelemetryValidation.Hash(JsonSerializer.SerializeToElement(new { created, principal = identity.PrincipalId,
                client = identity.ClientId, tenant = manifest.Identity.TenantId, target = identity.Target })) != identity.NativeRevision)
            throw Changed("SupplementalIdentityChanged");
        var config = supplemental!.Configuration!;
        if (FactoryModelsValidation.Hash(config.Hosting) != FactoryModelsValidation.Hash(bindings["hosting"])) throw Changed("SupplementalHostingChanged");
        var target = FactorySupplementalValidation.HostTarget(config, selected.Kind);
        var host = await GetAsync(target, "2025-01-01", token);
        var auth = await GetAsync(target + "/authconfigs/current", "2025-01-01", token);
        foreach (var (body, id) in new[] { (host, target), (auth, target + "/authconfigs/current") })
        {
            var observed = FactoryHostedWorkloadPolicy.ObserveExactHost(body, id);
            var effect = supplementalEffects.Single(e => e.Target == id);
            if (observed.Revision != effect.NativeRevision || observed.Incarnation != effect.Incarnation) throw Changed("OriginalSupplementalHostChanged");
        }
        _ = FactoryHostedWorkloadPolicy.Validate(host, auth, identity, config.Hosting, config.Selection.RuntimeImage, "serve-supplemental",
            Product, ProductEndpoint, manifest.ProfilePin, manifest.Identity.TenantId, config.Selection.AudienceClientId, [manifest.Identity.AzureActorId],
            new Dictionary<string, string> { ["DSF_SUPPLEMENTAL_CONFIG"] = JsonSerializer.Serialize(
                FactorySupplementalValidation.HostConfiguration(manifest, supplemental, identity, selected.Kind), FactoryGitHubJson.Options) });
        var currentRole = await GetAsync(target + "/providers/microsoft.authorization/roledefinitions/" + selected.MetadataRoleDefinitionId.Split('/').Last(), "2022-04-01", token);
        var rp = currentRole.GetProperty("properties");
        if (!Same(currentRole.GetProperty("id").GetString(), selected.MetadataRoleDefinitionId)
            || rp.GetProperty("type").GetString() != "CustomRole" || FactoryModelsValidation.Hash(rp) != selected.MetadataRoleRevision
            || rp.GetProperty("permissions").GetArrayLength() != 1) throw Changed("SupplementalPreviewMetadataRoleChanged");
        Exact(rp.GetProperty("permissions")[0], "actions", FactoryPreviewSupplementalValidation.MetadataActions(selected.Kind));
        foreach (var field in new[] { "notActions", "dataActions", "notDataActions" }) Exact(rp.GetProperty("permissions")[0], field, []);
        return await NewHostAsync(FactoryPreviewSupplementalValidation.HostKind(selected.Kind), token, councilEndpoint);
    }
}
