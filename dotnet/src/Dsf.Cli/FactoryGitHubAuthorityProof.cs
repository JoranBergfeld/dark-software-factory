using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryGitHubAuthorityService
{
    private async Task VerifyCurrentGrantsAsync(FactoryReservationIdentity identity, CancellationToken token)
    {
        var failures = new List<ReservationStoreException>();
        foreach (var grant in operations.Where(o => o.RoleScope is not null))
        {
            var operation = $"GET {grant.Target}?api-version={grant.ApiVersion}";
            const string recovery = " Responsible administrator: factory Azure RBAC grantor. Historical receipts/access are not current binding proof; no adoption or recreation.";
            try
            {
                using var response = await ArmAsync(HttpMethod.Get, grant.Target, grant.ApiVersion, null, identity, null, token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new ReservationStoreException(FactoryReservationState.Blocked, operation + ": approved assignment is missing." + recovery);
                if (response.StatusCode != HttpStatusCode.OK) throw new JsonException();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var current = document.RootElement;
                var properties = current.GetProperty("properties");
                if (!string.Equals(current.GetProperty("id").GetString(), grant.ResourceId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(properties.GetProperty("principalId").GetString(), grant.PrincipalId, StringComparison.OrdinalIgnoreCase)
                    || properties.GetProperty("principalType").GetString() != "ServicePrincipal"
                    || !string.Equals(properties.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(properties.GetProperty("scope").GetString(), grant.RoleScope, StringComparison.OrdinalIgnoreCase)
                    || properties.GetProperty("createdBy").GetString() != identity.AzureActorId
                    || properties.GetProperty("createdOn").GetDateTimeOffset()
                        != DateTimeOffset.Parse(grant.Incarnation!, CultureInfo.InvariantCulture)
                    || OptionalString(properties, "condition") != grant.Condition
                    || OptionalString(properties, "conditionVersion") != grant.ConditionVersion)
                    throw new ReservationStoreException(FactoryReservationState.Blocked, operation + ": approved assignment was recreated or its binding changed." + recovery);
            }
            catch (ReservationStoreException e) { failures.Add(e); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException
                or HttpRequestException or OwnerReservationStore.AuthenticationFailedException
                || e is OperationCanceledException && !token.IsCancellationRequested)
            {
                failures.Add(new ReservationStoreException(FactoryReservationState.Unknown,
                    operation + ": current assignment evidence is unavailable or malformed." + recovery));
            }
        }
        if (failures.Count != 0)
            throw new ReservationStoreException(failures.Any(f => f.State == FactoryReservationState.Unknown)
                ? FactoryReservationState.Unknown : FactoryReservationState.Blocked, string.Join(" ", failures.Select(f => f.Message)));
    }

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;

    private async Task VerifyKeyIsolationAsync(FactoryReservationIdentity identity, FactoryGitHubAuthority preview, CancellationToken token)
    {
        var scope = preview.DestinationVaultId + "/secrets/github-app-private-key";
        var ancestorScopes = new[] { scope, preview.DestinationVaultId, preview.FactoryResourceGroupId,
            "/subscriptions/" + identity.SubscriptionId, "/" };
        foreach (var workload in preview.OtherWorkloadPrincipalIds.Append(preview.CodePrincipalId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var operation = $"GET {scope}/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('{workload}')";
            try
            {
                if (!Guid.TryParse(workload, out var principal) || principal == Guid.Empty) throw new JsonException();
                using var response = await ArmAsync(HttpMethod.Get, operation[4..], "2022-04-01", null, identity, null, token);
                if (response.StatusCode != HttpStatusCode.OK) throw new JsonException();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (json.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
                    throw new JsonException();
                foreach (var assignment in json.RootElement.GetProperty("value").EnumerateArray())
                {
                    var properties = assignment.GetProperty("properties");
                    var assigned = properties.GetProperty("principalId").GetString();
                    var type = properties.GetProperty("principalType").GetString();
                    var assignedScope = properties.GetProperty("scope").GetString() ?? throw new JsonException();
                    var id = assignment.GetProperty("id").GetString() ?? throw new JsonException();
                    if (!Guid.TryParse(assigned, out var assignedId) || assignedId == Guid.Empty
                        || type is not ("Group" or "ServicePrincipal") || type == "ServicePrincipal" && assignedId != principal
                        || !(ancestorScopes.Contains(assignedScope, StringComparer.OrdinalIgnoreCase)
                            || Regex.IsMatch(assignedScope, "^/providers/microsoft.management/managementgroups/[-a-zA-Z0-9_.()]+$", RegexOptions.IgnoreCase))
                        || !Guid.TryParse(id.Split('/').Last(), out var assignmentId) || assignmentId == Guid.Empty
                        || !id.Equals(assignedScope.TrimEnd('/') + "/providers/microsoft.authorization/roleassignments/" + assignmentId, StringComparison.OrdinalIgnoreCase)
                        || properties.TryGetProperty("condition", out var condition)
                            && condition.ValueKind != JsonValueKind.Null && !string.IsNullOrEmpty(condition.GetString()))
                        throw new JsonException();
                    var definition = properties.GetProperty("roleDefinitionId").GetString() ?? throw new JsonException();
                    if (!Regex.IsMatch(definition,
                        "^(/subscriptions/[0-9a-f-]{36}|/providers/microsoft.management/managementgroups/[-a-z0-9_.()]+)?/providers/microsoft.authorization/roledefinitions/[0-9a-f-]{36}$",
                        RegexOptions.IgnoreCase) || !Guid.TryParse(definition.Split('/').Last(), out _))
                        throw new JsonException();
                    operation = "GET " + definition;
                    using var role = await ArmAsync(HttpMethod.Get, definition, "2022-04-01", null, identity, null, token);
                    if (role.StatusCode != HttpStatusCode.OK) throw new JsonException();
                    using var roleJson = JsonDocument.Parse(await role.Content.ReadAsStringAsync(token));
                    if (!string.Equals(roleJson.RootElement.GetProperty("id").GetString(), definition, StringComparison.OrdinalIgnoreCase))
                        throw new JsonException();
                    foreach (var permission in roleJson.RootElement.GetProperty("properties").GetProperty("permissions").EnumerateArray())
                    {
                        var data = Strings("dataActions");
                        var excludedData = Strings("notDataActions");
                        var actions = Strings("actions");
                        var excluded = Strings("notActions");
                        if (Allows(data, excludedData, "Microsoft.KeyVault/vaults/secrets/getSecret/action")
                            || Allows(actions, excluded, "Microsoft.Authorization/roleAssignments/write")
                            || Allows(actions, excluded, "Microsoft.KeyVault/vaults/accessPolicies/write")
                            || Allows(actions, excluded, "Microsoft.KeyVault/vaults/write"))
                            throw new ReservationStoreException(FactoryReservationState.Blocked,
                                $"ARM assignedTo reports inherited {type} {assigned} key-read or access-changing authority for workload {workload} at {assignedScope}; "
                                + "factory Azure grantor must reconcile. No signing key copied or group/operator authority adopted.");
                        string[] Strings(string name) => permission.GetProperty(name).EnumerateArray()
                            .Select(v => v.GetString() ?? throw new JsonException()).ToArray();
                    }
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or HttpRequestException
                or OwnerReservationStore.AuthenticationFailedException)
            {
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    operation + $": effective membership/role/scope evidence unavailable for workload {workload}; "
                    + "Azure RBAC/directory administrator must resolve. No consent, grant or key copy.");
            }
        }

        static bool Allows(string[] actions, string[] excluded, string operation) =>
            actions.Any(p => Matches(p, operation)) && !excluded.Any(p => Matches(p, operation));
        static bool Matches(string pattern, string operation) =>
            Regex.IsMatch(operation, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);
    }

    private async Task VerifyBaseBindingsAsync(FactoryReservationResult admission, FactoryGitHubAuthority preview, CancellationToken token)
    {
        foreach (var (target, incarnation) in preview.BindingIncarnations)
        {
            var original = admission.Foundation!.Operations.Single(o => o.Target == target);
            using var response = await ArmAsync(HttpMethod.Get, target, original.ApiVersion, null, admission.Identity!, null, token);
            try
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new JsonException();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var current = json.RootElement;
                var created = current.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset();
                if (!string.Equals(current.GetProperty("id").GetString(), target, StringComparison.OrdinalIgnoreCase)
                    || created.ToString("O") != incarnation
                    || current.GetProperty("systemData").TryGetProperty("lastModifiedAt", out var modified)
                        && modified.GetDateTimeOffset() != created)
                    throw new JsonException();
                if (original.PrincipalId is not null)
                {
                    var properties = current.GetProperty("properties");
                    if (properties.GetProperty("principalId").GetString() != original.PrincipalId
                        || properties.GetProperty("clientId").GetString() != original.ClientId
                        || properties.GetProperty("tenantId").GetString() != admission.Identity!.TenantId)
                        throw new JsonException();
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "GET proven foundation binding changed or is unavailable: " + target
                    + ". Factory administrator must reconcile the original incarnation; no grants, key copy or adoption.");
            }
        }
    }

    private async Task VerifyNativeHostPolicyAsync(FactoryReservationIdentity identity, string hostTarget, CancellationToken token)
    {
        using var response = await ArmAsync(HttpMethod.Get, hostTarget + "/authconfigs/current", "2025-01-01", null, identity, null, token);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new JsonException();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var props = json.RootElement.GetProperty("properties");
            var global = props.GetProperty("globalValidation");
            var aad = props.GetProperty("identityProviders").GetProperty("azureActiveDirectory");
            var registration = aad.GetProperty("registration");
            var validation = aad.GetProperty("validation");
            var principals = validation.GetProperty("defaultAuthorizationPolicy").GetProperty("allowedPrincipals");
            var identities = principals.GetProperty("identities").EnumerateArray().Select(v => v.GetString()).ToArray();
            var audiences = validation.GetProperty("allowedAudiences").EnumerateArray().Select(v => v.GetString()).ToArray();
            if (props.GetProperty("platform").GetProperty("enabled").ValueKind != JsonValueKind.True
                || global.GetProperty("unauthenticatedClientAction").GetString() != "Return401"
                || global.TryGetProperty("excludedPaths", out var excluded) && excluded.GetArrayLength() != 0
                || props.GetProperty("httpSettings").GetProperty("requireHttps").ValueKind != JsonValueKind.True
                || props.GetProperty("login").GetProperty("tokenStore").GetProperty("enabled").ValueKind != JsonValueKind.False
                || aad.GetProperty("enabled").ValueKind != JsonValueKind.True
                || registration.GetProperty("clientId").GetString() != authority!.AudienceClientId
                || registration.GetProperty("openIdIssuer").GetString() != "https://login.microsoftonline.com/" + identity.TenantId + "/v2.0"
                || identities.Length != 2 || !identities.Contains(identity.AzureActorId) || !identities.Contains(authority.CodePrincipalId)
                || principals.TryGetProperty("groups", out var groups) && groups.GetArrayLength() != 0
                || audiences.Length != 2 || !audiences.Contains(authority.AudienceClientId) || !audiences.Contains("api://" + authority.AudienceClientId))
                throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "GET deployed authConfig does not prove exact Entra caller isolation and disabled token persistence; factory identity administrator must reconcile, no /verify or token issuance.");
        }
    }

    private async Task ProveDeploymentResourceAsync(OwnerReservationStore store, FactoryReservationIdentity identity,
        FoundationOperation deployment, string resourceTarget, string apiVersion, CancellationToken token)
    {
        using var response = await ArmAsync(HttpMethod.Get, (deployment.DeploymentTarget ?? deployment.Target) + "/operations", "2025-04-01", null, identity, null, token);
        if (response.StatusCode != HttpStatusCode.OK) throw new JsonException();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (json.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException();
        var matches = json.RootElement.GetProperty("value").EnumerateArray()
            .Where(o => o.GetProperty("properties").TryGetProperty("targetResource", out var target)
                && string.Equals(target.GetProperty("id").GetString(), resourceTarget, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new JsonException();
        var receipt = matches[0];
        var props = receipt.GetProperty("properties");
        if (props.GetProperty("provisioningOperation").GetString() != "Create"
            || props.GetProperty("provisioningState").GetString() != "Succeeded"
            || props.GetProperty("statusCode").GetString() is not ("OK" or "200" or "Created" or "201")
            || !Guid.TryParse(props.GetProperty("trackingId").GetString(), out var requestId) || requestId == Guid.Empty)
            throw new JsonException();
        var providerOperation = receipt.GetProperty("operationId").GetString();
        if (providerOperation is null || !Regex.IsMatch(providerOperation, "^[a-fA-F0-9-]{1,128}$")) throw new JsonException();
        using var resourceResponse = await ArmAsync(HttpMethod.Get, resourceTarget, apiVersion, null, identity, null, token);
        if (resourceResponse.StatusCode != HttpStatusCode.OK) throw new JsonException();
        using var resourceJson = JsonDocument.Parse(await resourceResponse.Content.ReadAsStringAsync(token));
        var resource = resourceJson.RootElement;
        if (!string.Equals(resource.GetProperty("id").GetString(), resourceTarget, StringComparison.OrdinalIgnoreCase)) throw new JsonException();
        DateTimeOffset created;
        string incarnation;
        string? versionUri = null;
        if (resourceTarget.EndsWith("/secrets/github-app-private-key", StringComparison.Ordinal))
        {
            var metadata = resource.GetProperty("properties");
            var attributes = metadata.GetProperty("attributes");
            created = DateTimeOffset.FromUnixTimeSeconds(attributes.GetProperty("created").GetInt64());
            versionUri = metadata.GetProperty("secretUriWithVersion").GetString();
            if (attributes.GetProperty("enabled").ValueKind != JsonValueKind.True
                || attributes.GetProperty("updated").GetInt64() != created.ToUnixTimeSeconds()
                || created.ToUnixTimeSeconds() < deployment.StartedAt.ToUnixTimeSeconds() || created > clock.GetUtcNow()
                || versionUri is null || !Regex.IsMatch(versionUri, "^https://"
                    + Regex.Escape(authority!.DestinationVaultId.Split('/').Last())
                    + @"\.vault\.azure\.net/secrets/github-app-private-key/[0-9a-f]{32}$"))
                throw new JsonException();
            incarnation = "key-version:" + versionUri + ";created:" + created.ToUnixTimeSeconds();
        }
        else
        {
            created = resource.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset();
            if (created < deployment.StartedAt || created > clock.GetUtcNow()
                || resource.GetProperty("systemData").GetProperty("lastModifiedAt").GetDateTimeOffset() != created)
                throw new JsonException();
            incarnation = created.ToString("O");
        }
        var completed = deployment with
        {
            State = FoundationOperationState.Completed, Disposition = FoundationResourceDisposition.Created,
            ProviderCorrelationId = deployment.ProviderCorrelationId
                ?? operations.Single(o => o.Target == deployment.DeploymentTarget).ProviderCorrelationId,
            Incarnation = incarnation, Endpoint = versionUri, ProviderOperationId = providerOperation,
            ProviderRequestId = requestId.ToString(), ResourceId = resourceTarget,
        };
        var key = ProductConfigurationKeys.GitHubAuthorityOperation(deployment.OperationId, deployment.Index);
        var record = await store.ReadAsync(key, token) ?? throw new JsonException();
        if (record.Value != FactoryReservationRecords.Serialize(deployment)) throw new JsonException();
        await store.WriteAsync(key, FactoryReservationRecords.Serialize(completed), record.ETag, token);
        operations[deployment.Index] = completed;
    }

    private async Task GrantAsync(OwnerReservationStore store, FactoryReservationIdentity identity,
        string scope, string role, CancellationToken token)
    {
        using var permissions = await ArmAsync(HttpMethod.Get, scope + "/providers/microsoft.authorization/permissions", "2022-04-01", null, identity, null, token);
        if (permissions.StatusCode != HttpStatusCode.OK)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "GET grantor permissions denied at " + scope + "; Azure grantor must resolve.");
        using var permissionsJson = JsonDocument.Parse(await permissions.Content.ReadAsStringAsync(token));
        const string action = "Microsoft.Authorization/roleAssignments/write";
        if (!permissionsJson.RootElement.GetProperty("value").EnumerateArray().Any(p =>
                p.GetProperty("actions").EnumerateArray().Any(a => Matches(a.GetString()!, action))
                && !p.GetProperty("notActions").EnumerateArray().Any(a => Matches(a.GetString()!, action))))
            throw new ReservationStoreException(FactoryReservationState.Blocked, "PUT exact role assignment not delegated at " + scope + "; Azure grantor must act.");
        using var denyResponse = await ArmAsync(HttpMethod.Get, scope + "/providers/microsoft.authorization/denyassignments", "2022-04-01", null, identity, null, token);
        if (denyResponse.StatusCode != HttpStatusCode.OK) throw new JsonException();
        using var denies = JsonDocument.Parse(await denyResponse.Content.ReadAsStringAsync(token));
        if (denies.RootElement.GetProperty("value").GetArrayLength() != 0)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Deny assignment requires Azure grantor review at " + scope + "; no override.");
        var definition = "/subscriptions/" + identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/" + role;
        using var grantsResponse = await ArmAsync(HttpMethod.Get, scope + "/providers/microsoft.authorization/roleassignments?$filter=atScope()", "2022-04-01", null, identity, null, token);
        if (grantsResponse.StatusCode != HttpStatusCode.OK) throw new JsonException();
        using var grants = JsonDocument.Parse(await grantsResponse.Content.ReadAsStringAsync(token));
        if (grants.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException();
        var bindings = grants.RootElement.GetProperty("value").EnumerateArray().Where(g =>
            g.GetProperty("properties").GetProperty("principalId").GetString() == authority!.AuthPrincipalId).ToArray();
        if (bindings.Length != 0)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Preexisting/shared workload grants require Azure grantor reconciliation at " + scope + "; not newly owned, no replacement.");
        var target = scope + "/providers/microsoft.authorization/roleassignments/" + Guid.NewGuid().ToString();
        var body = JsonSerializer.Serialize(new { properties = new
        { roleDefinitionId = definition, principalId = authority!.AuthPrincipalId, principalType = "ServicePrincipal" } });
        var operation = new FoundationOperation(1, OperationKind, identity.FactoryId, authority.OperationId, authority.OperationCount,
            target, "2022-04-01", FactoryReservationRecords.Hash(body), Guid.NewGuid(), clock.GetUtcNow(),
            FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested)
        { LogicalIdentity = "factory:github-auth", PrincipalId = authority.AuthPrincipalId, RoleDefinitionId = definition, RoleScope = scope };
        authority = authority with { OperationCount = authority.OperationCount + 1 };
        await SaveAsync(store, token);
        var record = await store.WriteAsync(ProductConfigurationKeys.GitHubAuthorityOperation(operation.OperationId, operation.Index),
            FactoryReservationRecords.Serialize(operation), null, token);
        operations.Add(operation);
        using var response = await ArmAsync(HttpMethod.Put, target, operation.ApiVersion, body, identity, operation.ClientRequestId, token);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            operation = operation with { State = FoundationOperationState.Failed, Disposition = FoundationResourceDisposition.Unproven,
                HttpStatus = (int)response.StatusCode };
            await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
            operations[operation.Index] = operation;
            throw new ReservationStoreException(FactoryReservationState.Blocked, "PUT exact role assignment denied at " + scope + "; Azure grantor must resolve. No retry.");
        }
        if (response.StatusCode != HttpStatusCode.Created) throw new JsonException();
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var actual = receipt.RootElement.GetProperty("properties");
        var created = actual.GetProperty("createdOn").GetDateTimeOffset();
        var conditionVersion = OptionalString(actual, "conditionVersion");
        if (!string.Equals(receipt.RootElement.GetProperty("id").GetString(), target, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.GetProperty("roleDefinitionId").GetString(), definition, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.GetProperty("scope").GetString(), scope, StringComparison.OrdinalIgnoreCase)
            || actual.GetProperty("principalId").GetString() != authority.AuthPrincipalId
            || actual.GetProperty("principalType").GetString() != "ServicePrincipal"
            || actual.GetProperty("createdBy").GetString() != identity.AzureActorId
            || created < operation.StartedAt || created > clock.GetUtcNow()
            || conditionVersion is not (null or "2.0")
            || actual.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null)
            throw new JsonException();
        operation = operation with { State = FoundationOperationState.Completed, Disposition = FoundationResourceDisposition.Created,
            ResourceId = target, Incarnation = created.ToString("O"), ConditionVersion = conditionVersion, HttpStatus = 201,
            ProviderRequestId = HeaderId("x-ms-request-id"), ProviderCorrelationId = HeaderId("x-ms-correlation-request-id"),
            ResponseDate = response.Headers.Date };
        await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
        operations[^1] = operation;

        static bool Matches(string pattern, string value) =>
            Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);
        string? HeaderId(string name) => response.Headers.TryGetValues(name, out var values)
            && Guid.TryParse(values.Single(), out var id) && id != Guid.Empty ? id.ToString() : null;
    }

    private void ValidateProof(FactoryGitHubAuthority authority, GitHubAuthorityProof proof)
    {
        if (!Enum.IsDefined(proof.State) || proof.FactoryId != authority.FactoryId || proof.OperationId != authority.OperationId
            || proof.ObservedAt < clock.GetUtcNow().AddMinutes(-5) || proof.ObservedAt > clock.GetUtcNow().AddMinutes(1))
            throw new JsonException();
        if (proof.State != GitHubAuthorityState.Verified) return;
        if (!FactoryGitHubAuthorityValidation.HasVerifiedScope(authority, proof) || proof.TokenExpiresAt <= clock.GetUtcNow())
            throw new JsonException();
    }

}
