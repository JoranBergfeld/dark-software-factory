using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryTelemetryService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    private static readonly HttpClient ProviderHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private const string OperationKind = "decide-telemetry-operation";
    internal const string NoConfirmedHold = "No readiness hold confirmed; hold not attempted; no revocation asserted. Previous Ready may remain; inspect current authority.";
    private enum HoldProgress { NotAttempted, Unconfirmed, Confirmed }
    private FactoryTelemetry? authority;
    private ConfigurationSetting? root;
    private bool registryDispatchStarted;
    private readonly List<FoundationOperation> effects = [];

    internal async Task<FactoryReservationResult> ApplyAsync(
        FactoryReservationResult admission, FactoryTelemetry preview, string approval, CancellationToken token)
    {
        if (admission.Telemetry is not null) return admission;
        if (preview.ApprovalFingerprint != approval)
            return admission with { State = FactoryReservationState.Blocked, Detail = "Telemetry approval differs; no effects." };
        var store = Store(admission);
        try
        {
            CheckCapacity(preview);
            await CheckBindingsAsync(admission, preview, token);
            await CheckIsolationAsync(admission, preview, token);
            foreach (var grant in preview.Configuration.AccessGrants) await CheckDelegationAsync(admission, grant, token);
            var (manifestRecord, manifest) = await ReadGovernanceAsync(admission, store, token);
            if (manifest.TelemetryOperationId is not null)
                throw new ReservationStoreException(FactoryReservationState.Unknown, "Telemetry intent already held; no new dispatch or replacement.");
            var requested = preview with { OperationId = Guid.NewGuid(), Stage = "preparation" };
            var manifestValue = FactoryReservationRecords.Serialize(manifest with
            {
                TelemetryOperationId = requested.OperationId, TelemetryApprovalFingerprint = approval,
            });
            OwnerReservationStore.CheckSize(manifestRecord.Key, ProductConfigurationKeys.NoLabel, manifestValue);
            registryDispatchStarted = true;
            await store.WriteAsync(manifestRecord.Key, manifestValue, manifestRecord.ETag, token);
            authority = requested;
            await EnsureCurrentAuthorityAsync(admission, store, token);
            root = await store.WriteAsync(ProductConfigurationKeys.FactoryTelemetry, FactoryReservationRecords.Serialize(authority), null, token);
            foreach (var grant in authority.Configuration.AccessGrants)
                await GrantAsync(admission, store, grant, token);
            await DeployAsync(admission, store, token);
            authority = authority with { Stage = "prepared", EffectHashes = effects.ToDictionary(
                e => e.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), FactoryTelemetryValidation.Hash) };
            await SaveAsync(store, token);
            return await CheckAsync(admission with { Telemetry = authority }, token);
        }
        catch (ReservationStoreException e) { return await CloseAsync(admission, store, Map(e.State), e.Message, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(admission, TelemetryState.Canceled, "Canceled; issued telemetry effects may continue. Inspect held receipts, no rollback or reissue."); }
        catch (Exception e) when (ProviderFailure(e))
        { return await CloseAsync(admission, store, TelemetryState.Unknown, "Telemetry provider/receipt evidence unavailable; held without retry. Factory administrator must inspect exact operation records; details withheld.", token); }
    }

    internal async Task<FactoryReservationResult> CheckAsync(FactoryReservationResult admission, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Result(admission, TelemetryState.Canceled, NoConfirmedHold);
        if (admission.Telemetry is not { Stage: "prepared" or "checking" or "observed" } existing)
            return admission with { State = FactoryReservationState.Blocked,
                Detail = "Check requires completed provenance-proven source grants and host. Unknown preparation stays held; no create retry." };
        var store = Store(admission);
        var hold = HoldProgress.NotAttempted;
        var promotionStarted = false;
        try
        {
            root = await store.ReadAsync(ProductConfigurationKeys.FactoryTelemetry, token) ?? throw new JsonException();
            var stored = FactoryReservationRecords.Parse<FactoryTelemetry>(root.Value) with { Proof = existing.Proof };
            if (Persisted(Expire(stored, clock.GetUtcNow())) != Persisted(existing)) throw new JsonException();
            existing = stored;
            authority = existing;
            var values = await ReadAsync(store, existing, token);
            var manifest = ReadGovernance(admission, values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile));
            _ = FactoryTelemetryValidation.ReadPreparation(manifest, values, requireChecking: false);
            effects.Clear();
            effects.AddRange(ReadEffects(existing, values, completed: true));
            authority = existing with { State = TelemetryState.Partial, Stage = "checking", CheckId = Guid.NewGuid(),
                Detail = "Current readiness held pending source verification; previous proof is historical." };
            hold = HoldProgress.Unconfirmed;
            try
            {
                await SaveAsync(store, token);
                hold = HoldProgress.Confirmed;
            }
            catch (ReservationStoreException exception)
            { return Result(admission, TelemetryState.Unknown, "Hold not confirmed; no proof request or revocation asserted. Factory App Configuration administrator must reconcile conditional authority. " + exception.Message); }
            await CheckBindingsAsync(admission, authority, token);
            await CheckIsolationAsync(admission, authority, token);
            await CheckEffectsAsync(admission, token);
            var endpoint = await CheckHostAsync(admission, token);
            if (endpoint != authority.HostEndpoint) throw new JsonException();
            var credential = new OwnerReservationStore.OperatorCredential(azure, admission.Identity!.TenantId,
                admission.Identity.SubscriptionId, admission.Identity.AzureActorId, "api://" + authority.Configuration.AudienceClientId);
            var access = await credential.GetTokenAsync(new TokenRequestContext(["api://" + authority.Configuration.AudienceClientId + "/.default"]), token);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/proof");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            await EnsureCurrentAuthorityAsync(admission, store, token);
            using var response = await (http ?? ProviderHttp).SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "POST " + endpoint + "/proof denied: Entra/factory configuration administrator must establish current native operator authorization and pinned telemetry authority.");
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict)) throw new JsonException();
            var proof = JsonSerializer.Deserialize<TelemetryProof>(await response.Content.ReadAsStringAsync(token), FactoryGitHubJson.Options)
                ?? throw new JsonException();
            ValidateProof(proof);
            if (response.StatusCode != HttpStatusCode.OK && proof.State == TelemetryState.Ready) throw new JsonException();
            // Recheck exact current grants and metadata after the workload observation, not merely after creation.
            await CheckBindingsAsync(admission, authority, token);
            await CheckIsolationAsync(admission, authority, token);
            await CheckEffectsAsync(admission, token);
            _ = await CheckHostAsync(admission, token);
            var current = await ReadAsync(store, authority, token);
            _ = ReadGovernance(admission, current.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                current.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile));
            if (current[ProductConfigurationKeys.FactoryManifest] != values[ProductConfigurationKeys.FactoryManifest]
                || current[ProductConfigurationKeys.FactoryProfile] != values[ProductConfigurationKeys.FactoryProfile]
                || current[ProductConfigurationKeys.FactoryTelemetry] != Persisted(authority)) throw new JsonException();
            _ = ReadEffects(authority, current, completed: true);
            var proofKey = ProductConfigurationKeys.TelemetryProof(authority.OperationId, authority.CheckId!.Value);
            await store.WriteAsync(proofKey, FactoryReservationRecords.Serialize(proof), null, token);
            await EnsureCurrentAuthorityAsync(admission, store, token);
            var held = authority;
            authority = authority with { State = proof.State, Stage = "observed", LastFailure = proof.Reason,
                Proof = proof.State == TelemetryState.Ready ? proof : authority.Proof, Detail = proof.Detail,
                LastCheckReference = proofKey, LastCheckSha256 = FactoryTelemetryValidation.Hash(proof),
                VerifiedProofReference = proof.State == TelemetryState.Ready ? proofKey : authority.VerifiedProofReference,
                VerifiedProofSha256 = proof.State == TelemetryState.Ready ? FactoryTelemetryValidation.Hash(proof) : authority.VerifiedProofSha256 };
            authority = Expire(authority, clock.GetUtcNow());
            promotionStarted = authority.State == TelemetryState.Ready;
            try
            {
                await SaveAsync(store, token);
                await EnsureCurrentAuthorityAsync(admission, store, token);
            }
            catch (Exception e) when (e is ReservationStoreException || ProviderFailure(e))
            {
                authority = held;
                throw;
            }
            return Result(admission, authority.State, authority.Detail);
        }
        catch (ReservationStoreException e) { return await CloseAsync(admission, store, Map(e.State), e.Message, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(admission, TelemetryState.Canceled, promotionStarted
            ? "Check canceled during readiness promotion; final authority/hold is unconfirmed. Inspect current records; no revocation or atomic global stop claimed."
            : hold switch
            {
                HoldProgress.Confirmed => "Check canceled after a readiness hold was confirmed; current authority may have changed. No current hold or revocation asserted; inspect current authority. No effects retried.",
                HoldProgress.Unconfirmed => "No readiness hold confirmed; conditional hold outcome is unknown; no revocation asserted. Inspect current authority.",
                _ => NoConfirmedHold,
            }); }
        catch (Exception e) when (ProviderFailure(e))
        { return await CloseAsync(admission, store, TelemetryState.Unknown,
            "NOT READY: deployed proof/current authority unavailable or malformed. Network/Entra/evidence administrator must inspect the exact source endpoint and held records; no operator query fallback.", token); }
    }

    private async Task GrantAsync(FactoryReservationResult admission, OwnerReservationStore store, TelemetryAccessGrant grant, CancellationToken token)
    {
        await CheckBindingsAsync(admission, authority!, token);
        await CheckIsolationAsync(admission, authority!, token);
        await CheckDelegationAsync(admission, grant, token);
        using var absent = await ArmAsync(admission, HttpMethod.Get, grant.Target, "2022-04-01", token);
        if (absent.StatusCode != HttpStatusCode.NotFound)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Exact grant already exists or cannot be inspected: " + grant.Target + ". Not adopted, replaced or newly owned.");
        var body = JsonSerializer.Serialize(new { properties = new { principalId = grant.PrincipalId,
            principalType = "ServicePrincipal", roleDefinitionId = grant.RoleDefinitionId } });
        await EnsureCurrentAuthorityAsync(admission, store, token);
        var (operation, record) = await IntentAsync(store, grant.Target, "2022-04-01", body, token,
            grant: grant);
        await EnsureDispatchAuthorityAsync(admission, store, [(operation, record)], token);
        using var response = await ArmAsync(admission, HttpMethod.Put, grant.Target, operation.ApiVersion, token, body, operation.ClientRequestId);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            await CompleteAsync(store, record, operation with { State = FoundationOperationState.Failed,
                Disposition = FoundationResourceDisposition.Unproven, HttpStatus = (int)response.StatusCode }, token);
        RequireCreated(response);
        using var json = await DocumentAsync(response, token);
        var properties = json.RootElement.GetProperty("properties");
        var created = properties.GetProperty("createdOn").GetDateTimeOffset();
        if (!GrantMatches(json.RootElement, grant, admission.Identity!.AzureActorId)
            || created < operation.StartedAt || created > clock.GetUtcNow()) throw new JsonException();
        operation = operation with { State = FoundationOperationState.Completed, Disposition = FoundationResourceDisposition.Created,
            ResourceId = grant.Target, Incarnation = created.ToString("O"), HttpStatus = 201,
            ProviderRequestId = HeaderId(response, "x-ms-request-id"), ProviderCorrelationId = HeaderId(response, "x-ms-correlation-request-id"),
            ResponseDate = response.Headers.Date, ConditionVersion = Optional(properties, "conditionVersion") };
        await CompleteAsync(store, record, operation, token);
    }

    private async Task DeployAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var config = authority!.Configuration;
        var assets = new FactoryFoundationAssets(admission.Identity!, admission.Plan!.Selection);
        var host = FactoryTelemetryValidation.HostTarget(config);
        foreach (var target in new[] { host, host + "/authconfigs/current" })
        {
            using var absent = await ArmAsync(admission, HttpMethod.Get, target, "2025-01-01", token);
            if (absent.StatusCode != HttpStatusCode.NotFound)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Host target not proven absent: " + target + "; no adoption or overwrite.");
        }
        var templateBytes = File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-telemetry.json"));
        if (FactoryReservationRecords.Hash(templateBytes) != config.HostTemplateSha256) throw new JsonException();
        using var template = JsonDocument.Parse(templateBytes);
        var body = JsonSerializer.Serialize(new { properties = new
        {
            mode = "Incremental", template = template.RootElement, parameters = new
            {
                namePrefix = new { value = assets.Prefix }, location = new { value = admission.Plan.Selection.FactoryLocation },
                runtimeImage = new { value = config.RuntimeImage }, productKey = new { value = admission.Identity!.ProductKey },
                productEndpoint = new { value = config.ProductEndpoint }, profilePin = new { value = admission.Foundation!.ProfilePin },
                tenantId = new { value = admission.Identity.TenantId }, audienceClientId = new { value = config.AudienceClientId },
                operatorPrincipalId = new { value = admission.Identity.AzureActorId },
            },
        } });
        var deployment = assets.Group + "/providers/microsoft.resources/deployments/tm-" + authority.OperationId.ToString("N");
        using var absence = await ArmAsync(admission, HttpMethod.Get, deployment, "2025-04-01", token);
        if (absence.StatusCode != HttpStatusCode.NotFound) throw new JsonException();
        await EnsureCurrentAuthorityAsync(admission, store, token);
        var hostIntent = await IntentAsync(store, host, "2025-01-01", body, token, deployment: deployment);
        var policyIntent = await IntentAsync(store, host + "/authconfigs/current", "2025-01-01", body, token, deployment: deployment);
        await EnsureDispatchAuthorityAsync(admission, store, [hostIntent, policyIntent], token);
        using var response = await ArmAsync(admission, HttpMethod.Put, deployment, "2025-04-01", token, body, hostIntent.Operation.ClientRequestId);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            foreach (var (intent, record) in new[] { hostIntent, policyIntent })
                await CompleteAsync(store, record, intent with { State = FoundationOperationState.Failed,
                    Disposition = FoundationResourceDisposition.Unproven, HttpStatus = (int)response.StatusCode }, token);
        RequireCreated(response);
        using var deployed = await DocumentAsync(response, token);
        if (!Same(deployed.RootElement.GetProperty("id").GetString(), deployment)) throw new JsonException();
        var properties = deployed.RootElement.GetProperty("properties");
        var correlation = Guid.Parse(properties.GetProperty("correlationId").GetString()!);
        if (correlation == Guid.Empty) throw new JsonException();
        var state = properties.GetProperty("provisioningState").GetString();
        for (var poll = 0; state is "Accepted" or "Running" or "Creating" && poll < 120; poll++)
        {
            if (poll > 0) await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
            using var observation = await GetAsync(admission, deployment, "2025-04-01", token);
            var current = observation.RootElement.GetProperty("properties");
            if (!Same(observation.RootElement.GetProperty("id").GetString(), deployment)
                || current.GetProperty("correlationId").GetString() != correlation.ToString()) throw new JsonException();
            state = current.GetProperty("provisioningState").GetString();
        }
        if (state is "Failed" or "Canceled")
            throw new ReservationStoreException(FactoryReservationState.Failed, "Telemetry host deployment failed/canceled; issued effects retained, no rollback or retry.");
        if (state != "Succeeded") throw new JsonException();
        using var receipt = await GetAsync(admission, deployment + "/operations", "2025-04-01", token);
        var receipts = CompleteValues(receipt);
        if (receipts.Any(r => r.GetProperty("properties").TryGetProperty("targetResource", out var target)
            && !new[] { host, host + "/authconfigs/current" }.Contains(target.GetProperty("id").GetString(), StringComparer.OrdinalIgnoreCase)))
            throw new JsonException();
        foreach (var (intent, record) in new[] { hostIntent, policyIntent })
        {
            var operationReceipt = receipts.Single(r => Same(r.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString(), intent.Target));
            var rp = operationReceipt.GetProperty("properties");
            var providerOperationId = operationReceipt.GetProperty("operationId").GetString();
            if (!FactoryTelemetryValidation.IsProviderOperationId(providerOperationId)
                || rp.GetProperty("provisioningOperation").GetString() != "Create" || rp.GetProperty("provisioningState").GetString() != "Succeeded"
                || rp.GetProperty("statusCode").GetString() is not ("OK" or "200" or "Created" or "201")) throw new JsonException();
            using var actual = await GetAsync(admission, intent.Target, intent.ApiVersion, token);
            var created = actual.RootElement.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset();
            if (!Same(actual.RootElement.GetProperty("id").GetString(), intent.Target) || created < intent.StartedAt || created > clock.GetUtcNow()
                || actual.RootElement.GetProperty("systemData").GetProperty("lastModifiedAt").GetDateTimeOffset() != created) throw new JsonException();
            await CompleteAsync(store, record, intent with { State = FoundationOperationState.Completed,
                Disposition = FoundationResourceDisposition.Created, ResourceId = intent.Target, Incarnation = created.ToString("O"),
                ProviderCorrelationId = correlation.ToString(), ProviderOperationId = providerOperationId,
                ProviderRequestId = Guid.Parse(rp.GetProperty("trackingId").GetString()!).ToString(), HttpStatus = 201 }, token);
        }
        authority = authority with { HostEndpoint = await CheckHostAsync(admission, token) };
    }

    private async Task EnsureDispatchAuthorityAsync(FactoryReservationResult admission, OwnerReservationStore store,
        IReadOnlyList<(FoundationOperation Operation, ConfigurationSetting Record)> intents, CancellationToken token)
    {
        try { await EnsureCurrentAuthorityAsync(admission, store, token); }
        catch (Exception e) when (e is ReservationStoreException or JsonException)
        {
            // These intents have not reached ARM. Earlier completed/unknown effects remain untouched.
            foreach (var (operation, record) in intents)
                await CompleteAsync(store, record, operation with
                { State = FoundationOperationState.Failed, Disposition = FoundationResourceDisposition.Unproven }, token);
            throw;
        }
    }

    private async Task<(FoundationOperation Operation, ConfigurationSetting Record)> IntentAsync(OwnerReservationStore store,
        string target, string version, string body, CancellationToken token, TelemetryAccessGrant? grant = null, string? deployment = null)
    {
        var operation = new FoundationOperation(1, OperationKind, authority!.Configuration.FactoryId, authority.OperationId,
            authority.OperationCount, target, version, FactoryReservationRecords.Hash(body), Guid.NewGuid(), clock.GetUtcNow(),
            FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested)
        { DeploymentTarget = deployment, RoleScope = grant?.Scope, RoleDefinitionId = grant?.RoleDefinitionId,
            PrincipalId = grant?.PrincipalId, LogicalIdentity = grant is null ? null : "factory:source-azuremonitor" };
        authority = authority with { OperationCount = authority.OperationCount + 1 };
        await SaveAsync(store, token);
        var record = await store.WriteAsync(ProductConfigurationKeys.TelemetryOperation(authority.OperationId, operation.Index),
            FactoryReservationRecords.Serialize(operation), null, token);
        effects.Add(operation);
        return (operation, record);
    }

    private async Task CompleteAsync(OwnerReservationStore store, ConfigurationSetting record, FoundationOperation operation, CancellationToken token)
    {
        await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
        effects[operation.Index] = operation;
    }

    private async Task SaveAsync(OwnerReservationStore store, CancellationToken token)
    {
        if (!FactoryTelemetryLimits.FitsDetail(authority!.Detail))
            throw new ReservationStoreException(FactoryReservationState.Unknown, "Telemetry diagnostic exceeds its lifecycle bound; rejected, not truncated.");
        registryDispatchStarted = true;
        root = await store.WriteAsync(ProductConfigurationKeys.FactoryTelemetry, Persisted(authority!), root!.ETag, token);
    }

    private static string Persisted(FactoryTelemetry value) => FactoryReservationRecords.Serialize(value with { Proof = null });

    private async Task<FactoryReservationResult> CloseAsync(FactoryReservationResult admission, OwnerReservationStore store,
        TelemetryState state, string detail, CancellationToken token)
    {
        if (!FactoryTelemetryLimits.FitsDetail(detail))
        {
            state = TelemetryState.Unknown;
            detail = "NOT READY: diagnostic exceeds its lifecycle bound and was rejected, not truncated. Inspect the held operation records; no new dispatch authorized.";
        }
        if (root is not null && authority is not null && !token.IsCancellationRequested)
        {
            authority = authority with { State = state, Detail = detail };
            try { await SaveAsync(store, token); }
            catch (ReservationStoreException)
            { return Result(admission, TelemetryState.Unknown, "Final telemetry outcome could not commit; readiness hold remains unconfirmed/current state must be inspected. No revocation or atomic global stop claimed."); }
        }
        return Result(admission, state, detail);
    }

    private FactoryReservationResult Result(FactoryReservationResult admission, TelemetryState state, string detail) =>
        admission with { State = state == TelemetryState.Ready ? FactoryReservationState.Reserved : Enum.Parse<FactoryReservationState>(state.ToString()),
            Detail = detail, Telemetry = (authority ?? admission.Telemetry) is { } current ? current with { State = state, Detail = detail } : null,
            HasRegistryEffects = admission.HasRegistryEffects || registryDispatchStarted,
            Foundation = Pending(admission.Foundation, state) };

    private static FactoryFoundationResult? Pending(FactoryFoundationResult? foundation, TelemetryState state) => foundation is null ? null
        : foundation with { PendingStages = state == TelemetryState.Ready
            ? foundation.PendingStages.Where(s => s != "evidence access and connectivity").ToArray()
            : foundation.PendingStages.Contains("evidence access and connectivity") ? foundation.PendingStages
            : [.. foundation.PendingStages, "evidence access and connectivity"] };

    private static FactoryTelemetry Expire(FactoryTelemetry value, DateTimeOffset now) =>
        value.State == TelemetryState.Ready && !FactoryTelemetryValidation.HasFreshEvidence(value, now)
            ? value with { State = TelemetryState.Blocked, LastFailure = TelemetryReason.Stale,
                Detail = "NOT READY: historical application records no longer satisfy the reviewed positive freshness window. Recheck via --verify-only; no unhealthy diagnosis." }
            : value;
    private static TelemetryState Map(FactoryReservationState state) => Enum.Parse<TelemetryState>(state.ToString());
    private OwnerReservationStore Store(FactoryReservationResult admission) => new(admission.Foundation!.ProductEndpoint!,
        admission.Identity!.TenantId, admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);
    private static bool ProviderFailure(Exception e) => e is JsonException or InvalidOperationException or FormatException
        or KeyNotFoundException or HttpRequestException or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException
        or ArgumentException or BoundaryInspectionException;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string? Optional(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;
    private static string HeaderId(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && Guid.TryParse(values.Single(), out var id) && id != Guid.Empty
            ? id.ToString() : throw new JsonException();
    private static void RequireCreated(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ReservationStoreException(FactoryReservationState.Failed, "Exact telemetry create denied; Azure grantor/deployment administrator must inspect held effects; no retry.");
        if (response.StatusCode != HttpStatusCode.Created) throw new JsonException();
    }
    private static async Task<JsonDocument> DocumentAsync(HttpResponseMessage response, CancellationToken token) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(token), new JsonDocumentOptions { AllowDuplicateProperties = false });
    private async Task<JsonDocument> GetAsync(FactoryReservationResult admission, string target, string version, CancellationToken token)
    {
        using var response = await ArmAsync(admission, HttpMethod.Get, target, version, token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ReservationStoreException(response.StatusCode == HttpStatusCode.NotFound ? FactoryReservationState.Blocked : FactoryReservationState.Unknown,
                "GET " + target + ": current evidence unavailable; Azure resource/RBAC administrator must reconcile. No adoption or repair.");
        return await DocumentAsync(response, token);
    }
    private async Task<HttpResponseMessage> ArmAsync(FactoryReservationResult admission, HttpMethod method, string target,
        string version, CancellationToken token, string? body = null, Guid? correlation = null)
    {
        var identity = admission.Identity!;
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "https://management.azure.com/");
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
        using var request = new HttpRequestMessage(method, "https://management.azure.com" + target
            + (target.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + version);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        if (correlation is not null) request.Headers.Add("x-ms-client-request-id", correlation.Value.ToString());
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await (http ?? ProviderHttp).SendAsync(request, token);
    }
    private static JsonElement[] CompleteValues(JsonDocument document)
    {
        if (Optional(document.RootElement, "nextLink") is { Length: > 0 }) throw new JsonException("Incomplete provider collection.");
        return document.RootElement.GetProperty("value").EnumerateArray().ToArray();
    }
}
