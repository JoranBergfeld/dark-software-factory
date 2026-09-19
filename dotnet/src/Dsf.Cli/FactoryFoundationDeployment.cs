using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryFoundationService
{
    private async Task<(ConfigurationSetting Root, FactoryReservationAnchor Anchor)> DeployBaseAsync(
        FactoryReservationResult admission, OwnerReservationStore store, ConfigurationSetting root,
        FactoryReservationAnchor anchor, CancellationToken token)
    {
        var assets = new FactoryFoundationAssets(anchor.Identity, admission.Plan!.Selection);
        var body = assets.Request();
        var deployment = assets.Deployment(anchor.Foundation!.OperationId);
        var targets = assets.Targets();
        var clientRequestId = Guid.NewGuid();
        var credential = new OwnerReservationStore.OperatorCredential(azure, anchor.Identity.TenantId,
            anchor.Identity.SubscriptionId, anchor.Identity.AzureActorId, "https://management.azure.com/");
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
        foreach (var target in targets)
        {
            using var response = await ReadArmAsync(target.Id, target.ApiVersion, access.Token, token);
            if (response.StatusCode != HttpStatusCode.NotFound)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "A base target is not proven absent; no deployment or adoption.");
        }
        var receipts = new List<(FoundationOperation Operation, ConfigurationSetting Record)>();
        foreach (var target in targets)
        {
            var pointer = anchor.Foundation!;
            var operation = new FoundationOperation(1, OperationKind, anchor.Identity.FactoryId, pointer.OperationId,
                pointer.OperationCount, target.Id, target.ApiVersion, FactoryReservationRecords.Hash(body), clientRequestId,
                clock.GetUtcNow(), FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested)
            { DeploymentTarget = deployment, LogicalIdentity = target.LogicalIdentity, ManagedBy = target.ManagedBy };
            anchor = anchor with { Foundation = pointer with { OperationCount = pointer.OperationCount + 1, Stage = "base-deployment" } };
            root = await SaveRootAsync(store, root, anchor, token);
            var record = await store.WriteAsync(ProductConfigurationKeys.FoundationOperation(pointer.OperationId, operation.Index),
                FactoryReservationRecords.Serialize(operation), null, token);
            receipts.Add((operation, record));
            confirmedOperations[operation.Index] = operation;
        }
        var dispatched = await DispatchAsync(store, root, anchor, deployment, "2025-04-01", body, token, clientRequestId);
        root = dispatched.Root;
        anchor = dispatched.Anchor;
        if (dispatched.Operation.State is not (FoundationOperationState.Completed or FoundationOperationState.Failed or FoundationOperationState.Canceled)) return (root, anchor);

        return await ReconcileDeploymentAsync(store, root, anchor, assets, dispatched.Operation, receipts, access.Token, token);
    }

    private async Task<(ConfigurationSetting Root, FactoryReservationAnchor Anchor)> ReconcileDeploymentAsync(
        OwnerReservationStore store, ConfigurationSetting root, FactoryReservationAnchor anchor, FactoryFoundationAssets assets,
        FoundationOperation dispatched, IReadOnlyList<(FoundationOperation Operation, ConfigurationSetting Record)> receipts,
        string access, CancellationToken token, bool verifyOnly = false)
    {
        var deployment = dispatched.Target;
        var targets = assets.Targets();
        using var operationsResponse = await ReadArmAsync(deployment + "/operations", "2025-04-01", access, token);
        if (!operationsResponse.IsSuccessStatusCode)
            throw new ReservationStoreException(FactoryReservationState.Unknown, "Deployment operation receipts unavailable; resource ownership is unproven.");
        using var operationsJson = JsonDocument.Parse(await operationsResponse.Content.ReadAsStringAsync(token));
        var operations = operationsJson.RootElement.GetProperty("value").EnumerateArray().ToArray();
        if (operationsJson.RootElement.TryGetProperty("nextLink", out var next) && !string.IsNullOrEmpty(next.GetString()))
            throw new ReservationStoreException(FactoryReservationState.Unknown, "Deployment receipts are paginated; incomplete ownership proof remains held.");
        var actualTargets = operations.Where(o => o.GetProperty("properties").TryGetProperty("targetResource", out var target)
                && target.TryGetProperty("id", out _))
            .Select(o => o.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString()!.ToLowerInvariant()).ToArray();
        if (actualTargets.Except(targets.Select(t => t.Id), StringComparer.Ordinal).Any())
            throw new ReservationStoreException(FactoryReservationState.Unknown, "Deployment returned unexpected resource operations; no completion or further dispatch.");
        var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (intent, record) in receipts)
        {
            if (intent.ManagedBy is not null)
            {
                var parent = confirmedOperations.Values.Single(o => o.Target == intent.ManagedBy);
                if (parent.State != FoundationOperationState.Completed) continue;
                using var groupResponse = await ReadArmAsync(intent.Target, intent.ApiVersion, access, token);
                if (!groupResponse.IsSuccessStatusCode) throw new JsonException();
                using var groupJson = JsonDocument.Parse(await groupResponse.Content.ReadAsStringAsync(token));
                if (!string.Equals(groupJson.RootElement.GetProperty("id").GetString(), intent.Target, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(groupJson.RootElement.GetProperty("managedBy").GetString(), intent.ManagedBy, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException();
                var owned = intent with
                {
                    State = FoundationOperationState.Completed,
                    Disposition = FoundationResourceDisposition.Created,
                    ResourceId = intent.Target,
                    ProviderCorrelationId = parent.ProviderCorrelationId,
                    Incarnation = "provider-managed-by:" + parent.ResourceId + ";creation:" + parent.Incarnation,
                    ProviderOperationId = parent.ProviderOperationId,
                };
                if (!verifyOnly) await SaveReconciledReceiptAsync(store, record, intent, owned, token);
                else RequireUnchangedReceipt(intent, owned);
                confirmedOperations[owned.Index] = owned;
                continue;
            }
            var matches = operations.Where(o => o.GetProperty("properties").TryGetProperty("targetResource", out var target)
                && target.GetProperty("id").GetString()?.Equals(intent.Target, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (matches.Length != 1) continue;
            var receipt = matches[0];
            var properties = receipt.GetProperty("properties");
            var statusCode = properties.GetProperty("statusCode").GetString();
            if (properties.GetProperty("provisioningOperation").GetString() == "Create"
                && properties.GetProperty("provisioningState").GetString() is "Failed" or "Canceled")
            {
                var failed = intent with
                {
                    State = properties.GetProperty("provisioningState").GetString() == "Canceled"
                        ? FoundationOperationState.Canceled : FoundationOperationState.Failed,
                    Disposition = FoundationResourceDisposition.Unproven,
                    ProviderCorrelationId = dispatched.ProviderCorrelationId,
                };
                if (!verifyOnly) await SaveReconciledReceiptAsync(store, record, intent, failed, token);
                else RequireUnchangedReceipt(intent, failed);
                confirmedOperations[failed.Index] = failed;
                continue;
            }
            if (properties.GetProperty("provisioningOperation").GetString() != "Create"
                || properties.GetProperty("provisioningState").GetString() != "Succeeded"
                || statusCode is not ("Created" or "201" or "OK" or "200"))
                throw new ReservationStoreException(FactoryReservationState.Unknown, "Resource receipt does not prove creation; no name/tag adoption.");
            using var resourceResponse = await ReadArmAsync(intent.Target, intent.ApiVersion, access, token);
            if (!resourceResponse.IsSuccessStatusCode) throw new JsonException();
            using var resourceJson = JsonDocument.Parse(await resourceResponse.Content.ReadAsStringAsync(token));
            var resource = resourceJson.RootElement;
            if (!string.Equals(resource.GetProperty("id").GetString(), intent.Target, StringComparison.OrdinalIgnoreCase))
                throw new JsonException();
            if (verifyOnly && resource.GetProperty("properties").TryGetProperty("provisioningState", out var currentState)
                && currentState.GetString() != "Succeeded")
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Current native resource is not in its completed provisioning state; recovery cannot adopt or overwrite changed provider work.");
            string? incarnation = null;
            if (intent.ApiVersion == "2020-02-02"
                && intent.Target.Contains("/providers/microsoft.insights/components/", StringComparison.Ordinal))
            {
                var native = resource.GetProperty("properties");
                var appId = ProviderGuid(native, "AppId");
                var createdAt = native.GetProperty("CreationDate").GetDateTimeOffset();
                if (createdAt < intent.StartedAt || createdAt > clock.GetUtcNow()
                    || resource.GetProperty("type").GetString() != "Microsoft.Insights/components"
                    || native.GetProperty("provisioningState").GetString() != "Succeeded"
                    || !string.Equals(native.GetProperty("WorkspaceResourceId").GetString(),
                        assets.Group + "/providers/microsoft.operationalinsights/workspaces/" + assets.Prefix + "-logs",
                        StringComparison.OrdinalIgnoreCase)
                    || native.GetProperty("publicNetworkAccessForIngestion").GetString() != "Disabled"
                    || native.GetProperty("publicNetworkAccessForQuery").GetString() != "Disabled"
                    || resource.TryGetProperty("systemData", out var supplemental)
                        && (supplemental.GetProperty("createdAt").GetDateTimeOffset() != createdAt
                            || supplemental.TryGetProperty("lastModifiedAt", out var changed) && changed.GetDateTimeOffset() != createdAt))
                    throw new ReservationStoreException(FactoryReservationState.Unknown,
                        "AppInsights native creation evidence is contradictory; no ownership adoption or telemetry readiness.");
                incarnation = "appinsights-app-id:" + appId.ToString("D") + ";created:" + createdAt.ToUniversalTime().ToString("O");
            }
            else if (resource.TryGetProperty("systemData", out var systemData))
            {
                var createdAt = systemData.GetProperty("createdAt").GetDateTimeOffset();
                if (createdAt < intent.StartedAt
                    || systemData.TryGetProperty("lastModifiedAt", out var modified) && modified.GetDateTimeOffset() != createdAt)
                    throw new ReservationStoreException(FactoryReservationState.Unknown,
                        "A resource has an earlier or modified incarnation; no ownership adoption or completion.");
                incarnation = createdAt.ToUniversalTime().ToString("O");
            }
            else if (statusCode is "Created" or "201"
                && resource.TryGetProperty("etag", out var etag) && etag.GetString() is { Length: > 0 } revision)
                incarnation = "etag-sha256:" + FactoryReservationRecords.Hash(revision);
            else if (statusCode is "Created" or "201" && intent.Target.Contains("/sqldatabases/", StringComparison.Ordinal)
                && resource.GetProperty("properties").TryGetProperty("resource", out var cosmosResource))
            {
                var rid = cosmosResource.GetProperty("_rid").GetString() ?? throw new JsonException();
                var decoded = new byte[intent.Target.Contains("/containers/", StringComparison.Ordinal) ? 8 : 4];
                // Cosmos substitutes '-' for '/', not the '+' substitution used by Base64URL.
                var normalized = rid.Replace('-', '/');
                if (rid.Length != (decoded.Length + 2) / 3 * 4
                    || !Convert.TryFromBase64String(normalized, decoded, out var length) || length != decoded.Length
                    || Convert.ToBase64String(decoded) != normalized)
                    throw new JsonException();
                incarnation = "cosmos-rid-sha256:" + FactoryReservationRecords.Hash(rid);
            }
            if (string.IsNullOrWhiteSpace(incarnation)) throw new JsonException();
            var trackingId = ProviderGuid(properties, properties.TryGetProperty("serviceRequestId", out _) ? "serviceRequestId" : "trackingId");
            var operationId = receipt.GetProperty("operationId").GetString() ?? throw new JsonException();
            if (operationId.Length is < 1 or > 128
                || operationId.Any(c => !char.IsAsciiHexDigit(c) && c != '-'))
                throw new JsonException();
            var completed = intent with
            {
                State = FoundationOperationState.Completed,
                Disposition = FoundationResourceDisposition.Created,
                ResourceId = intent.Target,
                Incarnation = incarnation,
                HttpStatus = statusCode is "Created" or "201" ? 201 : 200,
                ProviderOperationId = operationId,
                ProviderRequestId = trackingId.ToString(),
                ProviderCorrelationId = dispatched.ProviderCorrelationId,
            };
            if (intent.LogicalIdentity is not null)
            {
                var props = resource.GetProperty("properties");
                var principal = ProviderGuid(props, "principalId").ToString();
                var client = ProviderGuid(props, "clientId").ToString();
                if (props.GetProperty("tenantId").GetString() != anchor.Identity.TenantId || !principals.Add(principal))
                    throw new JsonException();
                completed = completed with { PrincipalId = principal, ClientId = client };
            }
            if (verifyOnly && intent.State != FoundationOperationState.Completed)
                RequireProviderCreationIdentity(properties, resource, completed);
            if (!verifyOnly) await SaveReconciledReceiptAsync(store, record, intent, completed, token);
            else RequireUnchangedReceipt(intent, completed);
            confirmedOperations[completed.Index] = completed;
        }
        return verifyOnly ? (root, anchor) : await CompleteDeploymentAsync(store, root, anchor, token);
    }

    private async Task<(ConfigurationSetting Root, FactoryReservationAnchor Anchor)> CompleteDeploymentAsync(
        OwnerReservationStore store, ConfigurationSetting root, FactoryReservationAnchor anchor, CancellationToken token)
    {
        var complete = confirmedOperations.Values.All(o => o.State == FoundationOperationState.Completed);
        var unknown = confirmedOperations.Values.Any(o => o.State is FoundationOperationState.Intent or FoundationOperationState.InFlight or FoundationOperationState.Unknown);
        var canceled = confirmedOperations.Values.Any(o => o.State == FoundationOperationState.Canceled);
        anchor = anchor with
        {
            Foundation = anchor.Foundation! with
            {
                State = complete ? FactoryFoundationState.Inactive : unknown ? FactoryFoundationState.Unknown
                    : canceled ? FactoryFoundationState.Canceled : FactoryFoundationState.Failed,
                Stage = complete ? "inactive-foundation" : "base-deployment",
            }
        };
        root = await SaveRootAsync(store, root, anchor, token);
        return (root, anchor);
    }

    private static async Task SaveReconciledReceiptAsync(OwnerReservationStore store, ConfigurationSetting record,
        FoundationOperation original, FoundationOperation current, CancellationToken token)
    {
        RequireUnchangedReceipt(original, current);
        if (original.State == FoundationOperationState.Completed)
            return;
        if (original != current)
            await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(current), record.ETag, token);
    }

    private static void RequireUnchangedReceipt(FoundationOperation original, FoundationOperation current)
    {
        if (original.State is FoundationOperationState.Failed or FoundationOperationState.Canceled
            && original.Disposition != FoundationResourceDisposition.NoEffect && current.State != original.State)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Current native result contradicts an original terminal failure/cancellation; that receipt is retained, not rewritten into success. Administrator reconciliation required.");
        if (original.State == FoundationOperationState.Completed
            && FactoryReservationRecords.Serialize(original) != FactoryReservationRecords.Serialize(current))
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Current native incarnation/ownership differs from the original receipt; retained evidence is not rewritten. Administrator reconciliation required.");
    }

    private static void RequireProviderCreationIdentity(JsonElement operation, JsonElement current, FoundationOperation receipt)
    {
        // Optional native response content is used only in memory. Never enable ARM debug capture or
        // retain whole responses: they can contain instrumentation keys and other provider secrets.
        if (!operation.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("content", out var original) || original.ValueKind != JsonValueKind.Object)
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Original native per-target creation response is unavailable; current names/IDs and a terminal deployment cannot recover lost incarnation receipts. Administrator reconciliation required.");
        if (!string.Equals(original.GetProperty("id").GetString(), receipt.Target, StringComparison.OrdinalIgnoreCase))
            throw new JsonException();
        var matches = receipt.Incarnation switch
        {
            { } value when value.StartsWith("appinsights-app-id:", StringComparison.Ordinal) =>
                Same("properties", "AppId") && Same("properties", "CreationDate"),
            { } value when value.StartsWith("cosmos-rid-sha256:", StringComparison.Ordinal) =>
                Same("properties", "resource", "_rid"),
            { } value when value.StartsWith("etag-sha256:", StringComparison.Ordinal) => Same("etag"),
            _ => Same("systemData", "createdAt"),
        };
        if (receipt.LogicalIdentity is not null)
            matches &= Same("properties", "principalId") && Same("properties", "clientId") && Same("properties", "tenantId");
        if (!matches)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Native creation response and current resource incarnation differ; a recreated resource ID cannot be adopted. Original ownership evidence remains held.");

        bool Same(params string[] path)
        {
            var left = original;
            var right = current;
            foreach (var part in path) { left = left.GetProperty(part); right = right.GetProperty(part); }
            return JsonElement.DeepEquals(left, right);
        }
    }

    private async Task<HttpResponseMessage> ReadArmAsync(string target, string version, string access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://management.azure.com{target}?api-version={version}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return await (http ?? ArmHttp).SendAsync(request, token);
    }
}
