using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryTelemetryService
{
    private async Task CheckBindingsAsync(FactoryReservationResult admission, FactoryTelemetry selected, CancellationToken token)
    {
        var config = selected.Configuration;
        if (FactoryTelemetryValidation.Hash(config) != selected.ApprovalFingerprint
            || FactoryTelemetryValidation.Hash(config.AccessGrants) != FactoryTelemetryValidation.Hash(FactoryTelemetryValidation.Grants(config, admission.Identity!.SubscriptionId)))
            throw new JsonException();
        foreach (var binding in new[] { config.SourceIdentity, config.HostingEnvironment })
        {
            using var response = await GetAsync(admission, binding.Target, binding.ApiVersion, token);
            var value = response.RootElement;
            var created = value.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset();
            if (!Same(value.GetProperty("id").GetString(), binding.Target)
                || created != DateTimeOffset.Parse(binding.Incarnation!, CultureInfo.InvariantCulture)
                || value.GetProperty("systemData").TryGetProperty("lastModifiedAt", out var modified) && modified.GetDateTimeOffset() != created)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "GET " + binding.Target + ": foundation binding incarnation changed; factory administrator must reconcile.");
            var properties = value.GetProperty("properties");
            if (binding.PrincipalId is not null && (properties.GetProperty("principalId").GetString() != binding.PrincipalId
                || properties.GetProperty("clientId").GetString() != binding.ClientId
                || properties.GetProperty("tenantId").GetString() != admission.Identity!.TenantId))
                throw new ReservationStoreException(FactoryReservationState.Blocked, "GET " + binding.Target + ": actual source identity differs; no grant or proof.");
            if (binding == config.HostingEnvironment && properties.GetProperty("vnetConfiguration").GetProperty("internal").ValueKind != JsonValueKind.True)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Internal factory hosting required; factory network administrator must reconcile. No application networking changes.");
        }
        var reader = new AzureBoundaryReader(azure);
        var currentResources = new List<BoundaryResource>();
        foreach (var id in admission.Plan!.Selection.ApplicationResourceIds)
        {
            var raw = await reader.ReadAsync(["resource", "show", "--ids", id, "--subscription", admission.Identity!.SubscriptionId], token);
            var resource = AzureBoundaryReader.Resource(raw);
            if (!Same(resource.Id, id)) throw new JsonException();
            currentResources.Add(resource);
        }
        var tableModes = new List<EvidenceTableObservation>();
        foreach (var backend in config.Backends)
        {
            using var workspace = await GetAsync(admission, backend.ResourceId, "2022-10-01", token);
            var native = workspace.RootElement;
            var properties = native.GetProperty("properties");
            if (!Same(native.GetProperty("id").GetString(), backend.ResourceId)
                || properties.GetProperty("customerId").GetString() != backend.WorkspaceId)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Wrong scope: workspace resource/customer ID changed at " + backend.ResourceId + "; evidence administrator must reconcile.");
            if (properties.GetProperty("features").GetProperty("enableLogAccessUsingOnlyResourcePermissions").ValueKind != JsonValueKind.False)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Unsupported authentication: conventional workspace-context permission mode required at " + backend.ResourceId + ". No workspace-mode changes.");
            currentResources.Add(AzureBoundaryReader.Resource(native));
            using var tables = await GetAsync(admission, backend.ResourceId + "/tables", "2022-10-01", token);
            foreach (var table in CompleteValues(tables))
            {
                var name = table.GetProperty("name").GetString()!;
                if (backend.Tables.Contains(name, StringComparer.Ordinal))
                    tableModes.Add(new(backend.ResourceId, name, table.GetProperty("properties").GetProperty("plan").GetString()!));
            }
            foreach (var componentId in backend.ApplicationResourceIds)
            {
                var component = currentResources.Single(r => Same(r.Id, componentId));
                if (component.Type != "microsoft.insights/components" || !Same(component.BackingWorkspaceResourceId, backend.ResourceId))
                    throw new ReservationStoreException(FactoryReservationState.Blocked, "Wrong scope: current native Application Insights workspace mapping differs at " + componentId + ".");
            }
        }
        using var components = await GetAsync(admission, "/subscriptions/" + admission.Identity!.SubscriptionId + "/providers/microsoft.insights/components", "2020-02-02", token);
        var topology = CompleteValues(components).Select(AzureBoundaryReader.Resource).ToArray();
        var discovery = new AzureBoundaryDiscovery(admission.Identity.TenantId, admission.Identity.SubscriptionId,
            admission.Identity.AzureActorId, currentResources, [], []) { InspectedTopology = topology, EvidenceTables = tableModes };
        var failures = ApplicationBoundaryPolicy.Review(admission.Plan.Selection, discovery);
        if (failures.Count != 0)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "NOT READY: " + string.Join(" ", failures.Select(f => f.Detail))
                + " Application/evidence administrator must reconcile; no scope, tags, diagnostics or workspace changes.");
    }

    private async Task CheckIsolationAsync(FactoryReservationResult admission, FactoryTelemetry selected, CancellationToken token)
    {
        var config = selected.Configuration;
        var principal = config.SourceIdentity.PrincipalId!;
        var paths = new[] { "/subscriptions/" + admission.Identity!.SubscriptionId
            + "/providers/microsoft.authorization/roleassignments?$filter=assignedTo('" + principal + "')" }
            .Concat(config.AccessGrants.Select(g => g.Scope + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + principal + "')"));
        foreach (var path in paths)
        {
            using var assignments = await GetAsync(admission, path, "2022-04-01", token);
            foreach (var assignment in CompleteValues(assignments))
            {
                var id = assignment.GetProperty("id").GetString();
                var grant = config.AccessGrants.SingleOrDefault(g => Same(g.Target, id));
                if (grant is null || !effects.Any(e => Same(e.Target, id) && e.State == FoundationOperationState.Completed)
                    || !GrantMatches(assignment, grant, admission.Identity.AzureActorId))
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Ambiguously authorized source: inherited, group, preexisting or unrelated assignment at " + path.Split('?')[0]
                        + ". Azure RBAC/directory grantor must reconcile; access is not ownership and no assignment is adopted.");
            }
        }
        foreach (var grant in config.AccessGrants) await CheckRoleAsync(admission, grant, token);
    }

    private async Task CheckDelegationAsync(FactoryReservationResult admission, TelemetryAccessGrant grant, CancellationToken token)
    {
        using var permissions = await GetAsync(admission, grant.Scope + "/providers/microsoft.authorization/permissions", "2022-04-01", token);
        var allowed = CompleteValues(permissions).Any(p => Strings(p, "actions").Any(a => Matches(a, "Microsoft.Authorization/roleAssignments/write"))
            && !Strings(p, "notActions").Any(a => Matches(a, "Microsoft.Authorization/roleAssignments/write")));
        using var denies = await GetAsync(admission, grant.Scope + "/providers/microsoft.authorization/denyassignments", "2022-04-01", token);
        if (!allowed || CompleteValues(denies).Length != 0)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Azure grantor: separately delegate exact roleAssignments/write at " + grant.Scope + " for principal " + grant.PrincipalId
                + ", role " + grant.RoleDefinitionId + ", no condition. Inspection/deployment authority is not delegation; restrictions remain outside DSF.");
    }

    private async Task CheckRoleAsync(FactoryReservationResult admission, TelemetryAccessGrant grant, CancellationToken token)
    {
        using var role = await GetAsync(admission, grant.RoleDefinitionId, "2022-04-01", token);
        var rp = role.RootElement.GetProperty("properties");
        if (!Same(role.RootElement.GetProperty("id").GetString(), grant.RoleDefinitionId) || rp.GetProperty("type").GetString() != "BuiltInRole")
            throw new JsonException();
        foreach (var permission in rp.GetProperty("permissions").EnumerateArray())
        {
            var logs = grant.RoleDefinitionId.EndsWith("73c42c96-874c-492b-b04d-ab87d138a893", StringComparison.Ordinal);
            var actions = Strings(permission, "actions");
            var data = Strings(permission, "dataActions");
            if (logs && (data.Any(a => !a.Equals("Microsoft.OperationalInsights/workspaces/jobs/export/action", StringComparison.OrdinalIgnoreCase))
                || !Strings(permission, "notActions").Contains("Microsoft.OperationalInsights/workspaces/sharedKeys/read", StringComparer.OrdinalIgnoreCase)
                || actions.Any(a => !new[] { "*/read",
                "Microsoft.OperationalInsights/workspaces/analytics/query/action", "Microsoft.OperationalInsights/workspaces/search/action",
                "Microsoft.Support/*" }.Contains(a, StringComparer.OrdinalIgnoreCase)))
                || !logs && (actions.Length != 0 || data.Any(a => !a.StartsWith("Microsoft.AppConfiguration/configurationStores/", StringComparison.OrdinalIgnoreCase)
                    || !a.EndsWith("/read", StringComparison.OrdinalIgnoreCase))))
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Unsupported current role definition; Azure RBAC grantor must review exact backend/config read-only authority.");
        }
    }

    private async Task CheckEffectsAsync(FactoryReservationResult admission, CancellationToken token)
    {
        foreach (var effect in effects)
        {
            using var current = await GetAsync(admission, effect.Target, effect.ApiVersion, token);
            var actual = current.RootElement;
            if (!Same(actual.GetProperty("id").GetString(), effect.Target)) throw new JsonException();
            var incarnation = DateTimeOffset.Parse(effect.Incarnation!, CultureInfo.InvariantCulture);
            if (effect.RoleScope is not null)
            {
                var grant = authority!.Configuration.AccessGrants.Single(g => Same(g.Target, effect.Target));
                var properties = actual.GetProperty("properties");
                if (!GrantMatches(actual, grant, admission.Identity!.AzureActorId)
                    || properties.GetProperty("createdOn").GetDateTimeOffset() != incarnation
                    || Optional(properties, "conditionVersion") != effect.ConditionVersion)
                    throw new ReservationStoreException(FactoryReservationState.Blocked, "GET " + effect.Target
                        + ": exact approved grant binding/incarnation changed. Azure RBAC grantor must reconcile; working access does not authorize adoption or replacement.");
            }
            else if (actual.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset() != incarnation
                || actual.GetProperty("systemData").GetProperty("lastModifiedAt").GetDateTimeOffset() != incarnation)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "GET " + effect.Target + ": source host incarnation/policy changed; no current readiness.");
        }
    }

    private async Task<string> CheckHostAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var config = authority!.Configuration;
        var target = FactoryTelemetryValidation.HostTarget(config);
        using var host = await GetAsync(admission, target, "2025-01-01", token);
        var native = host.RootElement;
        var identity = native.GetProperty("identity");
        var properties = native.GetProperty("properties");
        var configuration = properties.GetProperty("configuration");
        var ingress = configuration.GetProperty("ingress");
        var containers = properties.GetProperty("template").GetProperty("containers").EnumerateArray().ToArray();
        var expectedEnv = new Dictionary<string, string>
        {
            ["AZURE_CLIENT_ID"] = config.SourceIdentity.ClientId!, ["DSF_PRODUCT"] = admission.Identity!.ProductKey,
            ["AZURE_APPCONFIG_ENDPOINT"] = config.ProductEndpoint, ["DSF_FACTORY_PROFILE_PIN"] = admission.Foundation!.ProfilePin!,
        };
        if (identity.GetProperty("type").GetString() != "UserAssigned"
            || !identity.GetProperty("userAssignedIdentities").EnumerateObject().Select(p => p.Name).SequenceEqual([config.SourceIdentity.Target], StringComparer.OrdinalIgnoreCase)
            || !Same(properties.GetProperty("managedEnvironmentId").GetString(), config.HostingEnvironment.Target)
            || configuration.GetProperty("activeRevisionsMode").GetString() != "Single"
            || configuration.TryGetProperty("secrets", out var secrets) && secrets.GetArrayLength() != 0
            || ingress.GetProperty("external").ValueKind != JsonValueKind.True || ingress.GetProperty("allowInsecure").ValueKind != JsonValueKind.False
            || ingress.GetProperty("targetPort").GetInt32() != 8080 || containers.Length != 1
            || containers[0].GetProperty("image").GetString() != config.RuntimeImage
            || !Strings(containers[0], "command").SequenceEqual(["dotnet", "dsf-runtime.dll"])
            || !Strings(containers[0], "args").SequenceEqual(["serve-telemetry"]))
            throw new JsonException();
        var env = containers[0].GetProperty("env").EnumerateArray().ToArray();
        if (env.Length != expectedEnv.Count || env.Select(e => e.GetProperty("name").GetString()).Distinct().Count() != env.Length
            || env.Any(e => !expectedEnv.TryGetValue(e.GetProperty("name").GetString()!, out var expected)
                || Optional(e, "secretRef") is not null || e.GetProperty("value").GetString() != expected))
            throw new JsonException();
        using var policy = await GetAsync(admission, target + "/authconfigs/current", "2025-01-01", token);
        var p = policy.RootElement.GetProperty("properties");
        var global = p.GetProperty("globalValidation");
        var aad = p.GetProperty("identityProviders").GetProperty("azureActiveDirectory");
        var registration = aad.GetProperty("registration");
        var validation = aad.GetProperty("validation");
        var principals = validation.GetProperty("defaultAuthorizationPolicy").GetProperty("allowedPrincipals");
        if (p.GetProperty("platform").GetProperty("enabled").ValueKind != JsonValueKind.True
            || global.GetProperty("unauthenticatedClientAction").GetString() != "Return401"
            || global.TryGetProperty("excludedPaths", out var excluded) && excluded.GetArrayLength() != 0
            || p.GetProperty("httpSettings").GetProperty("requireHttps").ValueKind != JsonValueKind.True
            || p.GetProperty("login").GetProperty("tokenStore").GetProperty("enabled").ValueKind != JsonValueKind.False
            || aad.GetProperty("enabled").ValueKind != JsonValueKind.True
            || registration.GetProperty("clientId").GetString() != config.AudienceClientId
            || registration.GetProperty("openIdIssuer").GetString() != "https://login.microsoftonline.com/" + admission.Identity.TenantId + "/v2.0"
            || !Strings(principals, "identities").SequenceEqual([admission.Identity.AzureActorId])
            || principals.TryGetProperty("groups", out var groups) && groups.GetArrayLength() != 0
            || !Strings(validation, "allowedAudiences").Order().SequenceEqual(new[] { config.AudienceClientId, "api://" + config.AudienceClientId }.Order()))
            throw new ReservationStoreException(FactoryReservationState.Blocked, "GET " + target + "/authconfigs/current: exact native Entra/operator isolation is not proven; identity administrator must reconcile.");
        var fqdn = ingress.GetProperty("fqdn").GetString();
        if (fqdn is null || !Regex.IsMatch(fqdn, "^[a-z0-9.-]+\\.azurecontainerapps\\.io$")) throw new JsonException();
        return "https://" + fqdn;
    }

    private static bool GrantMatches(JsonElement value, TelemetryAccessGrant grant, string actor)
    {
        var properties = value.GetProperty("properties");
        return Same(value.GetProperty("id").GetString(), grant.Target)
            && Same(properties.GetProperty("principalId").GetString(), grant.PrincipalId)
            && properties.GetProperty("principalType").GetString() == "ServicePrincipal"
            && Same(properties.GetProperty("scope").GetString(), grant.Scope)
            && Same(properties.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId)
            && properties.GetProperty("createdBy").GetString() == actor
            && Optional(properties, "condition") is null
            && Optional(properties, "conditionVersion") is null or "2.0";
    }
    private static string[] Strings(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().Select(v => v.GetString() ?? throw new JsonException()).ToArray();
    private static bool Matches(string pattern, string action) =>
        Regex.IsMatch(action, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);

    private void ValidateProof(TelemetryProof proof)
    {
        var config = authority!.Configuration;
        var now = clock.GetUtcNow();
        if (!Enum.IsDefined(proof.State) || !Enum.IsDefined(proof.Reason) || proof.FactoryId != config.FactoryId
            || proof.OperationId != authority.OperationId || proof.ConfigurationRevision != authority.ApprovalFingerprint
            || proof.CheckId != authority.CheckId || proof.CheckId is null
            || proof.SourcePrincipalId != config.SourceIdentity.PrincipalId || proof.ApplicationEnvironment != config.ApplicationEnvironment
            || proof.ObservedAt < now.AddMinutes(-5) || proof.ObservedAt > now.AddMinutes(1)
            || proof.WindowEnd > proof.ObservedAt || proof.WindowEnd < now.AddMinutes(-5)
            || proof.WindowEnd - proof.WindowStart != TimeSpan.FromSeconds(config.FreshnessSeconds)
            || proof.CoverageLimitations != FactoryTelemetryValidation.Coverage || proof.AttributionBasis != FactoryTelemetryValidation.Attribution)
            throw new JsonException();
        if (proof.State != TelemetryState.Ready) return;
        if (proof.Reason != TelemetryReason.None || proof.Queries.Count != config.Backends.Sum(b => b.Tables.Count)
            || proof.Queries.Any(q => q.Reason is not (TelemetryReason.None or TelemetryReason.Empty or TelemetryReason.Stale)))
            throw new JsonException();
        foreach (var backend in config.Backends)
        {
            if (!proof.Records.Any(r => r.BackendResourceId == backend.ResourceId)) throw new JsonException();
            foreach (var table in backend.Tables)
            {
                var query = proof.Queries.Single(q => q.BackendResourceId == backend.ResourceId && q.Table == table);
                if (query.QuerySha256 != FactoryTelemetryValidation.Hash(config.Queries[table])) throw new JsonException();
                var observed = proof.Records.Where(r => r.BackendResourceId == backend.ResourceId && r.Table == table).ToArray();
                if (query.Reason == TelemetryReason.None)
                {
                    if (observed.Length != 1 || query.Records != observed[0].ObservedRecords
                        || query.Records is < 1 or >= 500000 || query.LatestRecord is null
                        || FactoryTelemetryValidation.Hash(query.LatestRecord) != FactoryTelemetryValidation.Hash(observed[0])) throw new JsonException();
                }
                else if (observed.Length != 0 || query.Records != 0
                    || query.Reason == TelemetryReason.Empty && query.LatestRecord is not null
                    || query.Reason == TelemetryReason.Stale && (query.LatestRecord is null || query.LatestRecord.RecordTime >= proof.WindowStart))
                    throw new JsonException();
            }
        }
        foreach (var record in proof.Records)
        {
            var backend = config.Backends.Single(b => b.ResourceId == record.BackendResourceId);
            if (record.WorkspaceId != backend.WorkspaceId || !backend.Tables.Contains(record.Table)
                || !backend.ApplicationResourceIds.Contains(record.ApplicationResourceId, StringComparer.OrdinalIgnoreCase)
                || record.RecordTime < proof.WindowStart || record.RecordTime > proof.WindowEnd || record.ObservedRecords < 1
                || string.IsNullOrWhiteSpace(record.ItemId) || record.ItemId.Length > 128 || record.ItemId.Any(char.IsControl)) throw new JsonException();
        }
    }

    private static IReadOnlyList<FoundationOperation> ReadEffects(FactoryTelemetry selected, Dictionary<string, string> values, bool completed)
    {
        if (completed) return FactoryTelemetryValidation.ReadCompletedEffects(selected, values);
        if (selected.OperationCount is < 0 or > 64) throw new JsonException();
        var result = new List<FoundationOperation>();
        for (var i = 0; i < selected.OperationCount; i++)
        {
            if (!values.TryGetValue(ProductConfigurationKeys.TelemetryOperation(selected.OperationId, i), out var value)) throw new JsonException();
            var operation = FactoryReservationRecords.Parse<FoundationOperation>(value);
            if (operation.SchemaVersion != 1 || operation.Kind != OperationKind || operation.Index != i
                || operation.OperationId != selected.OperationId || operation.FactoryId != selected.Configuration.FactoryId) throw new JsonException();
            result.Add(operation);
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> ReadAsync(OwnerReservationStore store, FactoryTelemetry selected, CancellationToken token)
    {
        if (selected.OperationCount is < 0 or > 64) throw new JsonException();
        var values = new Dictionary<string, string>();
        foreach (var key in new[] { ProductConfigurationKeys.FactoryManifest, ProductConfigurationKeys.FactoryTelemetry }
            .Concat(Enumerable.Range(0, selected.OperationCount).Select(i => ProductConfigurationKeys.TelemetryOperation(selected.OperationId, i))))
            if (await store.ReadAsync(key, token) is { } value) values.Add(key, value.Value);
        return values;
    }

    internal static async Task<FactoryReservationResult> InspectAsync(FactoryReservationResult admission, OwnerReservationStore store, TimeProvider clock, CancellationToken token)
    {
        var manifest = FactoryReservationRecords.Parse<FactoryManifest>((await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token))!.Value);
        if (manifest.TelemetryOperationId is null) return admission;
        var root = await store.ReadAsync(ProductConfigurationKeys.FactoryTelemetry, token);
        if (root is null) return admission with { State = FactoryReservationState.Unknown, Detail = "Telemetry intent without complete record; held, no replacement dispatch." };
        var selected = FactoryReservationRecords.Parse<FactoryTelemetry>(root.Value);
        var values = await ReadAsync(store, selected, token);
        _ = FactoryTelemetryValidation.ReadPreparation(manifest, values, requireChecking: false);
        var operations = ReadEffects(selected, values, completed: selected.Stage is "prepared" or "checking" or "observed");
        if (operations.Any(o => o.State is FoundationOperationState.Intent or FoundationOperationState.InFlight or FoundationOperationState.Unknown))
            selected = selected with { State = TelemetryState.Unknown };
        else if (operations.Any(o => o.State == FoundationOperationState.Failed) && selected.State is not (TelemetryState.Blocked or TelemetryState.Canceled))
            selected = selected with { State = TelemetryState.Failed };
        foreach (var (reference, hash, verified) in new[]
        {
            (selected.LastCheckReference, selected.LastCheckSha256, false),
            (selected.VerifiedProofReference, selected.VerifiedProofSha256, true),
        })
        {
            if (reference is null)
            {
                if (hash is not null || selected.State == TelemetryState.Ready) throw new JsonException();
                continue;
            }
            if (!Guid.TryParse(reference.Split('/').Last(), out var checkId)
                || reference != ProductConfigurationKeys.TelemetryProof(selected.OperationId, checkId)) throw new JsonException();
            var observed = FactoryReservationRecords.Parse<TelemetryProof>((await store.ReadAsync(reference, token))?.Value ?? throw new JsonException());
            if (FactoryTelemetryValidation.Hash(observed) != hash || observed.CheckId != checkId
                || observed.FactoryId != selected.Configuration.FactoryId || observed.OperationId != selected.OperationId
                || observed.ConfigurationRevision != selected.ApprovalFingerprint || verified && observed.State != TelemetryState.Ready)
                throw new JsonException();
            if (verified) selected = selected with { Proof = observed };
        }
        selected = Expire(selected, clock.GetUtcNow());
        return admission with { Telemetry = selected, Foundation = Pending(admission.Foundation, selected.State),
            Detail = "Historical telemetry proof and preparation records only; use telemetry --verify-only for current readiness. " + admission.Detail };
    }
}
