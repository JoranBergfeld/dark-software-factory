using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryFoundationService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    private static readonly HttpClient ArmHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private const string OperationKind = "decide-foundation-operation";
    private FactoryReservationAnchor? confirmedAnchor;
    private ConfigurationSetting? confirmedRoot;
    private bool registryDispatchStarted;
    private readonly Dictionary<int, FoundationOperation> confirmedOperations = [];

    public async Task<FactoryReservationResult> ProvisionAsync(FactoryReservationResult admission, CancellationToken token)
    {
        if (admission.State != FactoryReservationState.Reserved || admission.Identity is null || admission.Plan is null)
            return admission;
        var identity = admission.Identity;
        _ = new FactoryFoundationAssets(identity, admission.Plan.Selection).Request();
        var store = Store(identity);
        var root = await store.ReadAsync(ProductConfigurationKeys.OwnerProductClaim(identity.ProductKey), token)
            ?? throw new ReservationStoreException(FactoryReservationState.Unknown, "Admission disappeared; no dispatch.");
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(root.Value);
        if (root.ETag.ToString() != admission.Revision || anchor.Identity != identity)
            return admission with { State = FactoryReservationState.Blocked, Detail = "Admission changed before foundation dispatch." };
        if (anchor.Foundation is not null) return await InspectAsync(admission, anchor.Foundation, token);
        anchor = anchor with { Foundation = new(Guid.NewGuid(), FactoryFoundationState.Partial, 0, "resource-group") };
        try
        {
            root = await SaveRootAsync(store, root, anchor, token);
            var group = $"/subscriptions/{identity.SubscriptionId}/resourcegroups/{admission.Plan.Selection.FactoryResourceGroup.ToLowerInvariant()}";
            var body = JsonSerializer.Serialize(new { location = admission.Plan.Selection.FactoryLocation });
            var response = await DispatchAsync(store, root, anchor, group, "2021-04-01", body, token);
            root = response.Root;
            anchor = response.Anchor;
            if (response.Operation.State != FoundationOperationState.Completed)
                return await InspectAsync(admission, anchor.Foundation!, token);
            anchor = anchor with { Foundation = anchor.Foundation! with { Stage = "product-configuration" } };
            root = await SaveRootAsync(store, root, anchor, token);
            var configName = "dsfo-" + identity.FactoryId.ToString("N");
            var configTarget = group + "/providers/microsoft.appconfiguration/configurationstores/" + configName;
            var configBody = JsonSerializer.Serialize(new
            {
                location = admission.Plan.Selection.FactoryLocation,
                sku = new { name = "standard" },
                properties = new { disableLocalAuth = true, softDeleteRetentionInDays = 7, enablePurgeProtection = true, publicNetworkAccess = "Enabled" },
            });
            response = await DispatchAsync(store, root, anchor, configTarget, "2024-06-01", configBody, token);
            root = response.Root;
            anchor = response.Anchor;
            if (response.Operation.State != FoundationOperationState.Completed)
                return await InspectAsync(admission, anchor.Foundation!, token);
            var endpoint = "https://" + configName + ".azconfig.io";
            if (response.Operation.Endpoint?.TrimEnd('/') != endpoint)
                throw new ReservationStoreException(FactoryReservationState.Unknown, "Provider did not confirm the exact product endpoint; no product writes.");
            var profile = new FactoryOnboardingProfile(1, "onboard-decide-inactive-v1", identity.FactoryId, identity.PlanFingerprint, []);
            var profileValue = FactoryReservationRecords.Serialize(profile);
            var pin = FactoryReservationRecords.Hash(profileValue);
            anchor = anchor with
            {
                Foundation = anchor.Foundation! with { Stage = "product-authority-pending", ProductEndpoint = endpoint, ProfilePin = pin },
            };
            root = await SaveRootAsync(store, root, anchor, token);
            var product = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
            (root, anchor) = await PublishAsync(store, product, root, anchor, ProductConfigurationKeys.FactoryProfile, profileValue, token);
            for (var i = 0; i < anchor.PlanChunks; i++)
            {
                var chunk = await store.ReadAsync(ProductConfigurationKeys.OwnerReservationPlanChunk(identity.FactoryId, i), token)
                    ?? throw new ReservationStoreException(FactoryReservationState.Unknown, "Reviewed plan chunk disappeared; authority handoff stays pending.");
                (root, anchor) = await PublishAsync(store, product, root, anchor, ProductConfigurationKeys.FactoryPlanChunk(i), chunk.Value, token);
            }
            var manifest = new FactoryManifest(1, "decide-factory-manifest", "product-app-configuration", "Inactive",
                identity, anchor.PlanSha256, anchor.PlanBytes, anchor.PlanChunks, pin);
            (root, anchor) = await PublishAsync(store, product, root, anchor, ProductConfigurationKeys.FactoryManifest,
                FactoryReservationRecords.Serialize(manifest), token);
            anchor = anchor with { Foundation = anchor.Foundation! with { Stage = "product-authoritative" } };
            root = await SaveRootAsync(store, root, anchor, token);
            (root, anchor) = await DeployBaseAsync(admission, store, root, anchor, token);
            return await InspectAsync(admission, anchor.Foundation!, token);
        }
        catch (ReservationStoreException e)
        {
            var state = token.IsCancellationRequested ? FactoryReservationState.Canceled : e.State;
            if (state is FactoryReservationState.Blocked or FactoryReservationState.Failed && confirmedRoot is not null && confirmedAnchor is not null)
            {
                try
                {
                    await SaveRootAsync(store, confirmedRoot, confirmedAnchor with
                    { Foundation = confirmedAnchor.Foundation! with { State = Enum.Parse<FactoryFoundationState>(state.ToString()) } }, token);
                }
                catch (ReservationStoreException) { state = FactoryReservationState.Unknown; }
            }
            return FailureResult(admission, confirmedAnchor ?? anchor, state, e.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return FailureResult(admission, confirmedAnchor ?? anchor, FactoryReservationState.Canceled, "Canceled; local dispatch closed. In-flight ARM may continue; inspect unknown receipts. No rollback or reissue."); }
        catch (OperationCanceledException)
        { return FailureResult(admission, confirmedAnchor ?? anchor, FactoryReservationState.Unknown, "Provider inspection timed out; absence or completion was not established. No further dispatch."); }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or FormatException
            or KeyNotFoundException or OwnerReservationStore.AuthenticationFailedException)
        { return FailureResult(admission, confirmedAnchor ?? anchor, FactoryReservationState.Unknown, "Foundation authority or provider receipt unavailable/invalid; no further dispatch. Provider details withheld."); }
    }

    private async Task<(ConfigurationSetting Root, FactoryReservationAnchor Anchor, FoundationOperation Operation)> DispatchAsync(
        OwnerReservationStore store, ConfigurationSetting root, FactoryReservationAnchor anchor,
        string target, string apiVersion, string body, CancellationToken token, Guid? clientRequestId = null)
    {
        var pointer = anchor.Foundation!;
        var credential = new OwnerReservationStore.OperatorCredential(azure, anchor.Identity.TenantId,
            anchor.Identity.SubscriptionId, anchor.Identity.AzureActorId, "https://management.azure.com/");
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
        var uri = new Uri($"https://management.azure.com{target}?api-version={apiVersion}");
        using (var read = new HttpRequestMessage(HttpMethod.Get, uri))
        {
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            using var observed = await (http ?? ArmHttp).SendAsync(read, token);
            if (observed.StatusCode != HttpStatusCode.NotFound)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Target absence is not established. Existing names/tags never authorize adoption; no dispatch.");
        }
        var operation = new FoundationOperation(1, OperationKind, anchor.Identity.FactoryId, pointer.OperationId,
            pointer.OperationCount, target, apiVersion, FactoryReservationRecords.Hash(body), clientRequestId ?? Guid.NewGuid(), clock.GetUtcNow(),
            FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested);
        anchor = anchor with { Foundation = pointer with { OperationCount = pointer.OperationCount + 1 } };
        root = await SaveRootAsync(store, root, anchor, token);
        var key = ProductConfigurationKeys.FoundationOperation(pointer.OperationId, operation.Index);
        var record = await store.WriteAsync(key, FactoryReservationRecords.Serialize(operation), null, token);
        confirmedOperations[operation.Index] = operation;
        try
        {
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            request.Headers.Add("x-ms-client-request-id", operation.ClientRequestId.ToString("D"));
            using var receipt = await (http ?? ArmHttp).SendAsync(request, token);
            var providerIds = receipt.Headers.TryGetValues("x-ms-request-id", out var ids) ? ids.ToArray() : [];
            operation = operation with
            {
                HttpStatus = (int)receipt.StatusCode,
                ProviderRequestId = providerIds.Length == 1 && Guid.TryParse(providerIds[0], out var requestId) && requestId != Guid.Empty
                    ? requestId.ToString() : null,
                ResponseDate = receipt.Headers.Date,
                State = FoundationOperationState.Unknown,
            };
            record = await store.WriteAsync(key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
            confirmedOperations[operation.Index] = operation;
            if (receipt.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                operation = operation with { State = FoundationOperationState.Failed };
            else
            {
                using var document = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync(token));
                operation = ReadCreationReceipt(operation, document.RootElement, receipt.StatusCode);
                if (operation.State == FoundationOperationState.InFlight)
                {
                    record = await store.WriteAsync(key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
                    confirmedOperations[operation.Index] = operation;
                    for (var poll = 0; poll < 120 && operation.State == FoundationOperationState.InFlight; poll++)
                    {
                        if (poll > 0) await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
                        using var observed = await ReadArmAsync(target, apiVersion, access.Token, token);
                        if (!observed.IsSuccessStatusCode) throw new JsonException();
                        using var current = JsonDocument.Parse(await observed.Content.ReadAsStringAsync(token));
                        operation = ReadCreationReceipt(operation, current.RootElement, HttpStatusCode.Created);
                    }
                    if (operation.State == FoundationOperationState.InFlight)
                        operation = operation with { State = FoundationOperationState.Unknown };
                }
            }
            await store.WriteAsync(key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
            confirmedOperations[operation.Index] = operation;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            // A lost response never authorizes reissue, even if a later GET says absent.
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
        }
        return (root, anchor, operation);
    }

    private async Task<(ConfigurationSetting Root, FactoryReservationAnchor Anchor)> PublishAsync(
        OwnerReservationStore owner, OwnerReservationStore product, ConfigurationSetting root, FactoryReservationAnchor anchor,
        string key, string value, CancellationToken token)
    {
        var pointer = anchor.Foundation!;
        var operation = new FoundationOperation(1, OperationKind, anchor.Identity.FactoryId, pointer.OperationId,
            pointer.OperationCount, pointer.ProductEndpoint + "/kv/" + Uri.EscapeDataString(key), "1.0",
            FactoryReservationRecords.Hash(value), Guid.NewGuid(), DateTimeOffset.UtcNow,
            FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested);
        anchor = anchor with { Foundation = pointer with { OperationCount = pointer.OperationCount + 1 } };
        root = await SaveRootAsync(owner, root, anchor, token);
        var operationKey = ProductConfigurationKeys.FoundationOperation(pointer.OperationId, operation.Index);
        var intent = await owner.WriteAsync(operationKey, FactoryReservationRecords.Serialize(operation), null, token);
        confirmedOperations[operation.Index] = operation;
        ConfigurationSetting receipt;
        try { receipt = await product.WriteAsync(key, value, null, token, operation.ClientRequestId); }
        catch (ReservationStoreException e) when (e.State == FactoryReservationState.Failed && !token.IsCancellationRequested)
        {
            operation = operation with { State = FoundationOperationState.Failed };
            await owner.WriteAsync(operationKey, FactoryReservationRecords.Serialize(operation), intent.ETag, token);
            confirmedOperations[operation.Index] = operation;
            throw new ReservationStoreException(e.State,
                "Product App Configuration rejected publication. Product data-plane authority/network access is required; no grants or network repairs were attempted.");
        }
        operation = operation with
        {
            State = FoundationOperationState.Completed,
            Disposition = FoundationResourceDisposition.Created,
            Incarnation = receipt.ETag.ToString(),
            ResourceId = operation.Target,
        };
        await owner.WriteAsync(operationKey, FactoryReservationRecords.Serialize(operation), intent.ETag, token);
        confirmedOperations[operation.Index] = operation;
        return (root, anchor);
    }

    private async Task<ConfigurationSetting> SaveRootAsync(
        OwnerReservationStore store, ConfigurationSetting root, FactoryReservationAnchor anchor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        registryDispatchStarted = true;
        var saved = await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(anchor), root.ETag, token);
        confirmedRoot = saved;
        confirmedAnchor = anchor;
        return saved;
    }

    private FactoryReservationResult FailureResult(
        FactoryReservationResult admission, FactoryReservationAnchor anchor, FactoryReservationState state, string detail) =>
        admission with
        {
            State = state,
            Detail = detail,
            Revision = confirmedRoot?.ETag.ToString() ?? admission.Revision,
            HasRegistryEffects = registryDispatchStarted,
            Foundation = confirmedRoot is null && state is FactoryReservationState.Blocked or FactoryReservationState.Failed
                ? null : new(Enum.Parse<FactoryFoundationState>(state.ToString()), anchor.Foundation!.Stage, detail,
                anchor.Foundation.OperationId, confirmedOperations.OrderBy(p => p.Key).Select(p => p.Value.State is FoundationOperationState.Intent or FoundationOperationState.InFlight
                    ? p.Value with { State = FoundationOperationState.Unknown } : p.Value).ToArray())
                { ProductEndpoint = anchor.Foundation.ProductEndpoint, ProfilePin = anchor.Foundation.ProfilePin },
        };

    public async Task<FactoryReservationResult> InspectAsync(
        FactoryReservationResult admission, FactoryFoundationPointer pointer, CancellationToken token)
    {
        var operations = new List<FoundationOperation>();
        var state = pointer.State;
        var detail = "Inactive foundation progress only; no workload readiness or activation. "
            + "Uncertain dispatch stays held. Resume inspects receipts only; never reissues or rolls back.";
        try
        {
            if (pointer.OperationId == Guid.Empty || pointer.OperationCount is < 0 or > 1024)
                throw new JsonException();
            if (!Enum.IsDefined(pointer.State) || pointer.Stage is not ("resource-group" or "product-configuration"
                    or "product-authority-pending" or "product-authoritative" or "base-deployment" or "inactive-foundation")
                || (pointer.State == FactoryFoundationState.Inactive) != (pointer.Stage == "inactive-foundation"))
                throw new JsonException();
            var store = Store(admission.Identity!, bindActor: false);
            var group = new FactoryFoundationAssets(admission.Identity!, admission.Plan!.Selection).Group;
            var assets = new FactoryFoundationAssets(admission.Identity!, admission.Plan!.Selection);
            for (var i = 0; i < pointer.OperationCount; i++)
            {
                var record = await store.ReadAsync(ProductConfigurationKeys.FoundationOperation(pointer.OperationId, i), token);
                if (record is null) { state = FactoryFoundationState.Unknown; break; }
                var operation = FactoryReservationRecords.Parse<FoundationOperation>(record.Value);
                if (operation.SchemaVersion != 1 || operation.Kind != OperationKind || operation.FactoryId != admission.FactoryId
                    || operation.OperationId != pointer.OperationId || operation.Index != i || !Enum.IsDefined(operation.State)
                    || !Enum.IsDefined(operation.Disposition) || operation.ClientRequestId == Guid.Empty || operation.StartedAt == default
                    || operation.RequestSha256.Length != 64 || operation.RequestSha256.Any(c => !char.IsAsciiHexDigit(c))
                    || !(operation.Target == group || operation.Target.StartsWith(group + "/providers/", StringComparison.Ordinal)
                        || operation.Target == assets.HostingPlatformGroup && operation.ManagedBy == assets.HostingEnvironment
                        || pointer.ProductEndpoint is not null && operation.Target.StartsWith(pointer.ProductEndpoint + "/kv/dsf%2Ffactory%2F", StringComparison.Ordinal))
                    || operation.State == FoundationOperationState.Completed
                        && (operation.Disposition != FoundationResourceDisposition.Created || operation.ResourceId != operation.Target))
                    throw new JsonException();
                operations.Add(operation.State is FoundationOperationState.Intent or FoundationOperationState.InFlight
                    ? operation with { State = FoundationOperationState.Unknown } : operation);
            }
            if (operations.Any(o => o.State == FoundationOperationState.Unknown)) state = FactoryFoundationState.Unknown;
            else if (state != FactoryFoundationState.Unknown && operations.Any(o => o.State == FoundationOperationState.Failed)) state = FactoryFoundationState.Failed;
            if (pointer.State == FactoryFoundationState.Inactive)
            {
                var planBytes = Encoding.UTF8.GetByteCount(BoundaryPlanFingerprint.Canonical(JsonSerializer.SerializeToElement(admission.Plan, FactoryReservationRecords.Json)));
                var expected = (planBytes + FactoryReservationRecords.ChunkBytes - 1) / FactoryReservationRecords.ChunkBytes
                    + new FactoryFoundationAssets(admission.Identity!, admission.Plan!.Selection).Targets().Count + 5;
                if (operations.Count != expected || pointer.ProductEndpoint is null || pointer.ProfilePin is not { Length: 64 })
                    throw new JsonException();
            }
            if (pointer.ProductEndpoint is not null)
            {
                var identity = admission.Identity!;
                if (pointer.ProductEndpoint != "https://dsfo-" + identity.FactoryId.ToString("N") + ".azconfig.io")
                    throw new JsonException();
                var product = new OwnerReservationStore(pointer.ProductEndpoint, identity.TenantId, identity.SubscriptionId, azure, http);
                var admittedPlanSha256 = FactoryReservationRecords.Hash(BoundaryPlanFingerprint.Canonical(
                    JsonSerializer.SerializeToElement(admission.Plan ?? throw new JsonException(), FactoryReservationRecords.Json)));
                admission = admission with
                {
                    Plan = await new FactoryManifestStore(product).ReadReviewedPlanAsync(identity, pointer, admittedPlanSha256, token),
                };
            }
        }
        catch (Exception e) when (e is ReservationStoreException or JsonException or NullReferenceException or FormatException)
        {
            state = FactoryFoundationState.Unknown;
            detail = "Foundation receipts or required authoritative product manifest/profile could not be verified. "
                + "No owner-archive fallback, reissue or activation; provider details withheld.";
            admission = admission with { Plan = null };
        }
        return admission with
        {
            State = state == FactoryFoundationState.Inactive ? FactoryReservationState.Reserved : Enum.Parse<FactoryReservationState>(state.ToString()),
            Detail = detail,
            HasRegistryEffects = registryDispatchStarted,
            Revision = confirmedRoot?.ETag.ToString() ?? admission.Revision,
            Foundation = new(state, pointer.Stage, detail, pointer.OperationId, operations)
            { ProductEndpoint = pointer.ProductEndpoint, ProfilePin = pointer.ProfilePin },
        };
    }

    private OwnerReservationStore Store(FactoryReservationIdentity identity, bool bindActor = true) =>
        new(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId, azure, http, bindActor ? identity.AzureActorId : null);

    private static FoundationOperation ReadCreationReceipt(FoundationOperation operation, JsonElement resource, HttpStatusCode status)
    {
        if (status != HttpStatusCode.Created || !string.Equals(resource.GetProperty("id").GetString(), operation.Target, StringComparison.OrdinalIgnoreCase)
            || operation.ProviderRequestId is null || operation.ResponseDate is null)
            return operation with { State = FoundationOperationState.Unknown, Disposition = FoundationResourceDisposition.Unproven };
        var properties = resource.GetProperty("properties");
        var state = properties.GetProperty("provisioningState").GetString();
        var updated = operation with
        {
            State = state switch
            {
                "Succeeded" => FoundationOperationState.Completed,
                "Creating" or "Running" or "Accepted" or "Updating" => FoundationOperationState.InFlight,
                "Failed" or "Canceled" => FoundationOperationState.Failed,
                _ => FoundationOperationState.Unknown,
            },
            Disposition = FoundationResourceDisposition.Created,
            ResourceId = operation.Target,
        };
        if (operation.Target.Contains("/configurationstores/", StringComparison.Ordinal))
        {
            var expected = "https://" + operation.Target.Split('/').Last() + ".azconfig.io";
            if (properties.GetProperty("endpoint").GetString()?.TrimEnd('/') != expected) throw new JsonException();
            var created = properties.GetProperty("creationDate").GetDateTimeOffset();
            if (created < operation.StartedAt) throw new JsonException();
            updated = updated with { Endpoint = expected, Incarnation = created.ToUniversalTime().ToString("O") };
        }
        if (operation.Target.Contains("/deployments/", StringComparison.Ordinal))
        {
            var correlation = ProviderGuid(properties, "correlationId").ToString();
            if (operation.ProviderCorrelationId is not null && operation.ProviderCorrelationId != correlation) throw new JsonException();
            updated = updated with { ProviderCorrelationId = correlation };
        }
        return updated;
    }

    private static Guid ProviderGuid(JsonElement resource, string property)
    {
        if (resource.ValueKind != JsonValueKind.Object || !resource.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var parsed)
            || parsed == Guid.Empty)
            throw new JsonException();
        return parsed;
    }
}
