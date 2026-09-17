using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal sealed class AzureFactoryPreviewCapability(
    FactoryHostSettings settings, PreviewHostKind kind, TokenCredential credential, HttpClient http, TimeProvider clock)
    : IFactoryPreviewCapability
{
    public async Task<PreviewCapabilitySnapshot> CheckAsync(PreviewCapabilityRequest request, CancellationToken token)
    {
        try
        {
            var records = await ReadAsync(credential, token);
            var values = records.Values;
            var manifest = FactoryGovernance.ReadInactiveManifest(values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile), settings.Product, settings.ProductEndpoint, settings.ProfilePin);
            var github = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
            FactoryGitHubAuthorityValidation.ReadCompletedEffects(github, values);
            FactoryGitHubAuthorityValidation.ValidateVerified(github);
            var (source, sourceEffects) = FactorySourceValidation.Read(manifest, github, values);
            var models = FactoryModelsValidation.ReadPreparation(manifest, values);
            models = FactoryModelsValidation.ReadVerified(manifest, values,
                new(models.OperationId, models.ApprovalFingerprint, models.LastProofReference ?? "", models.LastProofSha256 ?? ""), clock.GetUtcNow());
            var (capability, bindings, effects) = FactoryPreviewValidation.Read(manifest, models, github, source, sourceEffects, values, request, clock.GetUtcNow());
            if (kind == PreviewHostKind.ModelEvaluation && capability.Configuration.ModelEvaluation is null)
                throw Denied("ApprovedPreviewModelConsumerRequired");
            var telemetry = capability.Configuration.Telemetry is null ? null : FactoryPreviewTelemetryValidation.Read(manifest, capability.Configuration, values);
            if (kind == PreviewHostKind.TelemetryEvidence && telemetry is null)
                throw Denied("PreviewTelemetryProjectionRequired:dsf/factory/preview.configuration.telemetry");
            var telemetryEffects = telemetry is null ? [] : FactoryTelemetryValidation.ReadCompletedEffects(telemetry, values);
            var supplemental = FactoryPreviewSupplementalValidation.Read(manifest, capability.Configuration, values);
            var supplementalEffects = supplemental is null ? [] : FactoryPreviewSupplementalValidation.ReadEffects(supplemental, values);
            var own = kind == PreviewHostKind.TelemetryEvidence ? telemetry!.Configuration.SourceIdentity
                : bindings[kind == PreviewHostKind.SourceContext ? "code" : "council"];
            if (settings.ManagedIdentityClientId != own.ClientId) throw Denied("PreviewWorkloadIdentityMismatch");
            var nativeCredential = new FactoryWorkloadCredential(credential, manifest.Identity.TenantId, own, clock);
            _ = await nativeCredential.GetTokenAsync(new TokenRequestContext(["https://appconfig.azure.com/.default"]), token);
            var native = new NativePreviewBindings(manifest, models, github, source, sourceEffects, capability, bindings, effects,
                telemetry, telemetryEffects, supplemental, supplementalEffects, nativeCredential, http, clock);
            var endpoints = kind == PreviewHostKind.TelemetryEvidence
                ? (Council: (string?)null, Source: (string?)null, Models: (string?)null, Telemetry: await native.CheckTelemetryAsync(true, token))
                : await native.CheckAsync(token);
            var current = await ReadAsync(nativeCredential, token);
            if (FactoryModelsValidation.Hash(records) != FactoryModelsValidation.Hash(current)) throw Denied("PreviewGovernanceChanged");
            var revision = FactoryModelsValidation.Hash(new { Records = records, Native = native.Revisions });
            return new(manifest, capability, bindings["council"], bindings["code"], endpoints.Council, endpoints.Source, revision)
            { ModelEndpoint = endpoints.Models, Models = models, GovernanceRevision = FactoryModelsValidation.Hash(records),
                Telemetry = telemetry, TelemetryEffects = telemetryEffects, TelemetryEndpoint = endpoints.Telemetry,
                Supplemental = supplemental, SupplementalEffects = supplementalEffects, Effects = effects,
                SupplementalEndpoints = native.SupplementalEndpoints };
        }
        catch (PreviewCapabilityException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ModelCapabilityException)
        { throw Denied("CurrentPreviewModelOrIdentityAuthorityRequired"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException
            or HttpRequestException or IOException or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException)
        { throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ProtectedPreviewBindingsUnconfirmed"); }
    }

    private sealed record ProtectedRecords(SortedDictionary<string, string> Values, SortedDictionary<string, string> Versions);

    private async Task<ProtectedRecords> ReadAsync(TokenCredential currentCredential, CancellationToken token)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var audit = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var setting in new AzureConfigurationSettingsGateway(currentCredential, http)
            .ListRecordsAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
        {
            var key = setting.Key;
            var metadata = Metadata(setting);
            if (Encoding.UTF8.GetByteCount(key + setting.Value + metadata.GetRawText()) + 512 > FactoryCharterLimits.RecordBytes
                || string.IsNullOrEmpty(setting.ETag.ToString())
                || key.StartsWith("dsf/factory/preview", StringComparison.Ordinal) && setting.ContentType != FactoryPreviewValidation.RecordContentType)
                throw new JsonException();
            if (key.StartsWith(FactoryCharterLimits.AuditPrefix, StringComparison.Ordinal))
            {
                if (!audit.Add(key) || audit.Count > FactoryCharterLimits.MaximumAuditRecords) throw new JsonException();
                if (key is not (ProductConfigurationKeys.CharterAdopted or ProductConfigurationKeys.CharterAdoptionOperation)) continue;
            }
            if (values.Count >= 512 || !values.TryAdd(key, setting.Value)) throw new JsonException();
            versions.Add(key, FactoryModelsValidation.Hash(metadata));
        }
        var supplemental = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefix in FactoryPreviewSupplementalValidation.NamespacePrefixes)
        await foreach (var setting in new AzureConfigurationSettingsGateway(currentCredential, http)
            .ListRecordsAsync(settings.ProductEndpoint, "*", token, prefix + "*"))
        {
            if (!setting.Key.StartsWith(prefix, StringComparison.Ordinal)
                || setting.Label is not null || !supplemental.Add(setting.Key)
                || !values.TryGetValue(setting.Key, out var value) || value != setting.Value
                || !versions.TryGetValue(setting.Key, out var version) || version != FactoryModelsValidation.Hash(Metadata(setting)))
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "SupplementalNamespaceUnconfirmed");
        }
        if (!supplemental.SetEquals(values.Keys.Where(FactoryPreviewSupplementalValidation.IsSelectionRecord)))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "SupplementalNamespaceUnconfirmed");
        return new(values, versions);

        static JsonElement Metadata(Azure.Data.AppConfiguration.ConfigurationSetting setting) => JsonSerializer.SerializeToElement(new
        { ETag = setting.ETag.ToString(), setting.Label, setting.ContentType,
            Tags = new SortedDictionary<string, string>(setting.Tags, StringComparer.Ordinal), setting.IsReadOnly, setting.LastModified });
    }

    private static PreviewCapabilityException Denied(string reason) => new(FactoryCharterState.Blocked, reason);
}

