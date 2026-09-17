using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    private async Task<(FactorySupplemental? Source, IReadOnlyList<FoundationOperation> Effects, PreviewSupplementalConfiguration? Configuration)> SupplementalAsync(
        FactoryPreparationArm arm, FactoryManifest manifest, PreviewPreparationSelection selection, IReadOnlyDictionary<string, string> values,
        IDictionary<string, PreviewPreparationNative> native, CancellationToken token)
    {
        RequireSupplementalNamespace(manifest, selection, values);
        if (!values.TryGetValue(ProductConfigurationKeys.FactorySupplemental, out var raw))
            return (null, [], null);
        var source = FactorySupplementalValidation.Read(manifest, raw);
        if (source.State != SupplementalState.ReadySupplemental || source.Configuration is not { } config)
            throw Failure("CurrentSelectedSupplementalPreparationRequired");
        var effects = FactoryPreviewSupplementalValidation.ReadEffects(source, values);
        if (!config.Selection.Sources.Select(s => s.Kind).Order(StringComparer.Ordinal)
            .SequenceEqual(selection.Supplemental.Select(s => s.Kind).Order(StringComparer.Ordinal)))
            throw Failure("EverySelectedSupplementalMustRemainSelected");
        var hosts = new List<PreviewSupplementalHost>();
        foreach (var selected in config.Selection.Sources)
        {
            var identity = effects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(config, selected.Kind));
            await ObserveBindingAsync(arm, identity, manifest, native, token);
            var check = source.Checks.Single(c => c.Kind == selected.Kind);
            if (check.State != SupplementalState.ReadySupplemental || !values.TryGetValue(check.ProofKey, out var proofJson)
                || FactoryTelemetryValidation.Hash(JsonSerializer.Deserialize<SupplementalProof>(proofJson, FactorySourceValidation.RecordJson)!) != check.ProofSha256)
                throw Failure("CompleteSelectedSupplementalProofRequired:" + selected.Kind);
            var proof = JsonSerializer.Deserialize<SupplementalProof>(proofJson, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            FactorySupplementalValidation.ValidateProof(source, identity, proof, clock.GetUtcNow());
            var input = selection.Supplemental.Single(s => s.Kind == selected.Kind);
            var role = await MetadataRoleAsync(arm, input.MetadataRoleDefinitionId,
                FactoryPreviewSupplementalValidation.MetadataActions(selected.Kind), native, token);
            var scope = FactoryPreviewSupplementalValidation.MetadataScope(config, selected);
            hosts.Add(new(selected.Kind, FactoryModelsValidation.Hash(identity), identity.PrincipalId!, identity.ClientId!,
                input.RuntimeImage, input.AudienceClientId, input.MetadataRoleDefinitionId, role, scope));
            var target = FactorySupplementalValidation.HostTarget(config, selected.Kind);
            _ = FactoryHostedWorkloadPolicy.Validate(await CaptureAsync(arm, target, "2025-01-01", native, token),
                await CaptureAsync(arm, target + "/authconfigs/current", "2025-01-01", native, token), identity, config.Hosting,
                config.Selection.RuntimeImage, "serve-supplemental", manifest.Identity.ProductKey, config.ProductEndpoint, manifest.ProfilePin,
                manifest.Identity.TenantId, config.Selection.AudienceClientId, [manifest.Identity.AzureActorId],
                new Dictionary<string, string> { ["DSF_SUPPLEMENTAL_CONFIG"] = FactoryReservationRecords.Serialize(
                    FactorySupplementalValidation.HostConfiguration(manifest, source, identity, selected.Kind)) });
            foreach (var grant in FactorySupplementalValidation.Grants(config, selected))
            {
                var existing = effects.Single(e => e.Target == grant.Target);
                var definition = await CaptureAsync(arm, grant.RoleDefinitionId, "2022-04-01", native, token);
                var properties = definition.GetProperty("properties");
                var permissions = properties.GetProperty("permissions");
                if (permissions.GetArrayLength() != 1) throw Failure("ExactOriginalSupplementalRoleRequired");
                var permission = permissions[0];
                if (!FactoryModelsValidation.Strings(permission, "actions").Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(grant.Actions.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                    || !FactoryModelsValidation.Strings(permission, "dataActions").Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(grant.DataActions.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                    || FactoryModelsValidation.Strings(permission, "notActions").Length != 0 || FactoryModelsValidation.Strings(permission, "notDataActions").Length != 0)
                    throw Failure("OriginalSupplementalRoleChanged:" + grant.RoleDefinitionId);
                RequireGrant(await CaptureAsync(arm, grant.Target, "2022-04-01", native, token),
                    new(grant.Target, identity.PrincipalId!, grant.RoleDefinitionId, grant.Scope, FactoryModelsValidation.Hash(properties)),
                    manifest.Identity.AzureActorId, existing.Incarnation);
            }
            if (selected.Kind == "webiq")
                await CaptureAsync(arm, config.Vault!.Target, config.Vault.ApiVersion, native, token);
            else await CaptureAsync(arm, selected.SearchResourceId!, "2025-05-01", native, token);
        }
        return (source, effects, new(source.ApprovalFingerprint!, FactoryModelsValidation.Hash(effects), hosts));
    }

    private static void RequireSupplementalNamespace(FactoryManifest manifest, PreviewPreparationSelection selection, IReadOnlyDictionary<string, string> values)
    {
        if (!values.ContainsKey(ProductConfigurationKeys.FactorySupplemental)
            && (manifest.SupplementalOperationId is not null || manifest.SupplementalApprovalFingerprint is not null
                || values.Keys.Any(FactoryPreviewSupplementalValidation.IsSelectionRecord) || selection.Supplemental.Count != 0))
            throw Unknown("SelectedSupplementalNamespaceUnconfirmed");
    }

    private static string WorkloadBody(FactoryReservationResult admission, FactoryManifest manifest, PreviewPreparationSelection selection,
        FactoryPreviewConfiguration config, IReadOnlyDictionary<string, FoundationOperation> bindings,
        FactorySupplemental? supplemental, IReadOnlyList<FoundationOperation> supplementalEffects, byte[] templateBytes)
    {
        using var template = JsonDocument.Parse(templateBytes);
        var identity = admission.Identity!;
        var account = bindings["account"].Target;
        var role = FactoryPreviewValidation.SqlRole(account, identity.FactoryId);
        var parameters = new Dictionary<string, object>
        {
            ["namePrefix"] = FactoryPreviewValidation.Prefix(identity.FactoryId),
            ["location"] = admission.Plan!.Selection.FactoryLocation,
            ["productKey"] = identity.ProductKey,
            ["productEndpoint"] = admission.Foundation!.ProductEndpoint!,
            ["profilePin"] = config.ProfilePin,
            ["tenantId"] = identity.TenantId,
            ["operatorPrincipalId"] = identity.AzureActorId,
            ["runtimeImage"] = config.RuntimeImage,
            ["audienceClientId"] = config.AudienceClientId,
            ["modelRuntimeImage"] = selection.Models.RuntimeImage,
            ["modelAudienceClientId"] = selection.Models.AudienceClientId,
            ["telemetryRuntimeImage"] = selection.Telemetry.RuntimeImage,
            ["telemetryAudienceClientId"] = selection.Telemetry.AudienceClientId,
            ["stateRoleId"] = role.Split('/').Last(),
            ["runsAssignmentId"] = FactoryPreviewValidation.SqlAssignment(account, bindings["council"].PrincipalId!,
                FactoryPreviewValidation.SqlScope(account, identity.ProductKey, "runs"), role).Split('/').Last(),
            ["previewAssignmentId"] = FactoryPreviewValidation.SqlAssignment(account, bindings["council"].PrincipalId!,
                FactoryPreviewValidation.SqlScope(account, identity.ProductKey, FactoryPreviewValidation.PreviewContainer), role).Split('/').Last(),
            ["supplementalSources"] = selection.Supplemental.Select(s => new
            {
                kind = s.Kind, runtimeImage = s.RuntimeImage, audienceClientId = s.AudienceClientId,
                workloadConfiguration = JsonSerializer.Serialize(FactorySupplementalValidation.HostConfiguration(
                    manifest, supplemental!, supplementalEffects.Single(e =>
                        e.Target == FactorySupplementalValidation.IdentityTarget(supplemental!.Configuration!, s.Kind)), s.Kind), FactoryGitHubJson.Options),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(new { properties = new { mode = "Incremental", template = template.RootElement,
            parameters = parameters.ToDictionary(p => p.Key, p => new { value = p.Value }) } }, RecordJson);
    }
}
