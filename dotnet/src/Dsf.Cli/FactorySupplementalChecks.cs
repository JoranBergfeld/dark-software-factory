using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactorySupplementalService
{
    private async Task CheckNativeAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var config = current.Configuration!;
        FactorySupplementalValidation.ValidateSelection(config.Selection, admission.Identity!.SubscriptionId);
        foreach (var binding in new[] { config.Hosting, config.Vault }.OfType<FoundationOperation>())
        {
            using var observed = await GetAsync(admission, binding.Target, binding.ApiVersion, token);
            var native = observed.RootElement;
            if (!Same(native.GetProperty("id").GetString(), binding.Target)
                || Created(native) != DateTimeOffset.Parse(binding.Incarnation!, System.Globalization.CultureInfo.InvariantCulture))
                throw new JsonException();
            var properties = native.GetProperty("properties");
            if (binding == config.Hosting && properties.GetProperty("vnetConfiguration").GetProperty("internal").ValueKind != JsonValueKind.True
                || binding == config.Vault && (properties.GetProperty("enableRbacAuthorization").ValueKind != JsonValueKind.True
                    || properties.GetProperty("publicNetworkAccess").GetString() != "Disabled" || properties.GetProperty("accessPolicies").GetArrayLength() != 0))
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Factory hosting/vault network or authorization changed. Factory network/Key Vault administrator must inspect; no public access relaxation or network repair.");
        }
        foreach (var source in config.Selection.Sources)
        {
            if (source.Kind == "foundryiq")
            {
                using var search = await GetAsync(admission, source.SearchResourceId!, "2025-05-01", token);
                var properties = search.RootElement.GetProperty("properties");
                if (!Same(search.RootElement.GetProperty("id").GetString(), source.SearchResourceId)
                    || properties.GetProperty("disableLocalAuth").ValueKind != JsonValueKind.True
                    || properties.GetProperty("publicNetworkAccess").GetString() != "Disabled"
                    || !config.PrerequisiteRevisions.TryGetValue(source.SearchResourceId!, out var revision)
                    || revision != FactoryTelemetryValidation.Hash(search.RootElement))
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Existing Search native identity/network differs from review. Search administrator must establish private workload connectivity, native RBAC and exact source-data access; no service deployment or repair.");
            }
            var grants = FactorySupplementalValidation.Grants(config, source);
            foreach (var grant in grants)
            {
                await RoleAsync(admission, grant, token);
                if (!effects.Any(e => e.Target == grant.Target)) await DelegationAsync(admission, grant.Scope, "Microsoft.Authorization/roleAssignments/write", token);
            }
            var identity = effects.SingleOrDefault(e => e.Target == FactorySupplementalValidation.IdentityTarget(config, source.Kind));
            if (identity is null)
            {
                await DelegationAsync(admission, config.FactoryGroup, "Microsoft.ManagedIdentity/userAssignedIdentities/write", token);
                await DelegationAsync(admission, config.FactoryGroup, "Microsoft.Resources/deployments/write", token);
                await DelegationAsync(admission, config.FactoryGroup, "Microsoft.App/containerApps/write", token);
                await DelegationAsync(admission, config.FactoryGroup, "Microsoft.App/containerApps/authConfigs/write", token);
                continue;
            }
            if (admission.Foundation!.Operations.Any(o => o.PrincipalId is not null && o.PrincipalId == identity.PrincipalId)
                || effects.Any(e => e != identity && e.Target.Contains("/userassignedidentities/", StringComparison.Ordinal)
                    && e.PrincipalId == identity.PrincipalId)) throw new JsonException();
            var paths = new[] { "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roleassignments?$filter=assignedTo('" + identity.PrincipalId + "')" }
                .Concat(grants.Select(g => g.Scope + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + identity.PrincipalId + "')"))
                .Append(config.FactoryGroup + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + identity.PrincipalId + "')");
            foreach (var path in paths)
            {
                using var response = await GetAsync(admission, path, "2022-04-01", token);
                foreach (var assignment in Complete(response.RootElement))
                {
                    var effect = effects.SingleOrDefault(e => Same(e.Target, assignment.GetProperty("id").GetString()));
                    var grant = grants.SingleOrDefault(g => Same(g.Target, assignment.GetProperty("id").GetString()));
                    if (effect is null || grant is null || Observe(effect, assignment, grant).NativeRevision != effect.NativeRevision)
                        throw new ReservationStoreException(FactoryReservationState.Blocked,
                            "Source effective inherited/transitive/preexisting grants exceed exact receipt-backed scopes. RBAC/directory administrator must reconcile; no signing-key, app-write, model, council/configuration, intent or repository-store authority permitted.");
                }
            }
        }
        foreach (var effect in effects)
        {
            var grant = config.Selection.Sources.SelectMany(s => FactorySupplementalValidation.Grants(config, s)).SingleOrDefault(g => g.Target == effect.Target);
            using var observed = await GetAsync(admission, effect.Target, effect.ApiVersion, token);
            if (Observe(effect, observed.RootElement, grant).NativeRevision != effect.NativeRevision)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Source native identity/grant/host incarnation changed; no adoption or authority replacement.");
        }
        foreach (var source in config.Selection.Sources)
        {
            var identity = effects.SingleOrDefault(e => e.Target == FactorySupplementalValidation.IdentityTarget(config, source.Kind));
            if (identity is not null && effects.Any(e => e.Target == FactorySupplementalValidation.HostTarget(config, source.Kind) + "/authconfigs/current"))
                await HostAsync(admission, source, identity, token);
        }
    }

    private FoundationOperation Observe(FoundationOperation operation, JsonElement native, SupplementalGrant? grant)
    {
        if (!Same(native.GetProperty("id").GetString(), operation.Target)) throw new JsonException();
        var properties = native.GetProperty("properties");
        DateTimeOffset created;
        string? principal = null;
        string? client = null;
        string revision;
        if (grant is not null)
        {
            principal = properties.GetProperty("principalId").GetString();
            var kind = operation.LogicalIdentity!["factory:supplemental-".Length..];
            var identity = effects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(current.Configuration!, kind));
            if (principal != identity.PrincipalId || properties.GetProperty("principalType").GetString() != "ServicePrincipal"
                || !Same(properties.GetProperty("scope").GetString(), grant.Scope)
                || !Same(properties.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId)
                || properties.GetProperty("createdBy").GetString() != manifest.Identity.AzureActorId
                || properties.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
                || properties.TryGetProperty("conditionVersion", out var version) && version.ValueKind != JsonValueKind.Null && version.GetString() != "2.0")
                throw new JsonException();
            created = properties.GetProperty("createdOn").GetDateTimeOffset();
            revision = FactoryTelemetryValidation.Hash(properties);
        }
        else
        {
            created = Created(native);
            if (operation.Target.Contains("/userassignedidentities/", StringComparison.Ordinal))
            {
                principal = Guid.Parse(properties.GetProperty("principalId").GetString()!).ToString();
                client = Guid.Parse(properties.GetProperty("clientId").GetString()!).ToString();
                if (principal == Guid.Empty.ToString() || client == Guid.Empty.ToString() || principal == manifest.Identity.AzureActorId
                    || properties.GetProperty("tenantId").GetString() != manifest.Identity.TenantId) throw new JsonException();
                revision = FactoryTelemetryValidation.Hash(JsonSerializer.SerializeToElement(new { created, principal, client, tenant = manifest.Identity.TenantId, target = operation.Target }));
            }
            else revision = FactoryHostedWorkloadPolicy.ObserveExactHost(native, operation.Target).Revision;
        }
        if (created < operation.StartedAt || created > clock.GetUtcNow()
            || operation.Incarnation is not null && created != DateTimeOffset.Parse(operation.Incarnation, System.Globalization.CultureInfo.InvariantCulture))
            throw new JsonException();
        return operation with { State = FoundationOperationState.Completed, Disposition = FoundationResourceDisposition.Created, HttpStatus = 201,
            ResourceId = operation.Target, Incarnation = created.ToString("O"), NativeRevision = revision, PrincipalId = principal, ClientId = client,
            RoleScope = grant?.Scope, RoleDefinitionId = grant?.RoleDefinitionId };
    }

    private async Task<string> HostAsync(FactoryReservationResult admission, SupplementalSourceSelection source, FoundationOperation identity, CancellationToken token)
    {
        var config = current.Configuration!;
        var target = FactorySupplementalValidation.HostTarget(config, source.Kind);
        using var native = await GetAsync(admission, target, "2025-01-01", token);
        using var policy = await GetAsync(admission, target + "/authconfigs/current", "2025-01-01", token);
        return FactoryHostedWorkloadPolicy.Validate(native.RootElement, policy.RootElement, identity, config.Hosting,
            config.Selection.RuntimeImage, "serve-supplemental", manifest.Identity.ProductKey, config.ProductEndpoint, config.ProfilePin,
            manifest.Identity.TenantId, config.Selection.AudienceClientId, [manifest.Identity.AzureActorId],
            new Dictionary<string, string> { ["DSF_SUPPLEMENTAL_CONFIG"] = FactoryReservationRecords.Serialize(
                FactorySupplementalValidation.HostConfiguration(manifest, current, identity, source.Kind)) });
    }
    private async Task RoleAsync(FactoryReservationResult admission, SupplementalGrant grant, CancellationToken token)
    {
        using var role = await GetAsync(admission, grant.RoleDefinitionId, "2022-04-01", token);
        var native = role.RootElement;
        var permissions = native.GetProperty("properties").GetProperty("permissions").EnumerateArray().ToArray();
        var secret = grant.RoleDefinitionId.EndsWith("/" + FactorySupplementalValidation.SecretsRole, StringComparison.Ordinal);
        var data = grant.RoleDefinitionId.EndsWith("/" + FactorySupplementalValidation.SearchDataRole, StringComparison.Ordinal);
        if (!Same(native.GetProperty("id").GetString(), grant.RoleDefinitionId)
            || native.GetProperty("properties").GetProperty("type").GetString() != (secret || data ? "BuiltInRole" : "CustomRole")
            || permissions.Length != 1 || !Exact(permissions[0], "actions", grant.Actions) || !Exact(permissions[0], "dataActions", grant.DataActions)
            || !Exact(permissions[0], "notActions", []) || !Exact(permissions[0], "notDataActions", []))
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "RBAC administrator: existing exact source-only role required. Search metadata reads only plus separate Search Index Data Reader; WebIQ Secrets User only at product webiq-api-key secret scope. No role creation, contributor wildcard or broad grants.");
        static bool Exact(JsonElement permission, string name, IReadOnlyList<string> expected) =>
            FactoryModelsValidation.Strings(permission, name).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }
    private async Task DelegationAsync(FactoryReservationResult admission, string scope, string action, CancellationToken token)
    {
        using var permissions = await GetAsync(admission, scope + "/providers/microsoft.authorization/permissions", "2022-04-01", token);
        using var denies = await GetAsync(admission, scope + "/providers/microsoft.authorization/denyassignments", "2022-04-01", token);
        if (Complete(denies.RootElement).Length != 0 || !Complete(permissions.RootElement).Any(p =>
            FactoryModelsValidation.Strings(p, "actions").Any(a => Matches(a, action))
            && !FactoryModelsValidation.Strings(p, "notActions").Any(a => Matches(a, action))))
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Azure grantor: approve only " + action + " at " + scope + "; discovery success is not delegation.");
        static bool Matches(string pattern, string value) => Regex.IsMatch(value,
            "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);
    }
    private static DateTimeOffset Created(JsonElement native)
    {
        var system = native.GetProperty("systemData");
        var created = system.GetProperty("createdAt").GetDateTimeOffset();
        if (system.TryGetProperty("lastModifiedAt", out var modified) && modified.GetDateTimeOffset() != created) throw new JsonException();
        return created;
    }
    private async Task AbsentAsync(FactoryReservationResult admission, string target, string api, CancellationToken token)
    {
        using var response = await Arm(admission).SendAsync(HttpMethod.Get, target, api, token);
        if (response.StatusCode != HttpStatusCode.NotFound)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Exact source target is not proven absent; no adoption, overwrite or recreate.");
    }
    private async Task<JsonDocument> GetAsync(FactoryReservationResult admission, string target, string api, CancellationToken token)
    {
        using var response = await Arm(admission).SendAsync(HttpMethod.Get, target, api, token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Required source native metadata unavailable; Azure RBAC/resource administrator must inspect " + target.Split('?')[0] + ". No repair.");
        return await DocumentAsync(response, token);
    }
    private static Task<JsonDocument> DocumentAsync(HttpResponseMessage response, CancellationToken token, int limit = EvidencePayload.MaximumBytes) =>
        EvidencePayload.ReadAsync(response, token, limit);
    private static JsonElement[] Complete(JsonElement native)
    {
        if (native.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException("Incomplete native collection.");
        return native.GetProperty("value").EnumerateArray().ToArray();
    }

    private static async Task<IReadOnlyList<FoundationOperation>> ReadEffectsAsync(FactorySupplemental value, OwnerReservationStore store, CancellationToken token)
    {
        var targets = FactorySupplementalValidation.Targets(value.Configuration!);
        if (value.OperationCount != targets.Count || value.EffectHashes.Count != targets.Count) throw new JsonException();
        var effects = new List<FoundationOperation>();
        for (var i = 0; i < targets.Count; i++)
        {
            var record = await store.ReadAsync(ProductConfigurationKeys.SupplementalOperation(value.OperationId, i), token) ?? throw new JsonException();
            var effect = FactoryReservationRecords.Parse<FoundationOperation>(record.Value);
            if (effect.SchemaVersion != 1 || effect.Kind != FactorySupplementalValidation.OperationKind || effect.FactoryId != value.Configuration!.FactoryId
                || effect.OperationId != value.OperationId || effect.Index != i || effect.Target != targets[i].Target || effect.ApiVersion != targets[i].Api
                || effect.State != FoundationOperationState.Completed || effect.Disposition != FoundationResourceDisposition.Created
                || effect.ResourceId != effect.Target || effect.Incarnation is null || effect.NativeRevision is not { Length: 64 }
                || effect.HttpStatus != 201 || effect.ClientRequestId == Guid.Empty || effect.StartedAt == default
                || !Guid.TryParse(effect.ProviderRequestId, out var request) || request == Guid.Empty
                || !Guid.TryParse(effect.ProviderCorrelationId, out var correlation) || correlation == Guid.Empty
                || FactoryTelemetryValidation.Hash(effect) != value.EffectHashes[i]) throw new JsonException();
            effects.Add(effect);
        }
        return effects;
    }
    internal static async Task<FactoryReservationResult> InspectAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var manifestRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token) ?? throw new JsonException();
        var profile = await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token);
        var manifest = FactoryGovernance.ReadInactiveManifest(manifestRecord.Value, profile?.Value, admission.Identity!.ProductKey,
            admission.Foundation!.ProductEndpoint!, admission.Foundation.ProfilePin);
        var root = await store.ReadAsync(ProductConfigurationKeys.FactorySupplemental, token);
        if (root is null && manifest.SupplementalOperationId is null)
            return admission;
        if (root is null) throw new JsonException("Selected supplemental record missing; no Azure-only fallback.");
        var selected = FactorySupplementalValidation.Read(manifest, root.Value);
        if (selected.Stage is "prepared" or "checking" or "observed")
        {
            _ = await ReadEffectsAsync(selected, store, token);
            foreach (var check in selected.Checks)
            {
                if (check.ProofKey != ProductConfigurationKeys.SupplementalProof(selected.OperationId, selected.CheckId!.Value, check.Kind))
                    throw new JsonException();
                var proof = FactoryReservationRecords.Parse<SupplementalProof>((await store.ReadAsync(check.ProofKey, token))?.Value ?? throw new JsonException());
                if (FactoryTelemetryValidation.Hash(proof) != check.ProofSha256 || proof.Kind != check.Kind
                    || proof.CheckId != selected.CheckId || proof.ConfigurationRevision != selected.ApprovalFingerprint
                    || proof.State != check.State || proof.Reason != check.Reason) throw new JsonException();
            }
            if (selected.State == SupplementalState.ReadySupplemental && (!selected.Checks.Select(c => c.Kind).SequenceEqual(selected.Configuration!.Selection.Sources.Select(s => s.Kind))
                || selected.Checks.Any(c => c.State != SupplementalState.ReadySupplemental))) throw new JsonException();
        }
        else if (selected.State == SupplementalState.Partial) selected = selected with { State = SupplementalState.Unknown };
        return admission with { Supplemental = selected, Detail = "Supplemental observations are historical; --verify-only checks all current source authority. " + admission.Detail };
    }
    private static void CheckCapacity(FactorySupplemental value, FactoryManifest manifest)
    {
        var targets = FactorySupplementalValidation.Targets(value.Configuration!);
        var hash = new string('f', 64);
        var checks = value.Configuration!.Selection.Sources.Select(s => new SupplementalCheck(s.Kind, SupplementalState.ReadySupplemental,
            SupplementalReason.MalformedResponse, ProductConfigurationKeys.SupplementalProof(value.OperationId, Guid.Empty, s.Kind), hash)).ToArray();
        Check(ProductConfigurationKeys.FactorySupplemental, value with { OperationCount = targets.Count, EffectHashes = targets.Select(_ => hash).ToArray(),
            State = SupplementalState.ReadySupplemental, CheckId = Guid.Empty, Checks = checks, Stage = "preparation", Detail = new string('x', 1024) });
        Check(ProductConfigurationKeys.FactoryManifest, manifest with
        {
            SupplementalOperationId = Guid.Empty, SupplementalApprovalFingerprint = hash,
            GitHubAuthorityOperationId = Guid.Empty, GitHubAuthorityApprovalFingerprint = hash,
            TelemetryOperationId = Guid.Empty, TelemetryApprovalFingerprint = hash, ModelsOperationId = Guid.Empty, ModelsApprovalFingerprint = hash,
            SourceOperationId = Guid.Empty, SourceApprovalFingerprint = hash,
        });
        foreach (var (target, api) in targets)
            Check(ProductConfigurationKeys.SupplementalOperation(value.OperationId, 9),
                new FoundationOperation(1, FactorySupplementalValidation.OperationKind, value.Configuration.FactoryId, value.OperationId, 9, target, api,
                    hash, Guid.Empty, DateTimeOffset.MaxValue, FoundationOperationState.Completed, FoundationResourceDisposition.CreateRequested)
                {
                    ResourceId = target, Incarnation = DateTimeOffset.MaxValue.ToString("O"), NativeRevision = hash, PrincipalId = Guid.Empty.ToString(),
                    ClientId = Guid.Empty.ToString(), ProviderRequestId = Guid.Empty.ToString(), ProviderCorrelationId = Guid.Empty.ToString(),
                    ProviderOperationId = new string('f', 128), LogicalIdentity = "factory:supplemental-foundryiq", RoleScope = target, RoleDefinitionId = target,
                    DeploymentTarget = value.Configuration.FactoryGroup + "/providers/microsoft.resources/deployments/sp-" + value.OperationId.ToString("N") + "-foundryiq",
                });
        foreach (var check in checks)
            Check(check.ProofKey, new SupplementalProof(value.Configuration.FactoryId, Guid.Empty, hash, check.Kind, Guid.Empty.ToString(),
                SupplementalState.ReadySupplemental, SupplementalReason.MalformedResponse, DateTimeOffset.MaxValue,
                Enumerable.Range(0, 16).Select(_ => new SupplementalEvidenceReference(new string('x', 320), hash)).ToArray(), hash, new string('f', 32)));
        static void Check<T>(string key, T record) => OwnerReservationStore.CheckSize(key, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(record));
    }
}
