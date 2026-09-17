using System.Globalization;
using System.Net;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    private static async Task<JsonElement> ReadNativeAsync(FactoryPreparationArm arm, string target, string api, CancellationToken token)
    {
        using var response = await arm.SendAsync(HttpMethod.Get, target, api, token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ReservationStoreException(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? FactoryReservationState.Blocked : FactoryReservationState.Unknown, "CurrentNativePreviewPrerequisiteUnavailable:" + target);
        return await BodyAsync(response, token);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        await response.Content.LoadIntoBufferAsync(FactoryPreviewPreparation.Policy.NativeResponseBytes, token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token),
            new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> CaptureAsync(FactoryPreparationArm arm, string target, string api,
        IDictionary<string, PreviewPreparationNative> observations, CancellationToken token)
    {
        var resource = await ReadNativeAsync(arm, target, api, token);
        if (!Same(resource.GetProperty("id").GetString(), target)) throw Failure("NativePreviewResourceIdentityChanged:" + target);
        observations[target] = new(target, api, FactoryModelsValidation.Hash(resource), Incarnation(resource));
        return resource;
    }

    private static string Incarnation(JsonElement resource)
    {
        if (resource.TryGetProperty("systemData", out var system) && system.TryGetProperty("createdAt", out var created))
            return created.GetDateTimeOffset().ToUniversalTime().ToString("O");
        var properties = resource.GetProperty("properties");
        if (properties.TryGetProperty("createdOn", out created))
            return created.GetDateTimeOffset().ToUniversalTime().ToString("O");
        if (properties.TryGetProperty("customerId", out var workspace) && Guid.TryParse(workspace.GetString(), out var workspaceId) && workspaceId != Guid.Empty)
            return "workspace-id:" + workspaceId.ToString("D");
        if (properties.TryGetProperty("AppId", out var app) && Guid.TryParse(app.GetString(), out var appId) && appId != Guid.Empty)
            return "application-id:" + appId.ToString("D");
        if (properties.TryGetProperty("resource", out var nested) && nested.TryGetProperty("_rid", out var rid))
        {
            var value = rid.GetString()!;
            var decoded = Convert.FromBase64String(value.Replace('-', '/'));
            if (decoded.Length is not (4 or 8)) throw new JsonException("Native database/container RID required.");
            return "cosmos-rid-sha256:" + FactoryReservationRecords.Hash(value);
        }
        if (resource.TryGetProperty("etag", out var etag) && etag.GetString() is { Length: > 0 and <= 256 } tag)
            return "etag-sha256:" + FactoryReservationRecords.Hash(tag);
        // ARM role definitions expose properties and are pinned by their complete native revision.
        if (resource.GetProperty("id").GetString()!.Contains("/roledefinitions/", StringComparison.OrdinalIgnoreCase))
            return "properties-sha256:" + FactoryModelsValidation.Hash(properties);
        if (resource.GetProperty("id").GetString()!.Contains("/tables/", StringComparison.OrdinalIgnoreCase))
            return "properties-sha256:" + FactoryModelsValidation.Hash(properties);
        throw Failure("NativeCreationIncarnationUnavailable:" + resource.GetProperty("id").GetString());
    }

    private static async Task ObserveBindingAsync(FactoryPreparationArm arm, FoundationOperation binding, FactoryManifest manifest,
        IDictionary<string, PreviewPreparationNative> native, CancellationToken token)
    {
        if (binding.FactoryId != manifest.Identity.FactoryId || binding.State != FoundationOperationState.Completed
            || binding.Disposition != FoundationResourceDisposition.Created || binding.Target != binding.ResourceId || binding.Incarnation is null)
            throw Failure("ProvenOriginalPreviewBindingRequired:" + binding.Target);
        var resource = await CaptureAsync(arm, binding.Target, binding.ApiVersion, native, token);
        var incarnation = native[binding.Target].Incarnation;
        if (DateTimeOffset.TryParse(binding.Incarnation, CultureInfo.InvariantCulture, out var original)
            ? !DateTimeOffset.TryParse(incarnation, CultureInfo.InvariantCulture, out var current) || current != original
            : incarnation != binding.Incarnation && (!resource.TryGetProperty("etag", out var etag) || etag.GetString() != binding.Incarnation))
            throw Failure("OriginalPreviewBindingIncarnationChanged:" + binding.Target);
        var p = resource.GetProperty("properties");
        if (binding.PrincipalId is not null && (p.GetProperty("principalId").GetString() != binding.PrincipalId
            || p.GetProperty("clientId").GetString() != binding.ClientId || p.GetProperty("tenantId").GetString() != manifest.Identity.TenantId))
            throw Failure("OriginalPreviewIdentityChanged:" + binding.Target);
        if (binding.Target.Contains("/managedenvironments/", StringComparison.Ordinal)
            && !p.GetProperty("vnetConfiguration").GetProperty("internal").GetBoolean()) throw Failure("ExistingPrivateFactoryEnvironmentRequired");
        if (binding.Target.EndsWith("-cosmos", StringComparison.Ordinal)
            && (!p.GetProperty("disableLocalAuth").GetBoolean() || p.GetProperty("publicNetworkAccess").GetString() != "Disabled"
                || FactoryGovernance.NormalizeEndpoint(p.GetProperty("documentEndpoint").GetString()!)
                    != "https://" + FactoryPreviewValidation.Prefix(manifest.Identity.FactoryId) + "-cosmos.documents.azure.com"))
            throw Failure("OriginalPrivateFactoryCosmosRequired");
        if (binding.Target.EndsWith("/containers/runs", StringComparison.Ordinal)) RequireContainer(resource, "runs");
    }

    private static async Task<string> MetadataRoleAsync(FactoryPreparationArm arm, string role, IReadOnlyList<string> actions,
        IDictionary<string, PreviewPreparationNative> native, CancellationToken token)
    {
        var resource = await CaptureAsync(arm, role, "2022-04-01", native, token);
        var p = resource.GetProperty("properties");
        if (p.GetProperty("type").GetString() != "CustomRole" || p.GetProperty("permissions").GetArrayLength() != 1)
            throw Failure("AdministratorMustProvideExactPreviewMetadataRole:" + role);
        var permission = p.GetProperty("permissions")[0];
        if (!FactoryModelsValidation.Strings(permission, "actions").Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(actions.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            || FactoryModelsValidation.Strings(permission, "notActions").Length != 0
            || FactoryModelsValidation.Strings(permission, "dataActions").Length != 0
            || FactoryModelsValidation.Strings(permission, "notDataActions").Length != 0)
            throw Failure("AdministratorMustProvideExactPreviewMetadataActions:" + role);
        return FactoryModelsValidation.Hash(p);
    }

    private static async Task GrantPermissionAsync(FactoryPreparationArm arm, PreviewPreparationGrant grant, CancellationToken token)
    {
        var role = await ReadNativeAsync(arm, grant.RoleDefinitionId, "2022-04-01", token);
        var p = role.GetProperty("properties");
        if (FactoryModelsValidation.Hash(p) != grant.RoleRevision
            || !FactoryModelsValidation.Strings(p, "assignableScopes").Any(s => Same(s, grant.Scope)
                || grant.Scope.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
            throw Failure("MetadataRoleAssignabilityRequired:" + grant.Scope);
        await OperatorPermissionAsync(arm, new(grant.Scope, ["Microsoft.Authorization/roleAssignments/write"]), token);
    }

    private static async Task OperatorPermissionAsync(FactoryPreparationArm arm, PreviewPreparationPermission requirement, CancellationToken token)
    {
        var permissions = Complete(await ReadNativeAsync(arm, requirement.Scope + "/providers/microsoft.authorization/permissions", "2022-04-01", token));
        var denies = Complete(await ReadNativeAsync(arm, requirement.Scope + "/providers/microsoft.authorization/denyassignments", "2022-04-01", token));
        if (denies.Length != 0 || requirement.Actions.Any(action => !permissions.Any(entry =>
            FactoryModelsValidation.Strings(entry, "actions").Any(a => Action(a, action))
            && !FactoryModelsValidation.Strings(entry, "notActions").Any(a => Action(a, action))
            && (!entry.TryGetProperty("condition", out var condition) || condition.ValueKind == JsonValueKind.Null))))
            throw Failure("CurrentNativeOperatorActionsRequired:" + requirement.Scope + ":" + string.Join(",", requirement.Actions));
    }

    private static bool Action(string pattern, string action) => System.Text.RegularExpressions.Regex.IsMatch(action,
        "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static JsonElement[] Complete(JsonElement result)
    {
        if (result.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException("Complete native collection required; no truncation.");
        var entries = result.GetProperty("value").EnumerateArray().ToArray();
        if (entries.Length > 256) throw new JsonException("Native collection exceeds preview preparation budget.");
        return entries;
    }

    private static void RequireGrant(JsonElement resource, PreviewPreparationGrant grant, string actor, string? incarnation = null)
    {
        var p = resource.GetProperty("properties");
        if (!Same(resource.GetProperty("id").GetString(), grant.Target) || p.GetProperty("principalId").GetString() != grant.PrincipalId
            || p.GetProperty("principalType").GetString() != "ServicePrincipal" || !Same(p.GetProperty("scope").GetString(), grant.Scope)
            || !Same(p.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId) || p.GetProperty("createdBy").GetString() != actor
            || incarnation is not null && p.GetProperty("createdOn").GetDateTimeOffset() != DateTimeOffset.Parse(incarnation, CultureInfo.InvariantCulture)
            || p.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
            || p.TryGetProperty("conditionVersion", out var version) && version.ValueKind != JsonValueKind.Null && version.GetString() != "2.0"
            || p.TryGetProperty("updatedOn", out var modified) && modified.ValueKind != JsonValueKind.Null
                && modified.GetDateTimeOffset() != p.GetProperty("createdOn").GetDateTimeOffset())
            throw Failure("ExactNativePreviewGrantRequired:" + grant.Target);
    }

    private static void RequireContainer(JsonElement resource, string name)
    {
        var p = resource.GetProperty("properties").GetProperty("resource");
        var partition = p.GetProperty("partitionKey");
        if (p.GetProperty("id").GetString() != name || partition.GetProperty("kind").GetString() != "Hash"
            || partition.GetProperty("version").GetInt32() != 2 || !FactoryModelsValidation.Strings(partition, "paths").SequenceEqual(["/product"]))
            throw Failure("ExactProductPartitionedPreviewStateRequired");
    }

    private static async Task AbsentAsync(FactoryPreparationArm arm, string target, string api, CancellationToken token)
    {
        using var response = await arm.SendAsync(HttpMethod.Get, target, api, token);
        if (response.StatusCode != HttpStatusCode.NotFound) throw Failure("CreateTargetNotProvenAbsent:" + target);
    }

    private async Task RequireCurrentAsync(FactoryReservationResult admission, PreviewPreparationPlan plan,
        OwnerReservationStore store, CancellationToken token)
    {
        var current = await ReadPlanAsync(admission, plan.Selection, store, token);
        if (current.Fingerprint != plan.Fingerprint) throw Failure("PreviewPreparationInputsOrNativeAuthorityChanged");
        var identity = plan.Manifest.Identity;
        var owner = await new FactoryReservationService(reviewer, azure, http, clock)
            .StatusAsync(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId, identity.ProductKey, token);
        if (owner.State != FactoryReservationState.Reserved || owner.Identity != identity || owner.Foundation?.State != FactoryFoundationState.Inactive)
            throw Unknown("CurrentOwnerRegistryAndInactiveFoundationUnconfirmed");
    }
}
