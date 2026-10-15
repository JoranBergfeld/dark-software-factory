using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactorySupplementalService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    private static readonly HttpClient HostHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private FactoryManifest manifest = null!;
    private string manifestValue = "";
    private ConfigurationSetting? root;
    private FactorySupplemental current = null!;
    private readonly List<FoundationOperation> effects = [];

    internal async Task RequireUnselectedAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var store = Store(admission);
        await ReadGovernanceAsync(admission, store, token);
        await RequireUnselectedAsync(manifest, store, token);
    }
    private static Task RequireUnselectedAsync(FactoryManifest manifest, OwnerReservationStore store, CancellationToken token)
    {
        if (manifest.SupplementalOperationId is not null || manifest.SupplementalApprovalFingerprint is not null) throw new JsonException();
        return store.RequirePristineAsync(ProductConfigurationKeys.FactorySupplemental, token);
    }
    internal async Task<FactoryReservationResult> PrepareAsync(FactoryReservationResult admission, FactorySupplemental preview,
        string approval, CancellationToken token)
    {
        var store = Store(admission);
        try
        {
            if (preview.ApprovalFingerprint != approval)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Exact supplemental approval differs; no effects.");
            var record = await ReadGovernanceAsync(admission, store, token);
            if (manifest.SupplementalOperationId is not null || await store.ReadAsync(ProductConfigurationKeys.FactorySupplemental, token) is not null)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Supplemental selection already held; no implicit removal, replacement or retry.");
            await RequireUnselectedAsync(manifest, store, token);
            current = preview with { OperationId = Guid.NewGuid(), Stage = "preparation" };
            CheckCapacity(current, manifest);
            await CheckNativeAsync(admission, token);
            foreach (var target in FactorySupplementalValidation.Targets(current.Configuration!))
                await AbsentAsync(admission, target.Target, target.Api, token);
            manifest = manifest with { SupplementalOperationId = current.OperationId, SupplementalApprovalFingerprint = approval };
            manifestValue = FactoryReservationRecords.Serialize(manifest);
            await store.WriteAsync(record.Key, manifestValue, record.ETag, token);
            await CurrentAsync(admission, store, token);
            root = await store.WriteAsync(ProductConfigurationKeys.FactorySupplemental, FactoryReservationRecords.Serialize(current), null, token);
            foreach (var source in current.Configuration!.Selection.Sources)
            {
                var config = current.Configuration;
                var identity = await CreateAsync(admission, store, FactorySupplementalValidation.IdentityTarget(config, source.Kind),
                    "2024-11-30", JsonSerializer.Serialize(new { location = config.Location }), source.Kind, null, token);
                foreach (var grant in FactorySupplementalValidation.Grants(config, source))
                    await CreateAsync(admission, store, grant.Target, "2022-04-01",
                        JsonSerializer.Serialize(new { properties = new { principalId = identity.PrincipalId,
                            principalType = "ServicePrincipal", roleDefinitionId = grant.RoleDefinitionId } }), source.Kind, grant, token);
                await DeployAsync(admission, store, source, identity, token);
            }
            await CheckNativeAsync(admission, token);
            current = current with { Stage = "prepared", EffectHashes = effects.Select(FactoryTelemetryValidation.Hash).ToArray(),
                Detail = "Selected source workloads prepared; readiness requires every actual source check. " + FactorySupplementalValidation.Coverage };
            await SaveAsync(admission, store, token);
            return await CheckAsync(admission with { Supplemental = current }, token);
        }
        catch (OperationCanceledException)
        { return Result(admission, SupplementalState.Canceled, "Canceled; issued source effects remain outstanding. No rollback, retry, hold or revocation asserted."); }
        catch (ReservationStoreException e) { return await CloseAsync(admission, store, Enum.Parse<SupplementalState>(e.State.ToString()), e.Message, token); }
        catch (Exception e) when (ProviderFailure(e))
        { return await CloseAsync(admission, store, SupplementalState.Unknown, "Source preparation outcome held; administrator must inspect native receipts. No recreation, provider detail disclosure or rollback.", token); }
    }

    internal async Task<FactoryReservationResult> CheckAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var store = Store(admission);
        var hold = false;
        try
        {
            await ReadGovernanceAsync(admission, store, token);
            root = await store.ReadAsync(ProductConfigurationKeys.FactorySupplemental, token) ?? throw new JsonException();
            current = FactorySupplementalValidation.Read(manifest, root.Value);
            if (current.Stage is not ("prepared" or "checking" or "observed"))
                return Result(admission, current.State == SupplementalState.Unknown ? SupplementalState.Unknown : SupplementalState.Blocked,
                    "Preparation incomplete or uncertain; source effects remain held, never recreated.");
            effects.Clear();
            effects.AddRange(await ReadEffectsAsync(current, store, token));
            current = current with { State = SupplementalState.Partial, Stage = "checking", CheckId = Guid.NewGuid(), Checks = [],
                Detail = "Checking every selected source. Prior observations are historical; no current readiness asserted." };
            await SaveAsync(admission, store, token);
            hold = true;
            await CheckNativeAsync(admission, token);
            var checks = new List<SupplementalCheck>();
            foreach (var source in current.Configuration!.Selection.Sources)
            {
                await CurrentAsync(admission, store, token);
                var identity = effects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(current.Configuration, source.Kind));
                var endpoint = await HostAsync(admission, source, identity, token);
                var credential = new OwnerReservationStore.OperatorCredential(azure, manifest.Identity.TenantId,
                    manifest.Identity.SubscriptionId, manifest.Identity.AzureActorId, "api://" + current.Configuration.Selection.AudienceClientId);
                var access = await credential.GetTokenAsync(new TokenRequestContext(["api://" + current.Configuration.Selection.AudienceClientId + "/.default"]), token);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/proof")
                { Content = JsonContent.Create(new SupplementalProofRequest(current.CheckId!.Value, current.ApprovalFingerprint!), options: FactoryGitHubJson.Options) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                await CurrentAsync(admission, store, token);
                SupplementalProof proof;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75), clock);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                try
                {
                    using var response = await (http ?? HostHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
                    if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
                        throw new SupplementalSourceException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            ? SupplementalReason.PermissionDenied : response.StatusCode == HttpStatusCode.PartialContent
                                ? SupplementalReason.Incomplete : SupplementalReason.NetworkUnavailable);
                    using var document = await DocumentAsync(response, pending.Token, 8192);
                    proof = document.RootElement.Deserialize<SupplementalProof>(FactoryGitHubJson.Options) ?? throw new JsonException();
                    if (response.StatusCode != HttpStatusCode.OK && proof.State == SupplementalState.ReadySupplemental) throw new JsonException();
                    FactorySupplementalValidation.ValidateProof(current, identity, proof, clock.GetUtcNow());
                }
                catch (Exception e) when (ProviderFailure(e) || e is OperationCanceledException && !token.IsCancellationRequested)
                {
                    var reason = e switch
                    {
                        SupplementalSourceException sourceError => sourceError.Reason,
                        HttpRequestException or IOException or OperationCanceledException => SupplementalReason.NetworkUnavailable,
                        _ => SupplementalReason.MalformedResponse,
                    };
                    proof = new(manifest.Identity.FactoryId, current.CheckId!.Value, current.ApprovalFingerprint!, source.Kind,
                        identity.PrincipalId!, reason == SupplementalReason.NetworkUnavailable ? SupplementalState.Unknown : SupplementalState.Blocked,
                        reason, clock.GetUtcNow(), [], null, source.SecretVersion);
                }
                await CheckNativeAsync(admission, token);
                await CurrentAsync(admission, store, token);
                var key = ProductConfigurationKeys.SupplementalProof(current.OperationId, current.CheckId!.Value, source.Kind);
                await store.WriteAsync(key, FactoryReservationRecords.Serialize(proof), null, token);
                await CurrentAsync(admission, store, token);
                checks.Add(new(source.Kind, proof.State, proof.Reason, key, FactoryTelemetryValidation.Hash(proof)));
            }
            foreach (var check in checks)
            {
                var record = await store.ReadAsync(check.ProofKey, token) ?? throw new JsonException();
                var proof = FactoryReservationRecords.Parse<SupplementalProof>(record.Value);
                if (FactoryTelemetryValidation.Hash(proof) != check.ProofSha256) throw new JsonException();
                FactorySupplementalValidation.ValidateProof(current,
                    effects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(current.Configuration, check.Kind)), proof, clock.GetUtcNow());
            }
            await CheckNativeAsync(admission, token);
            await DispatchCurrentAsync(admission, store, [], token);
            var ready = checks.Count == current.Configuration.Selection.Sources.Count && checks.All(c => c.State == SupplementalState.ReadySupplemental);
            current = current with { State = ready ? SupplementalState.ReadySupplemental : SupplementalState.Blocked, Stage = "observed", Checks = checks,
                Detail = (ready ? "Every selected supplemental source checked; empty coverage is explicit. " :
                    "Selected source failed: inspect each check; source/secret/network administrator must restore exact approved access. No silent disable. ") + FactorySupplementalValidation.Coverage };
            await SaveAsync(admission, store, token);
            await CurrentAsync(admission, store, token);
            return Result(admission, current.State, current.Detail);
        }
        catch (OperationCanceledException)
        { return Result(admission, SupplementalState.Canceled, "Canceled; current authority unconfirmed. " + (hold ? "A conditional check hold was observed, not a global revocation." : "No hold confirmed; previous readiness may remain.")); }
        catch (ReservationStoreException e)
        { return await CloseAsync(admission, store, Enum.Parse<SupplementalState>(e.State.ToString()), e.Message, token); }
        catch (Exception e) when (ProviderFailure(e))
        { return await CloseAsync(admission, store, SupplementalState.Blocked, "Current source identity, native grants, host or authority changed/unavailable. Administrator must inspect; no alternate identity or network repair.", token); }
    }

    private async Task<FoundationOperation> CreateAsync(FactoryReservationResult admission, OwnerReservationStore store,
        string target, string api, string body, string kind, SupplementalGrant? grant, CancellationToken token)
    {
        await CheckNativeAsync(admission, token);
        await AbsentAsync(admission, target, api, token);
        var (operation, record) = await IntentAsync(admission, store, target, api, body, kind, null, token);
        await CheckNativeAsync(admission, token);
        await AbsentAsync(admission, target, api, token);
        await DispatchCurrentAsync(admission, store, [record], token);
        using var response = await Arm(admission).SendAsync(HttpMethod.Put, target, api, token, body, operation.ClientRequestId);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(operation with { State = FoundationOperationState.Failed,
                HttpStatus = (int)response.StatusCode, Disposition = FoundationResourceDisposition.Unproven }), record.ETag, token);
            throw new ReservationStoreException(FactoryReservationState.Failed, "Exact source create denied; Azure grantor must inspect held intent. No automatic retry.");
        }
        if (response.StatusCode != HttpStatusCode.Created) throw new JsonException();
        using var native = await DocumentAsync(response, token);
        var receipt = Observe(operation, native.RootElement, grant);
        receipt = receipt with { ProviderRequestId = Header(response, "x-ms-request-id"), ProviderCorrelationId = Header(response, "x-ms-correlation-request-id") };
        await CurrentAsync(admission, store, token);
        await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(receipt), record.ETag, token);
        effects.Add(receipt);
        return receipt;
    }

    private async Task DeployAsync(FactoryReservationResult admission, OwnerReservationStore store, SupplementalSourceSelection source,
        FoundationOperation identity, CancellationToken token)
    {
        var config = current.Configuration!;
        var templateBytes = File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-supplemental.json"));
        if (FactoryReservationRecords.Hash(templateBytes) != config.TemplateSha256) throw new JsonException();
        using var template = JsonDocument.Parse(templateBytes);
        var body = JsonSerializer.Serialize(new { properties = new { mode = "Incremental", template = template.RootElement, parameters = new
        {
            namePrefix = new { value = FactorySupplementalValidation.Prefix(config.FactoryId) }, sourceKind = new { value = source.Kind },
            location = new { value = config.Location }, runtimeImage = new { value = config.Selection.RuntimeImage },
            productKey = new { value = manifest.Identity.ProductKey }, productEndpoint = new { value = config.ProductEndpoint },
            profilePin = new { value = config.ProfilePin }, tenantId = new { value = manifest.Identity.TenantId },
            audienceClientId = new { value = config.Selection.AudienceClientId }, operatorPrincipalId = new { value = manifest.Identity.AzureActorId },
            sourceConfiguration = new { value = FactoryReservationRecords.Serialize(FactorySupplementalValidation.HostConfiguration(manifest, current, identity, source.Kind)) },
        } } });
        var host = FactorySupplementalValidation.HostTarget(config, source.Kind);
        var deployment = config.FactoryGroup + "/providers/microsoft.resources/deployments/sp-" + current.OperationId.ToString("N") + "-" + source.Kind;
        await AbsentAsync(admission, deployment, "2025-04-01", token);
        var intents = new List<(FoundationOperation Operation, ConfigurationSetting Record)>();
        foreach (var target in new[] { host, host + "/authconfigs/current" })
        {
            await AbsentAsync(admission, target, "2025-01-01", token);
            intents.Add(await IntentAsync(admission, store, target, "2025-01-01", body, source.Kind, deployment, token));
        }
        await CheckNativeAsync(admission, token);
        foreach (var (operation, _) in intents) await AbsentAsync(admission, operation.Target, operation.ApiVersion, token);
        await AbsentAsync(admission, deployment, "2025-04-01", token);
        await DispatchCurrentAsync(admission, store, intents.Select(i => i.Record), token);
        using var response = await Arm(admission).SendAsync(HttpMethod.Put, deployment, "2025-04-01", token, body, intents[0].Operation.ClientRequestId);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new ReservationStoreException(FactoryReservationState.Failed, "Source host deployment denied; issued grants retained. No rollback or retry.");
        if (response.StatusCode != HttpStatusCode.Created) throw new JsonException();
        using var created = await DocumentAsync(response, token);
        var native = created.RootElement.Clone();
        var correlation = Guid.Parse(native.GetProperty("properties").GetProperty("correlationId").GetString()!);
        if (correlation == Guid.Empty) throw new JsonException();
        for (var poll = 0; native.GetProperty("properties").GetProperty("provisioningState").GetString() is "Running" or "Accepted" or "Creating" && poll < 120; poll++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
            await CurrentAsync(admission, store, token);
            using var observed = await GetAsync(admission, deployment, "2025-04-01", token);
            native = observed.RootElement.Clone();
            if (native.GetProperty("properties").GetProperty("correlationId").GetString() != correlation.ToString()) throw new JsonException();
        }
        if (!Same(native.GetProperty("id").GetString(), deployment)) throw new JsonException();
        var state = native.GetProperty("properties").GetProperty("provisioningState").GetString();
        if (state is "Failed" or "Canceled")
            throw new ReservationStoreException(FactoryReservationState.Failed, "Source deployment failed/canceled; outstanding effects retained, never automatically recreated.");
        if (state != "Succeeded") throw new JsonException();
        using var receipt = await GetAsync(admission, deployment + "/operations", "2025-04-01", token);
        var operations = Complete(receipt.RootElement);
        if (operations.Length != 2) throw new JsonException();
        foreach (var (operation, record) in intents)
        {
            var rp = operations.Single(o => Same(o.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString(), operation.Target));
            var properties = rp.GetProperty("properties");
            var providerOperation = rp.GetProperty("operationId").GetString();
            if (!FactoryTelemetryValidation.IsProviderOperationId(providerOperation) || properties.GetProperty("provisioningOperation").GetString() != "Create"
                || properties.GetProperty("provisioningState").GetString() != "Succeeded" || properties.GetProperty("statusCode").GetString() != "Created") throw new JsonException();
            var request = Guid.Parse(properties.GetProperty("trackingId").GetString()!);
            if (request == Guid.Empty) throw new JsonException();
            using var resource = await GetAsync(admission, operation.Target, operation.ApiVersion, token);
            var effect = Observe(operation, resource.RootElement, null) with { ProviderOperationId = providerOperation,
                ProviderRequestId = request.ToString(), ProviderCorrelationId = correlation.ToString() };
            await CurrentAsync(admission, store, token);
            await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(effect), record.ETag, token);
            effects.Add(effect);
        }
    }

    private async Task<(FoundationOperation, ConfigurationSetting)> IntentAsync(FactoryReservationResult admission, OwnerReservationStore store,
        string target, string api, string body, string kind, string? deployment, CancellationToken token)
    {
        await CurrentAsync(admission, store, token);
        var operation = new FoundationOperation(1, FactorySupplementalValidation.OperationKind, manifest.Identity.FactoryId, current.OperationId,
            current.OperationCount, target, api, FactoryReservationRecords.Hash(body), Guid.NewGuid(), clock.GetUtcNow(),
            FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested)
        { LogicalIdentity = "factory:supplemental-" + kind, DeploymentTarget = deployment };
        var record = await store.WriteAsync(ProductConfigurationKeys.SupplementalOperation(current.OperationId, operation.Index),
            FactoryReservationRecords.Serialize(operation), null, token);
        current = current with { OperationCount = current.OperationCount + 1 };
        await SaveAsync(admission, store, token);
        return (operation, record);
    }

    private async Task SaveAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        await CurrentAsync(admission, store, token);
        root = await store.WriteAsync(ProductConfigurationKeys.FactorySupplemental, FactoryReservationRecords.Serialize(current), root!.ETag, token);
    }
    private async Task DispatchCurrentAsync(FactoryReservationResult admission, OwnerReservationStore store,
        IEnumerable<ConfigurationSetting> intents, CancellationToken token)
    {
        foreach (var effect in effects)
        {
            var record = await store.ReadAsync(ProductConfigurationKeys.SupplementalOperation(current.OperationId, effect.Index), token);
            if (record?.Value != FactoryReservationRecords.Serialize(effect)) throw new JsonException("Source effect receipt changed.");
        }
        foreach (var intent in intents)
        {
            var record = await store.ReadAsync(intent.Key, token);
            if (record is null || record.ETag != intent.ETag || record.Value != intent.Value) throw new JsonException("Source dispatch intent changed.");
        }
        await CurrentAsync(admission, store, token);
    }
    private async Task CurrentAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var observed = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token);
        var profile = await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token);
        _ = FactoryGovernance.ReadInactiveManifest(observed?.Value, profile?.Value, admission.Identity!.ProductKey,
            admission.Foundation!.ProductEndpoint!, admission.Foundation.ProfilePin);
        if (observed?.Value != manifestValue) throw new JsonException("Exact supplemental governance changed.");
        if (root is not null)
        {
            var value = await store.ReadAsync(root.Key, token);
            if (value is null || value.ETag != root.ETag || value.Value != root.Value) throw new JsonException("Supplemental conditional authority changed.");
        }
    }
    private async Task<ConfigurationSetting> ReadGovernanceAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var record = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token) ?? throw new JsonException();
        var profile = await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token);
        manifest = FactoryGovernance.ReadInactiveManifest(record.Value, profile?.Value, admission.Identity!.ProductKey,
            admission.Foundation!.ProductEndpoint!, admission.Foundation.ProfilePin);
        if (manifest.Identity != admission.Identity || manifest.ReviewedPlanSha256 != FactoryReservationRecords.Hash(BoundaryPlanFingerprint.Canonical(
            JsonSerializer.SerializeToElement(admission.Plan!, FactoryReservationRecords.Json)))) throw new JsonException();
        manifestValue = record.Value;
        return record;
    }
    private async Task<FactoryReservationResult> CloseAsync(FactoryReservationResult admission, OwnerReservationStore store,
        SupplementalState state, string detail, CancellationToken token)
    {
        if (root is not null)
        {
            current = current with { State = state, Stage = current.Stage == "preparation" ? "held" : current.Stage, Detail = detail, Checks = [] };
            try { root = await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(current), root.ETag, token); }
            catch (ReservationStoreException)
            { return Result(admission, SupplementalState.Unknown, "Final conditional hold unconfirmed; previous authority may remain. Administrator must inspect; no revocation or retry asserted."); }
        }
        return Result(admission, state, detail);
    }
    private FactoryReservationResult Result(FactoryReservationResult admission, SupplementalState state, string detail) =>
        admission with { State = state is SupplementalState.ReadySupplemental or SupplementalState.NotSelected ? FactoryReservationState.Reserved
            : Enum.Parse<FactoryReservationState>(state.ToString()), Detail = detail,
            Supplemental = current is null ? admission.Supplemental : current with { State = state, Detail = detail } };
    private OwnerReservationStore Store(FactoryReservationResult admission)
    {
        if (admission.Foundation?.State != FactoryFoundationState.Inactive || admission.Identity is null || admission.Plan is null)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Completed provenance-proven inactive foundation required; no downgrade to greenfield.");
        return new(admission.Foundation.ProductEndpoint!, admission.Identity.TenantId, admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);
    }
    private FactoryPreparationArm Arm(FactoryReservationResult admission) => new(azure, http, admission.Identity!);
    private static bool ProviderFailure(Exception e) => e is JsonException or InvalidOperationException or ArgumentException or SupplementalSourceException
        or KeyNotFoundException or HttpRequestException or IOException or OwnerReservationStore.AuthenticationFailedException;
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && Guid.TryParse(values.Single(), out var id) && id != Guid.Empty ? id.ToString() : throw new JsonException();
}
