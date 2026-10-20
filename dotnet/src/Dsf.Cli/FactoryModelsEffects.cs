using System.Net;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryModelsService
{
    private FactoryModels? current;
    private ConfigurationSetting? root;
    private string? governance;
    private bool dispatched;
    private readonly List<FoundationOperation> effects = [];

    internal async Task<FactoryReservationResult> ApplyAsync(FactoryReservationResult admission, FactoryModels preview, string approval, CancellationToken token)
    {
        if (approval != preview.ApprovalFingerprint)
            return admission with { State = FactoryReservationState.Blocked, Detail = "Model approval differs; no effects." };
        var store = Store(admission);
        var arm = new FactoryPreparationArm(azure, http, admission.Identity!);
        try
        {
            CheckCapacity(preview);
            await CheckPrerequisitesAsync(admission, preview, arm, token);
            var (manifestRecord, manifest) = await ReadGovernanceAsync(admission, store, token);
            if (manifest.ModelsOperationId is not null || await store.ReadAsync(ProductConfigurationKeys.FactoryModels, token) is not null)
                throw new ReservationStoreException(FactoryReservationState.Unknown, "Existing model preparation intent held; no reconfiguration, adoption or retry.");
            current = preview with { OperationId = Guid.NewGuid(), Stage = "preparation", Effects = [] };
            var updated = manifest with { ModelsOperationId = current.OperationId, ModelsApprovalFingerprint = approval };
            governance = FactoryReservationRecords.Serialize(updated);
            OwnerReservationStore.CheckSize(manifestRecord.Key, ProductConfigurationKeys.NoLabel, governance);
            dispatched = true;
            await store.WriteAsync(manifestRecord.Key, governance, manifestRecord.ETag, token);
            await EnsureCurrentAsync(admission, store, token);
            for (var i = 0; i < 5; i++) await WriteReviewAsync(ProductConfigurationKeys.ModelRoute(current.OperationId, i), current.Routes![i]);
            for (var i = 0; i < 3; i++) await WriteReviewAsync(ProductConfigurationKeys.ModelBinding(current.OperationId, FactoryModelsValidation.BindingNames[i]), current.Bindings![i]);
            for (var i = 0; i < current.Grants!.Count; i++) await WriteReviewAsync(ProductConfigurationKeys.ModelGrant(current.OperationId, i), current.Grants[i]);
            root = await store.WriteAsync(ProductConfigurationKeys.FactoryModels, Serialize(current), null, token);
            foreach (var route in current.Routes!.Take(2))
            {
                var selection = route.Selection;
                var body = JsonSerializer.Serialize(new
                {
                    sku = new { name = selection.Sku, capacity = selection.Capacity },
                    properties = new { model = new { format = selection.Format, name = selection.Model, version = selection.Version },
                        versionUpgradeOption = "NoAutoUpgrade" },
                });
                await CreateAsync(FactoryModelsValidation.DeploymentTarget(selection), FactoryModelsValidation.ProviderApi, body, null);
            }
            foreach (var grant in current.Grants)
                await CreateAsync(grant.Target, "2022-04-01", JsonSerializer.Serialize(new { properties = new
                    { principalId = grant.PrincipalId, principalType = "ServicePrincipal", roleDefinitionId = grant.RoleDefinitionId } }), grant);
            await DeployHostAsync(admission, store, arm, token);
            current = current with { Stage = "prepared", State = ModelCapabilityState.Partial,
                EffectHashes = effects.Select(FactoryModelsValidation.Hash).ToArray(),
                Detail = "Prepared with exact creation receipts; effective hosted model workload paths are not yet verified." };
            await EnsureCurrentAsync(admission, store, token, expected: root!.Value);
            await SaveAsync(store, token);
            return Result(admission, current.State, current.Detail);

            async Task WriteReviewAsync<T>(string key, T value)
            {
                await EnsureCurrentAsync(admission, store, token);
                await store.WriteAsync(key, FactoryReservationRecords.Serialize(value), null, token);
            }
            async Task CreateAsync(string target, string version, string body, ModelAccessGrant? grant)
            {
                await CheckPrerequisitesAsync(admission, current!, arm, token);
                await AbsentAsync(arm, target, version, token);
                await EnsureCurrentAsync(admission, store, token);
                var (intent, record) = await IntentAsync(store, target, version, body, grant, token);
                await EnsureCurrentAsync(admission, store, token);
                using var response = await arm.SendAsync(HttpMethod.Put, target, version, token, body, intent.ClientRequestId);
                RequireCreated(response);
                using var native = await DocumentAsync(response, token);
                var resource = native.RootElement;
                if (grant is null)
                {
                    for (var poll = 0; resource.GetProperty("properties").GetProperty("provisioningState").GetString() is "Accepted" or "Creating" && poll < 120; poll++)
                    {
                        if (poll > 0) await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
                        using var observed = await GetAsync(arm, target, "deployment", token);
                        resource = observed.RootElement.Clone();
                    }
                }
                var observation = grant is null
                    ? FactoryModelsValidation.ObserveDeployment(current!.Routes!.Single(r => FactoryModelsValidation.DeploymentTarget(r.Selection) == target).Selection, resource)
                    : ObserveGrant(resource, grant, admission.Identity!.AzureActorId);
                var created = DateTimeOffset.Parse(observation.Incarnation, System.Globalization.CultureInfo.InvariantCulture);
                if (created < intent.StartedAt || created > clock.GetUtcNow()) throw new JsonException();
                if (grant is null && resource.GetProperty("systemData").GetProperty("createdBy").GetString() != admission.Identity!.AzureActorId) throw new JsonException();
                await CompleteAsync(store, record, intent with
                {
                    State = FoundationOperationState.Completed, Disposition = FoundationResourceDisposition.Created,
                    ResourceId = target, Incarnation = observation.Incarnation, NativeRevision = observation.Revision,
                    HttpStatus = 201, ProviderRequestId = Header(response, "x-ms-request-id"),
                    ProviderCorrelationId = Header(response, "x-ms-correlation-request-id"),
                    ProviderOperationId = Header(response, "x-ms-request-id"),
                    ConditionVersion = grant is null ? null : Optional(resource.GetProperty("properties"), "conditionVersion"),
                }, token);
            }
        }
        catch (ModelCapabilityException e) { return await CloseAsync(admission, store, ModelCapabilityState.Blocked, $"{e.Route}: {e.Reason}. {e.Message}", token); }
        catch (ReservationStoreException e) { return await CloseAsync(admission, store, Enum.Parse<ModelCapabilityState>(e.State.ToString()), e.Message, token); }
        catch (OperationCanceledException)
        { return Result(admission, ModelCapabilityState.Canceled, "Canceled; dispatched operations may continue. No current hold/revocation asserted; inspect same operation, no recreate or rollback."); }
        catch (Exception e) when (ProviderFailure(e))
        { return await CloseAsync(admission, store, ModelCapabilityState.Unknown, "Model preparation response or binding uncertain; held without retry. Inspect exact operation receipts; raw provider details withheld.", token); }
    }

    private async Task DeployHostAsync(FactoryReservationResult admission, OwnerReservationStore store, FactoryPreparationArm arm, CancellationToken token)
    {
        var config = current!.Configuration;
        var target = FactoryModelsValidation.HostTarget(current);
        var group = target[..target.IndexOf("/providers/", StringComparison.Ordinal)];
        var deployment = group + "/providers/microsoft.resources/deployments/md-" + current.OperationId.ToString("N");
        var templateBytes = File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-models.json"));
        if (FactoryReservationRecords.Hash(templateBytes) != config.HostTemplateSha256) throw new JsonException();
        using var template = JsonDocument.Parse(templateBytes);
        var body = JsonSerializer.Serialize(new { properties = new { mode = "Incremental", template = template.RootElement, parameters = new
        {
            namePrefix = new { value = "df-" + config.FactoryId.ToString("N")[..14] }, location = new { value = admission.Plan!.Selection.FactoryLocation },
            runtimeImage = new { value = config.RuntimeImage }, productKey = new { value = admission.Identity!.ProductKey },
            productEndpoint = new { value = config.ProductEndpoint }, profilePin = new { value = config.ProfilePin },
            tenantId = new { value = admission.Identity.TenantId }, audienceClientId = new { value = config.AudienceClientId },
            operatorPrincipalId = new { value = admission.Identity.AzureActorId },
        } } });
        await CheckPrerequisitesAsync(admission, current, arm, token);
        await AbsentAsync(arm, deployment, "2025-04-01", token);
        await AbsentAsync(arm, target, "2025-01-01", token);
        await AbsentAsync(arm, target + "/authconfigs/current", "2025-01-01", token);
        await EnsureCurrentAsync(admission, store, token);
        var host = await IntentAsync(store, target, "2025-01-01", body, null, token, deployment);
        var auth = await IntentAsync(store, target + "/authconfigs/current", "2025-01-01", body, null, token, deployment);
        await EnsureCurrentAsync(admission, store, token);
        using var response = await arm.SendAsync(HttpMethod.Put, deployment, "2025-04-01", token, body, host.Operation.ClientRequestId);
        RequireCreated(response);
        using var deployed = await DocumentAsync(response, token);
        var native = deployed.RootElement;
        var correlation = Guid.Parse(native.GetProperty("properties").GetProperty("correlationId").GetString()!);
        if (correlation == Guid.Empty || !Same(native.GetProperty("id").GetString(), deployment)) throw new JsonException();
        for (var poll = 0; native.GetProperty("properties").GetProperty("provisioningState").GetString() is "Accepted" or "Running" or "Creating" && poll < 120; poll++)
        {
            if (poll > 0) await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
            using var observed = await ReadNativeAsync(arm, deployment, "2025-04-01", token);
            native = observed.RootElement.Clone();
            if (!Same(native.GetProperty("id").GetString(), deployment)
                || native.GetProperty("properties").GetProperty("correlationId").GetString() != correlation.ToString()) throw new JsonException();
        }
        if (native.GetProperty("properties").GetProperty("provisioningState").GetString() != "Succeeded") throw new JsonException();
        using var operations = await ReadNativeAsync(arm, deployment + "/operations", "2025-04-01", token);
        var receipts = CompleteValues(operations.RootElement);
        if (receipts.Length != 2) throw new JsonException();
        foreach (var (intent, record) in new[] { host, auth })
        {
            var receipt = receipts.Single(r => Same(r.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString(), intent.Target));
            var properties = receipt.GetProperty("properties");
            var operation = receipt.GetProperty("operationId").GetString();
            if (!FactoryTelemetryValidation.IsProviderOperationId(operation) || properties.GetProperty("provisioningOperation").GetString() != "Create"
                || properties.GetProperty("provisioningState").GetString() != "Succeeded" || properties.GetProperty("statusCode").GetString() != "Created"
                || !Guid.TryParse(properties.GetProperty("trackingId").GetString(), out var tracking) || tracking == Guid.Empty) throw new JsonException();
            using var resource = await ReadNativeAsync(arm, intent.Target, "2025-01-01", token);
            var observation = ObserveHostResource(resource.RootElement, intent.Target);
            var created = DateTimeOffset.Parse(observation.Incarnation, System.Globalization.CultureInfo.InvariantCulture);
            if (created < intent.StartedAt || created > clock.GetUtcNow()) throw new JsonException();
            await CompleteAsync(store, record, intent with
            {
                State = FoundationOperationState.Completed, Disposition = FoundationResourceDisposition.Created,
                ResourceId = intent.Target, Incarnation = observation.Incarnation, NativeRevision = observation.Revision, HttpStatus = 201,
                ProviderRequestId = tracking.ToString(), ProviderCorrelationId = correlation.ToString(), ProviderOperationId = operation,
            }, token);
        }
        current = current with { HostEndpoint = await CheckHostAsync(admission, arm, token) };
    }

    private async Task<(FoundationOperation Operation, ConfigurationSetting Record)> IntentAsync(OwnerReservationStore store,
        string target, string version, string body, ModelAccessGrant? grant, CancellationToken token, string? deployment = null)
    {
        var intent = new FoundationOperation(1, FactoryModelsValidation.OperationKind, current!.Configuration.FactoryId,
            current.OperationId, effects.Count, target, version, FactoryReservationRecords.Hash(body), Guid.NewGuid(), clock.GetUtcNow(),
            FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested)
        { DeploymentTarget = deployment, RoleScope = grant?.Scope, RoleDefinitionId = grant?.RoleDefinitionId,
            PrincipalId = grant?.PrincipalId, LogicalIdentity = grant is null ? null : "factory:council" };
        current = current with { OperationCount = effects.Count + 1 };
        await SaveAsync(store, token);
        var record = await store.WriteAsync(ProductConfigurationKeys.ModelOperation(current.OperationId, intent.Index),
            FactoryReservationRecords.Serialize(intent), null, token);
        effects.Add(intent);
        return (intent, record);
    }

    private async Task CompleteAsync(OwnerReservationStore store, ConfigurationSetting record, FoundationOperation effect, CancellationToken token)
    {
        await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(effect), record.ETag, token);
        effects[effect.Index] = effect;
    }
    private async Task SaveAsync(OwnerReservationStore store, CancellationToken token)
    {
        if (!FactoryTelemetryLimits.FitsDetail(current!.Detail)) throw new JsonException("Bounded model diagnostic required.");
        root = await store.WriteAsync(ProductConfigurationKeys.FactoryModels, Serialize(current), root!.ETag, token);
    }
    private async Task<FactoryReservationResult> CloseAsync(FactoryReservationResult admission, OwnerReservationStore store,
        ModelCapabilityState state, string detail, CancellationToken token)
    {
        if (root is not null && current is not null && !token.IsCancellationRequested)
        {
            current = current with { State = state, Detail = detail, VerifiedUntil = null };
            try { await SaveAsync(store, token); }
            catch (ReservationStoreException)
            { return Result(admission, ModelCapabilityState.Unknown, "Final model outcome unconfirmed; no current hold or revocation asserted. Inspect current records."); }
        }
        return Result(admission, state, detail);
    }
    private FactoryReservationResult Result(FactoryReservationResult admission, ModelCapabilityState state, string detail) => admission with
    {
        State = state == ModelCapabilityState.Verified ? FactoryReservationState.Reserved : Enum.Parse<FactoryReservationState>(state.ToString()),
        Detail = detail, HasRegistryEffects = admission.HasRegistryEffects || dispatched,
        Models = current is null ? null : current with { State = state, Detail = detail, Effects = effects.ToArray(),
            VerifiedUntil = state == ModelCapabilityState.Verified ? current.VerifiedUntil : null },
        Foundation = Pending(admission.Foundation, state),
    };
    private static FactoryFoundationResult? Pending(FactoryFoundationResult? foundation, ModelCapabilityState state) => foundation is null ? null
        : foundation with { PendingStages = state == ModelCapabilityState.Verified
            ? foundation.PendingStages.Where(s => s != "models").ToArray()
            : foundation.PendingStages.Contains("models") ? foundation.PendingStages : [.. foundation.PendingStages, "models"] };
    private static string Serialize(FactoryModels value) => FactoryReservationRecords.Serialize(FactoryModelsValidation.Persisted(value));
    private OwnerReservationStore Store(FactoryReservationResult admission) => new(admission.Foundation!.ProductEndpoint!,
        admission.Identity!.TenantId, admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);
    private static bool ProviderFailure(Exception e) => e is JsonException or InvalidOperationException or ArgumentException
        or FormatException or KeyNotFoundException or HttpRequestException or OwnerReservationStore.AuthenticationFailedException;
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string? Optional(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && Guid.TryParse(values.Single(), out var id) && id != Guid.Empty ? id.ToString() : throw new JsonException();
    private static JsonElement[] CompleteValues(JsonElement response) =>
        Optional(response, "nextLink") is not (null or "") ? throw new JsonException("Incomplete provider inventory.") : response.GetProperty("value").EnumerateArray().ToArray();
    private static void RequireCreated(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new ReservationStoreException(FactoryReservationState.Failed, "Exact model preparation create denied; inspect held intent, no privilege expansion or retry.");
        if (response.StatusCode != HttpStatusCode.Created) throw new JsonException("Only affirmative creation receipts establish ownership.");
    }
    private static async Task<JsonDocument> ReadNativeAsync(FactoryPreparationArm arm, string target, string version, CancellationToken token)
    {
        using var response = await arm.SendAsync(HttpMethod.Get, target, version, token);
        RequireRead(response, target);
        return await DocumentAsync(response, token);
    }
    private static async Task AbsentAsync(FactoryPreparationArm arm, string target, string version, CancellationToken token)
    {
        using var response = await arm.SendAsync(HttpMethod.Get, target, version, token);
        if (response.StatusCode != HttpStatusCode.NotFound)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Creation target not proven absent; no adoption or replacement: " + target);
    }
}
