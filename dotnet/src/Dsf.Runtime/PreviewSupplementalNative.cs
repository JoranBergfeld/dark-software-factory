using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class PreviewSupplementalNative(PreviewSupplementalAuthority authority, TokenCredential credential, HttpClient http)
{
    internal SortedDictionary<string, string> Revisions { get; } = new(StringComparer.Ordinal);
    internal static IReadOnlyList<string> Targets(PreviewSupplementalAuthority authority) => new[] { authority.Host.MetadataScope,
        authority.MetadataGrant.Target, RoleTarget(authority.Host.MetadataScope, authority.Host.MetadataRoleDefinitionId), InventoryTarget(authority) }
        .Concat(authority.Grants.SelectMany(g => new[] { g.Target, RoleTarget(g.Scope, g.RoleDefinitionId) })).Distinct(StringComparer.Ordinal).ToArray();
    private static string RoleTarget(string scope, string id) => scope + "/providers/microsoft.authorization/roledefinitions/" + id.Split('/').Last();
    private static string InventoryTarget(PreviewSupplementalAuthority a) =>
        (a.Workload.Source.Kind == "webiq" ? a.Grants.Single().Scope : a.Host.MetadataScope)
        + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('" + a.Host.PrincipalId + "')";

    internal async Task CheckAsync(CancellationToken token)
    {
        var web = authority.Workload.Source.Kind == "webiq";
        var resource = await GetAsync(authority.Host.MetadataScope, web ? authority.Vault!.ApiVersion : "2025-05-01", token);
        var p = resource.GetProperty("properties");
        if (!Same(resource.GetProperty("id").GetString(), authority.Host.MetadataScope)
            || p.GetProperty("publicNetworkAccess").GetString() != "Disabled") throw Changed();
        if (web)
        {
            if (p.GetProperty("enableRbacAuthorization").ValueKind != JsonValueKind.True || p.GetProperty("accessPolicies").GetArrayLength() != 0
                || resource.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset()
                    != DateTimeOffset.Parse(authority.Vault!.Incarnation!, System.Globalization.CultureInfo.InvariantCulture)) throw Changed();
        }
        else if (p.GetProperty("disableLocalAuth").ValueKind != JsonValueKind.True
            || !authority.PrerequisiteRevisions.TryGetValue(authority.Host.MetadataScope, out var revision)
            || revision != FactoryTelemetryValidation.Hash(resource)) throw Changed();
        foreach (var grant in authority.Grants)
        {
            var effect = authority.Effects.Single(e => e.Target == grant.Target);
            RequireGrant(await GetAsync(grant.Target, "2022-04-01", token), effect, grant.Scope, grant.RoleDefinitionId);
            var native = await GetAsync(RoleTarget(grant.Scope, grant.RoleDefinitionId), "2022-04-01", token);
            var role = native.GetProperty("properties");
            var builtin = grant.RoleDefinitionId.EndsWith("/" + FactorySupplementalValidation.SecretsRole, StringComparison.Ordinal)
                || grant.RoleDefinitionId.EndsWith("/" + FactorySupplementalValidation.SearchDataRole, StringComparison.Ordinal);
            if (!Same(native.GetProperty("id").GetString(), grant.RoleDefinitionId) || role.GetProperty("type").GetString() != (builtin ? "BuiltInRole" : "CustomRole")
                || role.GetProperty("permissions").GetArrayLength() != 1) throw Changed();
            RequirePermissions(role.GetProperty("permissions")[0], grant.Actions, grant.DataActions);
        }
        RequireGrant(await GetAsync(authority.MetadataGrant.Target, "2022-04-01", token), authority.MetadataGrant,
            authority.Host.MetadataScope, authority.Host.MetadataRoleDefinitionId);
        var metadata = await GetAsync(RoleTarget(authority.Host.MetadataScope, authority.Host.MetadataRoleDefinitionId), "2022-04-01", token);
        var mp = metadata.GetProperty("properties");
        if (!Same(metadata.GetProperty("id").GetString(), authority.Host.MetadataRoleDefinitionId) || mp.GetProperty("type").GetString() != "CustomRole"
            || FactoryModelsValidation.Hash(mp) != authority.Host.MetadataRoleRevision || mp.GetProperty("permissions").GetArrayLength() != 1) throw Changed();
        RequirePermissions(mp.GetProperty("permissions")[0], FactoryPreviewSupplementalValidation.MetadataActions(authority.Host.Kind), []);
        var list = await GetAsync(InventoryTarget(authority), "2022-04-01", token);
        if (list.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "") throw Changed();
        var assignments = list.GetProperty("value").EnumerateArray().ToArray();
        if (!assignments.Select(a => a.GetProperty("id").GetString()).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(authority.Grants.Select(g => g.Target).Append(authority.MetadataGrant.Target).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw Changed();
        foreach (var assignment in assignments)
        {
            var id = assignment.GetProperty("id").GetString()!;
            var grant = authority.Grants.SingleOrDefault(g => Same(g.Target, id));
            RequireGrant(assignment, grant is null ? authority.MetadataGrant : authority.Effects.Single(e => e.Target == grant.Target),
                grant?.Scope ?? authority.Host.MetadataScope, grant?.RoleDefinitionId ?? authority.Host.MetadataRoleDefinitionId);
        }
    }

    private void RequireGrant(JsonElement native, FoundationOperation effect, string scope, string role)
    {
        var p = native.GetProperty("properties");
        if (!Same(native.GetProperty("id").GetString(), effect.Target) || FactoryTelemetryValidation.Hash(p) != effect.NativeRevision
            || !Same(p.GetProperty("scope").GetString(), scope) || !Same(p.GetProperty("roleDefinitionId").GetString(), role)
            || p.GetProperty("principalId").GetString() != authority.Workload.SourcePrincipalId
            || p.GetProperty("principalType").GetString() != "ServicePrincipal" || p.GetProperty("createdBy").GetString() != authority.Workload.OperatorPrincipalId
            || p.GetProperty("createdOn").GetDateTimeOffset() != DateTimeOffset.Parse(effect.Incarnation!, System.Globalization.CultureInfo.InvariantCulture)
            || p.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
            || p.TryGetProperty("conditionVersion", out var version) && version.ValueKind != JsonValueKind.Null && version.GetString() != "2.0")
            throw Changed();
    }
    private static void RequirePermissions(JsonElement p, IReadOnlyList<string> actions, IReadOnlyList<string> data)
    {
        if (!Exact("actions", actions) || !Exact("dataActions", data) || !Exact("notActions", []) || !Exact("notDataActions", [])) throw Changed();
        bool Exact(string field, IReadOnlyList<string> expected) => FactoryModelsValidation.Strings(p, field).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }
    private async Task<JsonElement> GetAsync(string target, string api, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + target + (target.Contains('?') ? "&" : "?") + "api-version=" + api);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK) throw new PreviewCapabilityException(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound ? FactoryCharterState.Blocked : FactoryCharterState.Unknown,
            "SupplementalNativeAuthorityUnavailable");
        using var body = await EvidencePayload.ReadAsync(response, token, FactoryPreviewSupplementalValidation.SourceBytes);
        var value = body.RootElement.Clone();
        var hash = FactoryModelsValidation.Hash(value);
        if (Revisions.TryGetValue(target, out var previous) && previous != hash) throw Changed();
        Revisions[target] = hash;
        return value;
    }
    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static PreviewCapabilityException Changed() => new(FactoryCharterState.Blocked, "CurrentSupplementalNativeAuthorityChanged");
}