internal sealed partial class NativePreviewBindings(
    FactoryManifest manifest, FactoryModels models, FactoryGitHubAuthority github, FactorySource source,
    IReadOnlyList<FoundationOperation> sourceEffects, FactoryPreviewCapability capability,
    IReadOnlyDictionary<string, FoundationOperation> bindings, IReadOnlyList<FoundationOperation> effects,
    FactoryTelemetry? telemetry, IReadOnlyList<FoundationOperation> telemetryEffects,
    FactorySupplemental? supplemental, IReadOnlyList<FoundationOperation> supplementalEffects,
    TokenCredential credential, HttpClient http, TimeProvider clock)
{
    internal SortedDictionary<string, string> Revisions { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> SupplementalEndpoints { get; } = new(StringComparer.Ordinal);
    private string Group => FactoryPreviewValidation.Group(models);
    private string Account => bindings["account"].Target;

    internal async Task<(string? Council, string? Source, string? Models, string? Telemetry)> CheckAsync(CancellationToken token)
    {
        foreach (var (name, binding) in bindings)
        {
            var native = await GetAsync(binding.Target, binding.ApiVersion, token);
            RequireIncarnation(native, binding);
            var properties = native.GetProperty("properties");
            if (name is "council" or "code")
            {
                if (properties.GetProperty("principalId").GetString() != binding.PrincipalId
                    || properties.GetProperty("clientId").GetString() != binding.ClientId
                    || properties.GetProperty("tenantId").GetString() != manifest.Identity.TenantId) throw Changed("PreviewIdentityChanged");
            }
            else if (name == "hosting" && properties.GetProperty("vnetConfiguration").GetProperty("internal").ValueKind != JsonValueKind.True)
                throw Changed("PrivatePreviewHostingRequired");
            else if (name == "account" && (properties.GetProperty("disableLocalAuth").ValueKind != JsonValueKind.True
                || properties.GetProperty("publicNetworkAccess").GetString() != "Disabled"
                || FactoryGovernance.NormalizeEndpoint(properties.GetProperty("documentEndpoint").GetString()!)
                    != "https://" + FactoryPreviewValidation.Prefix(manifest.Identity.FactoryId) + "-cosmos.documents.azure.com"))
                throw Changed("PrivateFactoryStateAccountRequired");
            else if (name == "database" && properties.GetProperty("resource").GetProperty("id").GetString() != manifest.Identity.ProductKey)
                throw Changed("PreviewStateDatabaseChanged");
            else if (name == "runs") RequireContainer(native, "runs");
        }
        var configGrant = models.Grants!.Single(g => g.Scope.EndsWith("/dsfo-" + manifest.Identity.FactoryId.ToString("N"), StringComparison.Ordinal));
        var governance = await GetAsync(configGrant.Scope, "2024-06-01", token);
        var protection = governance.GetProperty("properties");
        if (!Same(governance.GetProperty("id").GetString(), configGrant.Scope)
            || protection.GetProperty("disableLocalAuth").ValueKind != JsonValueKind.True
            || protection.GetProperty("provisioningState").GetString() != "Succeeded"
            || FactoryGovernance.NormalizeEndpoint(protection.GetProperty("endpoint").GetString()!) != ProductEndpoint)
            throw Changed("ProtectedProductGovernanceRequired");
        var configEffect = models.Effects!.Single(e => e.Target == configGrant.Target);
        var configRole = await GetAsync(configGrant.Scope + "/providers/microsoft.authorization/roledefinitions/" + configGrant.RoleDefinitionId.Split('/').Last(), "2022-04-01", token);
        if (FactoryModelsValidation.ObserveRole(configGrant.RoleDefinitionId, configRole) != configGrant.RoleRevision) throw Changed("CouncilGovernanceReadRoleChanged");
        RequireAssignment(await GetAsync(configGrant.Target, "2022-04-01", token), configEffect,
            configGrant.Scope, configGrant.RoleDefinitionId, configGrant.PrincipalId);

        await OriginalHostsAsync(token);
        var role = await GetAsync(FactoryPreviewValidation.HostTarget(Group, manifest.Identity.FactoryId, PreviewHostKind.Council)
            + "/providers/microsoft.authorization/roledefinitions/" + capability.Configuration.MetadataRoleDefinitionId.Split('/').Last(), "2022-04-01", token);
        var rp = role.GetProperty("properties");
        if (!Same(role.GetProperty("id").GetString(), capability.Configuration.MetadataRoleDefinitionId)
            || rp.GetProperty("type").GetString() != "CustomRole"
            || FactoryModelsValidation.Hash(rp) != capability.Configuration.MetadataRoleRevision
            || rp.GetProperty("permissions").GetArrayLength() != 1) throw Changed("PreviewMetadataRoleChanged");
        var permission = rp.GetProperty("permissions")[0];
        Exact(permission, "actions", FactoryPreviewValidation.MetadataActions);
        Exact(permission, "notActions", []);
        Exact(permission, "dataActions", []);
        Exact(permission, "notDataActions", []);

        foreach (var effect in effects)
        {
            var operation = await GetAsync(effect.DeploymentTarget! + "/operations/" + effect.ProviderOperationId, "2022-09-01", token);
            var op = operation.GetProperty("properties");
            if (operation.GetProperty("operationId").GetString() != effect.ProviderOperationId
                || !Same(op.GetProperty("targetResource").GetProperty("id").GetString(), effect.Target)
                || op.GetProperty("provisioningOperation").GetString() != "Create"
                || op.GetProperty("provisioningState").GetString() != "Succeeded"
                || op.GetProperty("trackingId").GetString() != effect.ProviderRequestId
                || (effect.HttpStatus == 201 ? op.GetProperty("statusCode").GetString() is not ("Created" or "201")
                    : op.GetProperty("statusCode").GetString() is not ("OK" or "200")))
                throw Changed("PreviewNativeDeploymentReceiptChanged");
        }
        var preview = await GetAsync(effects[0].Target, effects[0].ApiVersion, token);
        RequireIncarnation(preview, effects[0]);
        RequireContainer(preview, FactoryPreviewValidation.PreviewContainer);
        RequirePropertiesRevision(preview, effects[0]);
        var sqlRole = await GetAsync(effects[1].Target, effects[1].ApiVersion, token);
        RequirePropertiesRevision(sqlRole, effects[1]);
        var sql = sqlRole.GetProperty("properties");
        if (sql.GetProperty("type").GetString() != "CustomRole" || sql.GetProperty("permissions").GetArrayLength() != 1)
            throw Changed("PreviewStateRoleChanged");
        Exact(sql, "assignableScopes", [Account]);
        Exact(sql.GetProperty("permissions")[0], "dataActions", FactoryPreviewValidation.StateDataActions);
        for (var index = 0; index < 2; index++)
        {
            var effect = effects[index + 2];
            var assignment = await GetAsync(effect.Target, effect.ApiVersion, token);
            RequirePropertiesRevision(assignment, effect);
            var properties = assignment.GetProperty("properties");
            if (properties.GetProperty("principalId").GetString() != bindings["council"].PrincipalId
                || !Same(properties.GetProperty("roleDefinitionId").GetString(), effects[1].Target)
                || !Same(properties.GetProperty("scope").GetString(), FactoryPreviewValidation.SqlScope(Account, manifest.Identity.ProductKey,
                    index == 0 ? "runs" : FactoryPreviewValidation.PreviewContainer))) throw Changed("PreviewStateGrantChanged");
        }
        var sqlAssignments = await GetAsync(Account + "/sqlroleassignments", "2024-11-15", token);
        var assigned = Complete(sqlAssignments).Where(a => a.GetProperty("properties").GetProperty("principalId").GetString() == bindings["council"].PrincipalId).ToArray();
        if (!assigned.Select(a => a.GetProperty("id").GetString()!).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(effects.Skip(2).Take(2).Select(e => e.Target).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw Changed("CouncilStateScopeExceedsPreviewContainers");
        foreach (var assignment in assigned)
            RequirePropertiesRevision(assignment, effects.Single(e => Same(e.Target, assignment.GetProperty("id").GetString())));

        var scopes = FactoryPreviewValidation.MetadataScopes(manifest, models, github, capability.OperationId, capability.Configuration);
        for (var i = 0; i < scopes.Count; i++)
        {
            var effect = effects[4 + i];
            RequireAssignment(await GetAsync(effect.Target, effect.ApiVersion, token), effect, scopes[i],
                capability.Configuration.MetadataRoleDefinitionId, bindings["council"].PrincipalId!);
            var assignments = Complete(await GetAsync(scopes[i]
                + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + bindings["council"].PrincipalId + "')", "2022-04-01", token));
            foreach (var assignment in assignments)
            {
                var id = assignment.GetProperty("id").GetString();
                var permitted = effects.Skip(4).Take(scopes.Count).SingleOrDefault(e => Same(e.Target, id));
                if (permitted is null && !Same(configGrant.Target, id)) throw Changed("CouncilMetadataScopeExpanded");
                RequireAssignment(assignment, permitted ?? configEffect,
                    permitted is null ? configGrant.Scope : scopes[permitted.Index - 4],
                    permitted is null ? configGrant.RoleDefinitionId : capability.Configuration.MetadataRoleDefinitionId, bindings["council"].PrincipalId!);
            }
            if (!assignments.Any(a => Same(a.GetProperty("id").GetString(), effect.Target))) throw Changed("PreviewMetadataGrantMissing");
        }
        var council = await NewHostAsync(PreviewHostKind.Council, token);
        var context = await NewHostAsync(PreviewHostKind.SourceContext, token);
        var model = capability.Configuration.ModelEvaluation is null ? null : await NewHostAsync(PreviewHostKind.ModelEvaluation, token);
        var evidence = telemetry is null ? null : await CheckTelemetryAsync(false, token);
        if (supplemental is not null)
            foreach (var selected in capability.Configuration.Supplemental!.Hosts)
                SupplementalEndpoints.Add(selected.Kind, await CheckSupplementalAsync(selected, council, token));
        return (council, context, model, evidence);
    }

    private async Task OriginalHostsAsync(CancellationToken token)
    {
        var sourceTarget = FactorySourceValidation.HostTarget(github);
        var sourceHost = await GetAsync(sourceTarget, "2025-01-01", token);
        var sourceAuth = await GetAsync(sourceTarget + "/authconfigs/current", "2025-01-01", token);
        foreach (var (value, target) in new[] { (sourceHost, sourceTarget), (sourceAuth, sourceTarget + "/authconfigs/current") })
        {
            var effect = sourceEffects.Single(e => e.Target == target);
            if (FactorySourceValidation.ObserveResource(value, target, manifest, github) != effect.Incarnation
                || FactorySourceValidation.NativeRevision(value, target) != effect.NativeRevision) throw Changed("SourceDeploymentChanged");
        }
        if (FactoryHostedWorkloadPolicy.Validate(sourceHost, sourceAuth, bindings["code"], bindings["hosting"],
            source.Configuration.Selection.RuntimeImage, "serve-source", Product, ProductEndpoint, manifest.ProfilePin,
            manifest.Identity.TenantId, source.Configuration.Selection.AudienceClientId,
            [manifest.Identity.AzureActorId, bindings["code"].PrincipalId!]) != source.HostEndpoint) throw Changed("SourceEndpointChanged");
        var modelTarget = FactoryModelsValidation.HostTarget(models);
        var modelHost = await GetAsync(modelTarget, "2025-01-01", token);
        var modelAuth = await GetAsync(modelTarget + "/authconfigs/current", "2025-01-01", token);
        foreach (var (value, target) in new[] { (modelHost, modelTarget), (modelAuth, modelTarget + "/authconfigs/current") })
        {
            var observed = FactoryHostedWorkloadPolicy.ObserveModelHost(value, target);
            var effect = models.Effects!.Single(e => e.Target == target);
            if (observed.Revision != effect.NativeRevision || observed.Incarnation != effect.Incarnation) throw Changed("ModelDeploymentChanged");
        }
        if (FactoryHostedWorkloadPolicy.Validate(modelHost, modelAuth, bindings["council"], bindings["hosting"],
            models.Configuration.RuntimeImage, "serve-models", Product, ProductEndpoint, manifest.ProfilePin,
            manifest.Identity.TenantId, models.Configuration.AudienceClientId,
            [manifest.Identity.AzureActorId, bindings["code"].PrincipalId!, bindings["council"].PrincipalId!]) != models.HostEndpoint)
            throw Changed("ModelEndpointChanged");
    }

    private string Product => manifest.Identity.ProductKey;
    private string ProductEndpoint => "https://dsfo-" + manifest.Identity.FactoryId.ToString("N") + ".azconfig.io";

    private async Task<string> NewHostAsync(PreviewHostKind kind, CancellationToken token, string? coordinatorEndpoint = null)
    {
        var target = FactoryPreviewValidation.HostTarget(Group, manifest.Identity.FactoryId, kind);
        var host = await GetAsync(target, "2025-01-01", token);
        var auth = await GetAsync(target + "/authconfigs/current", "2025-01-01", token);
        foreach (var (native, id) in new[] { (host, target), (auth, target + "/authconfigs/current") })
        {
            var observed = FactoryHostedWorkloadPolicy.ObserveExactHost(native, id);
            var effect = effects.Single(e => e.Target == id);
            if (observed.Revision != effect.NativeRevision || observed.Incarnation != effect.Incarnation) throw Changed("PreviewHostChanged");
            RequireIncarnation(native, effect);
        }
        var model = kind == PreviewHostKind.ModelEvaluation ? capability.Configuration.ModelEvaluation : null;
        var projection = kind == PreviewHostKind.TelemetryEvidence ? capability.Configuration.Telemetry : null;
        var optional = capability.Configuration.Supplemental?.Hosts.SingleOrDefault(h => FactoryPreviewSupplementalValidation.HostKind(h.Kind) == kind);
        IReadOnlyList<string> callers = kind == PreviewHostKind.Council
            ? new[] { manifest.Identity.AzureActorId }.Concat(capability.Configuration.Supplemental?.Hosts.Select(h => h.PrincipalId) ?? []).ToArray()
            : [bindings["council"].PrincipalId!];
        var optionalIdentity = optional is null ? null
            : supplementalEffects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(supplemental!.Configuration!, optional.Kind));
        var extra = optional is null ? null : new Dictionary<string, string>
        {
            ["DSF_SUPPLEMENTAL_CONFIG"] = JsonSerializer.Serialize(FactorySupplementalValidation.HostConfiguration(manifest, supplemental!,
                optionalIdentity!, optional.Kind), FactoryGitHubJson.Options),
            ["DSF_PREVIEW_COORDINATOR"] = JsonSerializer.Serialize(new PreviewSupplementalCoordinator(coordinatorEndpoint!,
                capability.Configuration.AudienceClientId, bindings["council"].PrincipalId!, bindings["council"].ClientId!), FactoryGitHubJson.Options),
        };
        return FactoryHostedWorkloadPolicy.Validate(host, auth,
            optionalIdentity ?? projection?.SourceIdentity ?? bindings[kind == PreviewHostKind.SourceContext ? "code" : "council"], bindings["hosting"],
            optional?.RuntimeImage ?? projection?.RuntimeImage ?? model?.RuntimeImage ?? capability.Configuration.RuntimeImage, kind switch
            {
                PreviewHostKind.Council => "serve-preview", PreviewHostKind.SourceContext => "serve-source-context",
                PreviewHostKind.ModelEvaluation => "serve-preview-models",
                PreviewHostKind.TelemetryEvidence => "serve-preview-telemetry",
                PreviewHostKind.FoundryEvidence or PreviewHostKind.WebEvidence => "serve-preview-supplemental",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            Product, ProductEndpoint, manifest.ProfilePin, manifest.Identity.TenantId,
            optional?.AudienceClientId ?? projection?.AudienceClientId ?? model?.AudienceClientId ?? capability.Configuration.AudienceClientId, callers, extra);
    }

    private void RequireIncarnation(JsonElement native, FoundationOperation binding)
    {
        if (!Same(native.GetProperty("id").GetString(), binding.Target)) throw Changed("PreviewResourceIdentityChanged");
        if (binding.Incarnation!.StartsWith("etag-sha256:", StringComparison.Ordinal))
        {
            if (binding.HttpStatus != 201 || native.GetProperty("etag").GetString() is not { Length: > 0 } etag
                || binding.Incarnation != "etag-sha256:" + TextHash(etag)) throw Changed("PreviewResourceEtagChanged");
        }
        else if (binding.Incarnation.StartsWith("cosmos-rid-sha256:", StringComparison.Ordinal))
        {
            var rid = native.GetProperty("properties").GetProperty("resource").GetProperty("_rid").GetString()!;
            var size = binding.Target.Contains("/containers/", StringComparison.Ordinal) ? 8 : 4;
            var decoded = Convert.FromBase64String(rid.Replace('-', '/'));
            if (decoded.Length != size || Convert.ToBase64String(decoded) != rid.Replace('-', '/')
                || binding.Incarnation != "cosmos-rid-sha256:" + TextHash(rid)) throw Changed("PreviewStateIncarnationChanged");
        }
        else
        {
            var system = native.GetProperty("systemData");
            var created = system.GetProperty("createdAt").GetDateTimeOffset();
            if (created != DateTimeOffset.Parse(binding.Incarnation!, CultureInfo.InvariantCulture) || created > clock.GetUtcNow()
                || system.TryGetProperty("lastModifiedAt", out var modified) && modified.GetDateTimeOffset() != created)
                throw Changed("PreviewResourceIncarnationChanged");
        }
    }

    private static string TextHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void RequireContainer(JsonElement native, string name)
    {
        var resource = native.GetProperty("properties").GetProperty("resource");
        var partition = resource.GetProperty("partitionKey");
        if (resource.GetProperty("id").GetString() != name || partition.GetProperty("kind").GetString() != "Hash"
            || partition.GetProperty("version").GetInt32() != 2) throw Changed("PreviewStatePartitionChanged");
        Exact(partition, "paths", ["/product"]);
    }

    private void RequireAssignment(JsonElement native, FoundationOperation effect, string scope, string role, string principal)
    {
        RequirePropertiesRevision(native, effect);
        var p = native.GetProperty("properties");
        if (!Same(p.GetProperty("scope").GetString(), scope) || !Same(p.GetProperty("roleDefinitionId").GetString(), role)
            || p.GetProperty("principalId").GetString() != principal || p.GetProperty("principalType").GetString() != "ServicePrincipal"
            || p.GetProperty("createdBy").GetString() != manifest.Identity.AzureActorId
            || p.GetProperty("createdOn").GetDateTimeOffset() != DateTimeOffset.Parse(effect.Incarnation!, CultureInfo.InvariantCulture)
            || p.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
            || p.TryGetProperty("conditionVersion", out var version) && version.ValueKind != JsonValueKind.Null && version.GetString() != "2.0")
            throw Changed("ExactPreviewReadGrantRequired");
    }

    private static void RequirePropertiesRevision(JsonElement native, FoundationOperation effect)
    {
        if (!Same(native.GetProperty("id").GetString(), effect.Target)
            || FactoryModelsValidation.Hash(native.GetProperty("properties")) != effect.NativeRevision) throw Changed("PreviewNativeResourceRevisionChanged");
    }
    private static void Exact(JsonElement value, string name, IReadOnlyList<string> expected)
    {
        if (!FactoryModelsValidation.Strings(value, name).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw Changed("PreviewNativePermissionOrScopeChanged");
    }
    private static JsonElement[] Complete(JsonElement value)
    {
        if (value.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException("Incomplete native preview authority collection.");
        return value.GetProperty("value").EnumerateArray().ToArray();
    }
    private async Task<JsonElement> GetAsync(string target, string api, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + target + (target.Contains('?') ? "&" : "?") + "api-version=" + api);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK) throw new PreviewCapabilityException(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
                ? FactoryCharterState.Blocked : FactoryCharterState.Unknown, "CurrentPreviewNativeReadUnavailable");
        using var document = await EvidencePayload.ReadAsync(response, token);
        var value = document.RootElement.Clone();
        Revisions[target] = FactoryModelsValidation.Hash(value);
        return value;
    }
    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static PreviewCapabilityException Changed(string reason) => new(FactoryCharterState.Blocked, reason);
}
