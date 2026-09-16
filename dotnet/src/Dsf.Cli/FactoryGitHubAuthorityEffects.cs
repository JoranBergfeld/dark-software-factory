using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryGitHubAuthorityService(
    IAzureCliRunner azure, HttpClient? http, TimeProvider clock, RepositoryAttachmentReviewer? reviewer = null)
{
    private static readonly HttpClient ProviderHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private const string OperationKind = "decide-github-operation";
    private ConfigurationSetting? root;
    private FactoryGitHubAuthority? authority;
    private bool manifestIntentConfirmed;
    private readonly List<FoundationOperation> operations = [];

    internal async Task<FactoryReservationResult> ApplyAsync(
        FactoryReservationResult admission, FactoryGitHubAuthority preview, string approval, CancellationToken token)
    {
        if (admission.GitHubAuthority is not null) return admission;
        if (preview.ApprovalFingerprint != approval)
            return admission with { State = FactoryReservationState.Blocked, Detail = "Exact GitHub authority approval differs; no effects." };
        var identity = admission.Identity!;
        var store = ProductStore(admission);
        try
        {
            await VerifyRepositoryAutomationAsync(admission, token);
            await VerifyBaseBindingsAsync(admission, preview, token);
            await VerifyKeyIsolationAsync(identity, preview, token);
            var manifestRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token) ?? throw new JsonException();
            var manifest = FactoryReservationRecords.Parse<FactoryManifest>(manifestRecord.Value);
            if (manifest.GitHubAuthorityOperationId is not null)
                throw new ReservationStoreException(FactoryReservationState.Unknown, "An earlier GitHub authority intent remains held; inspect only, no replacement operation.");
            var source = await ArmAsync(HttpMethod.Get, preview.SourceVaultId, "2024-11-01", null, identity, null, token);
            if (source.StatusCode != HttpStatusCode.OK)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "GET approved owner vault denied; owner vault administrator must establish exact source identity and ARM template retrieval. No copy.");
            using (source)
            using (var doc = JsonDocument.Parse(await source.Content.ReadAsStringAsync(token)))
            {
                var props = doc.RootElement.GetProperty("properties");
                if (!string.Equals(doc.RootElement.GetProperty("id").GetString(), preview.SourceVaultId, StringComparison.OrdinalIgnoreCase)
                    || props.GetProperty("tenantId").GetString() != identity.TenantId
                    || new Uri(props.GetProperty("vaultUri").GetString()!).Host != new Uri(admission.Plan!.Selection.OwnerKeyVaultUri).Host
                    || props.GetProperty("enabledForTemplateDeployment").ValueKind != JsonValueKind.True)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "GET approved owner vault identity/template retrieval mismatch; owner vault administrator must resolve outside DSF.");
            }
            authority = preview with { OperationId = Guid.NewGuid(), Stage = "key-copy", Detail = Risk, AcceptedSigningRisk = true };
            await store.WriteAsync(manifestRecord.Key,
                FactoryReservationRecords.Serialize(manifest with { GitHubAuthorityOperationId = authority.OperationId }),
                manifestRecord.ETag, token);
            manifestIntentConfirmed = true;
            root = await store.WriteAsync(ProductConfigurationKeys.FactoryGitHubAuthority,
                FactoryReservationRecords.Serialize(authority), null, token);
            var group = new FactoryFoundationAssets(identity, admission.Plan!.Selection).Group;
            using var template = JsonDocument.Parse(File.ReadAllText(new ProvisioningAssets().Resolve("product-github-private-key.json")));
            var body = JsonSerializer.Serialize(new
            {
                properties = new
                {
                    mode = "Incremental", template = template.RootElement,
                    parameters = new
                    {
                        vaultName = new { value = authority.DestinationVaultId.Split('/').Last() },
                        githubAppPrivateKey = new
                        {
                            reference = new
                            {
                                keyVault = new { id = authority.SourceVaultId },
                                secretName = "github-app-private-key", secretVersion = authority.SourceSecretVersion,
                            },
                        },
                    },
                },
            });
            var secretTarget = authority.DestinationVaultId + "/secrets/github-app-private-key";
            using var absence = await ArmAsync(HttpMethod.Get, secretTarget, "2024-11-01", null, identity, null, token);
            if (absence.StatusCode != HttpStatusCode.NotFound)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "GET destination signing key did not prove absence; factory vault administrator must reconcile. No overwrite or name-based adoption.");
            var copy = await DispatchAsync(store, identity, group + "/providers/microsoft.resources/deployments/gh-" + authority.OperationId.ToString("N") + "-key",
                "2025-04-01", body, secretTarget, token);
            if (copy.State != FoundationOperationState.Completed)
                throw new ReservationStoreException(copy.State == FoundationOperationState.Failed ? FactoryReservationState.Failed : FactoryReservationState.Unknown,
                    "Key-copy response is held; inspect this operation only. No retry, overwrite or owner rotation.");
            await ProveDeploymentResourceAsync(store, identity, copy, secretTarget, "2024-11-01", token);
            using var secretMetadata = await ArmAsync(HttpMethod.Get, secretTarget, "2024-11-01", null, identity, null, token);
            if (secretMetadata.StatusCode != HttpStatusCode.OK) throw new JsonException();
            using var secretJson = JsonDocument.Parse(await secretMetadata.Content.ReadAsStringAsync(token));
            var versioned = secretJson.RootElement.GetProperty("properties").GetProperty("secretUriWithVersion").GetString();
            if (versioned != operations[copy.Index].Endpoint || versioned is null || !Regex.IsMatch(versioned,
                "^https://" + Regex.Escape(authority.DestinationVaultId.Split('/').Last()) + @"\.vault\.azure\.net/secrets/github-app-private-key/[0-9a-f]{32}$"))
                throw new JsonException();
            authority = authority with { DestinationSecretUri = versioned, Stage = "workload-grants" };
            await SaveAsync(store, token);
            await GrantAsync(store, identity, secretTarget, "4633458b-17de-408a-b874-0445c86b69e6", token);
            await GrantAsync(store, identity, group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + identity.FactoryId.ToString("N"),
                "516239f1-63e1-4d78-a4de-a74fb236a071", token);
            var assets = new FactoryFoundationAssets(identity, admission.Plan.Selection);
            var hostTarget = group + "/providers/microsoft.app/containerapps/" + assets.Prefix + "-github-auth";
            using var hostAbsent = await ArmAsync(HttpMethod.Get, hostTarget, "2025-01-01", null, identity, null, token);
            if (hostAbsent.StatusCode != HttpStatusCode.NotFound)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "GitHub auth host already exists or cannot be inspected; no overwrite or adoption.");
            using var hostTemplate = JsonDocument.Parse(File.ReadAllText(new ProvisioningAssets().Resolve("onboard-github-auth.json")));
            var hostBody = JsonSerializer.Serialize(new { properties = new
            {
                mode = "Incremental", template = hostTemplate.RootElement, parameters = new
                {
                    namePrefix = new { value = assets.Prefix }, location = new { value = admission.Plan.Selection.FactoryLocation },
                    runtimeImage = new { value = authority.RuntimeImage }, productKey = new { value = identity.ProductKey },
                    productEndpoint = new { value = admission.Foundation!.ProductEndpoint }, profilePin = new { value = admission.Foundation.ProfilePin },
                    tenantId = new { value = identity.TenantId }, audienceClientId = new { value = authority.AudienceClientId },
                    operatorPrincipalId = new { value = identity.AzureActorId },
                },
            } });
            authority = authority with { HostResourceId = hostTarget, Stage = "host-deployment" };
            await SaveAsync(store, token);
            var host = await DispatchAsync(store, identity, group + "/providers/microsoft.resources/deployments/gh-" + authority.OperationId.ToString("N") + "-host",
                "2025-04-01", hostBody, hostTarget, token);
            if (host.State != FoundationOperationState.Completed)
                throw new ReservationStoreException(host.State == FoundationOperationState.Failed ? FactoryReservationState.Failed : FactoryReservationState.Unknown,
                    "Host dispatch unresolved; held without reissue.");
            await ProveDeploymentResourceAsync(store, identity, host, hostTarget, "2025-01-01", token);
            var policy = operations.Single(o => o.Target == hostTarget + "/authconfigs/current");
            await ProveDeploymentResourceAsync(store, identity, policy, policy.Target, policy.ApiVersion, token);
            await VerifyNativeHostPolicyAsync(identity, hostTarget, token);
            using var hostResponse = await ArmAsync(HttpMethod.Get, hostTarget, "2025-01-01", null, identity, null, token);
            if (hostResponse.StatusCode != HttpStatusCode.OK) throw new JsonException();
            using var hostJson = JsonDocument.Parse(await hostResponse.Content.ReadAsStringAsync(token));
            var fqdn = hostJson.RootElement.GetProperty("properties").GetProperty("configuration").GetProperty("ingress").GetProperty("fqdn").GetString();
            if (fqdn is null || !Regex.IsMatch(fqdn, "^[a-z0-9.-]+\\.azurecontainerapps\\.io$")) throw new JsonException();
            authority = authority with { HostEndpoint = "https://" + fqdn, Stage = "host-verification" };
            await SaveAsync(store, token);
            await VerifyDeployedAsync(store, identity, token);
            return CapabilityResult(admission);
        }

        catch (ReservationStoreException e)
        { return await CloseAsync(admission, store, Enum.Parse<GitHubAuthorityState>(e.State.ToString()), e.Message, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(admission, GitHubAuthorityState.Canceled, "Canceled; dispatch closed. In-flight provider operations may continue; inspect same operation, never reissue."); }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or HttpRequestException
            or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException or KeyNotFoundException or ArgumentOutOfRangeException)
        { return await CloseAsync(admission, store, GitHubAuthorityState.Unknown, "Authority/provider evidence unavailable or invalid; details withheld. Existing operations remain held.", token); }
    }

    private async Task<FactoryReservationResult> CloseAsync(FactoryReservationResult admission, OwnerReservationStore store,
        GitHubAuthorityState state, string detail, CancellationToken token)
    {
        if (root is not null && authority is not null && !token.IsCancellationRequested)
        {
            authority = authority with { State = state, Detail = detail };
            try { await SaveAsync(store, token); }
            catch (ReservationStoreException)
            { return Result(admission, GitHubAuthorityState.Unknown, "Terminal outcome could not be recorded conditionally; inspect held effects, no reissue."); }
        }
        return Result(admission, state, detail);
    }

    private async Task SaveAsync(OwnerReservationStore store, CancellationToken token) =>
        root = await store.WriteAsync(root!.Key, FactoryReservationRecords.Serialize(authority!), root.ETag, token);

    private async Task<FoundationOperation> DispatchAsync(OwnerReservationStore store, FactoryReservationIdentity identity,
        string target, string apiVersion, string body, string resourceTarget, CancellationToken token)
    {
        using var absent = await ArmAsync(HttpMethod.Get, target, apiVersion, null, identity, null, token);
        if (absent.StatusCode != HttpStatusCode.NotFound)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Exact GitHub effect target is not absent; no adoption or overwrite.");
        var operation = new FoundationOperation(1, OperationKind, identity.FactoryId, authority!.OperationId,
            authority.OperationCount, target, apiVersion, FactoryReservationRecords.Hash(body), Guid.NewGuid(), clock.GetUtcNow(),
            FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested)
        { ResourceId = resourceTarget };
        authority = authority with { OperationCount = authority.OperationCount + 1 };
        root = await store.WriteAsync(root!.Key, FactoryReservationRecords.Serialize(authority), root.ETag, token);
        var record = await store.WriteAsync(ProductConfigurationKeys.GitHubAuthorityOperation(authority.OperationId, operation.Index),
            FactoryReservationRecords.Serialize(operation), null, token);
        operations.Add(operation);
        if (resourceTarget.Contains("/providers/microsoft.app/containerapps/", StringComparison.Ordinal))
        {
            var child = operation with
            {
                Index = authority.OperationCount, Target = resourceTarget + "/authconfigs/current",
                ResourceId = resourceTarget + "/authconfigs/current", DeploymentTarget = target, ApiVersion = "2025-01-01",
            };
            authority = authority with { OperationCount = authority.OperationCount + 1 };
            await SaveAsync(store, token);
            await store.WriteAsync(ProductConfigurationKeys.GitHubAuthorityOperation(child.OperationId, child.Index),
                FactoryReservationRecords.Serialize(child), null, token);
            operations.Add(child);
        }
        using var response = await ArmAsync(HttpMethod.Put, target, apiVersion, body, identity, operation.ClientRequestId, token);
        operation = operation with { HttpStatus = (int)response.StatusCode, State = FoundationOperationState.Unknown };
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            operation = operation with { State = FoundationOperationState.Failed };
        else if (response.StatusCode == HttpStatusCode.Created
            && response.Headers.TryGetValues("x-ms-request-id", out var ids)
            && Guid.TryParse(ids.Single(), out var requestId) && requestId != Guid.Empty)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var props = document.RootElement.GetProperty("properties");
            if (string.Equals(document.RootElement.GetProperty("id").GetString(), target, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(props.GetProperty("correlationId").GetString(), out var correlation) && correlation != Guid.Empty)
            {
                operation = operation with
                {
                    State = DeploymentState(props.GetProperty("provisioningState").GetString()),
                    ProviderRequestId = requestId.ToString(), ProviderCorrelationId = correlation.ToString(),
                    ResponseDate = response.Headers.Date,
                };
                if (operation.State == FoundationOperationState.InFlight)
                {
                    record = await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
                    operations[operation.Index] = operation;
                    for (var poll = 0; poll < 120 && operation.State == FoundationOperationState.InFlight; poll++)
                    {
                        if (poll > 0) await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
                        using var observed = await ArmAsync(HttpMethod.Get, target, apiVersion, null, identity, null, token);
                        if (observed.StatusCode != HttpStatusCode.OK) throw new JsonException();
                        using var observation = JsonDocument.Parse(await observed.Content.ReadAsStringAsync(token));
                        var current = observation.RootElement;
                        if (!string.Equals(current.GetProperty("id").GetString(), target, StringComparison.OrdinalIgnoreCase)
                            || current.GetProperty("properties").GetProperty("correlationId").GetString() != operation.ProviderCorrelationId)
                            throw new JsonException();
                        operation = operation with { State = DeploymentState(current.GetProperty("properties").GetProperty("provisioningState").GetString()) };
                    }
                    if (operation.State == FoundationOperationState.InFlight)
                        operation = operation with { State = FoundationOperationState.Unknown };
                }
                if (operation.State == FoundationOperationState.Completed)
                    operation = operation with { Disposition = FoundationResourceDisposition.Created };
            }
        }
        await store.WriteAsync(record.Key, FactoryReservationRecords.Serialize(operation), record.ETag, token);
        operations[operation.Index] = operation;
        return operation;
    }

    private static FoundationOperationState DeploymentState(string? state) => state switch
    {
        "Succeeded" => FoundationOperationState.Completed,
        "Accepted" or "Running" or "Creating" => FoundationOperationState.InFlight,
        "Failed" or "Canceled" => FoundationOperationState.Failed,
        _ => FoundationOperationState.Unknown,
    };

    private async Task<HttpResponseMessage> ArmAsync(HttpMethod method, string target, string version, string? body,
        FactoryReservationIdentity identity, Guid? correlation, CancellationToken token)
    {
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "https://management.azure.com/");
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
        using var request = new HttpRequestMessage(method,
            $"https://management.azure.com{target}{(target.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api-version={version}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        if (correlation is not null) request.Headers.Add("x-ms-client-request-id", correlation.Value.ToString());
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await (http ?? ProviderHttp).SendAsync(request, token);
    }

    private OwnerReservationStore ProductStore(FactoryReservationResult admission) =>
        new(admission.Foundation!.ProductEndpoint!, admission.Identity!.TenantId, admission.Identity.SubscriptionId,
            azure, http, admission.Identity.AzureActorId);

    private FactoryReservationResult Result(FactoryReservationResult admission, GitHubAuthorityState state, string detail) =>
        admission with
        {
            State = state == GitHubAuthorityState.Verified ? FactoryReservationState.Reserved : Enum.Parse<FactoryReservationState>(state.ToString()),
            Detail = detail,
            GitHubAuthority = authority is null || !manifestIntentConfirmed ? null : authority with { State = state, Detail = detail },
            GitHubOperations = operations.Select(o => o.State == FoundationOperationState.Intent
                ? o with { State = FoundationOperationState.Unknown } : o).ToArray(),
            Foundation = admission.Foundation is { } foundation ? foundation with
            {
                PendingStages = state == GitHubAuthorityState.Verified
                    ? foundation.PendingStages.Where(s => s != "GitHub authentication").ToArray()
                    : foundation.PendingStages.Contains("GitHub authentication") ? foundation.PendingStages
                    : [.. foundation.PendingStages, "GitHub authentication"],
            } : null,
        };

    internal static async Task<FactoryReservationResult> InspectAsync(
        FactoryReservationResult admission, OwnerReservationStore product, CancellationToken token)
    {
        if (admission.Foundation?.State != FactoryFoundationState.Inactive) return admission;
        var manifest = FactoryReservationRecords.Parse<FactoryManifest>(
            (await product.ReadAsync(ProductConfigurationKeys.FactoryManifest, token))?.Value ?? throw new JsonException());
        if (manifest.GitHubAuthorityOperationId is null) return admission;
        var record = await product.ReadAsync(ProductConfigurationKeys.FactoryGitHubAuthority, token);
        if (record is null)
            return admission with { State = FactoryReservationState.Unknown, Detail = "GitHub authority intent exists without its record; held, no new operation or dispatch." };
        var authority = FactoryReservationRecords.Parse<FactoryGitHubAuthority>(record.Value);
        if (authority.SchemaVersion != 1 || authority.FactoryId != admission.FactoryId
            || authority.OperationId != manifest.GitHubAuthorityOperationId || authority.PlanFingerprint != admission.Identity!.PlanFingerprint
            || authority.RepositoryId != admission.RepositoryId || authority.OperationCount is < 0 or > 32
            || !Enum.IsDefined(authority.State))
            throw new JsonException();
        var operations = new List<FoundationOperation>();
        var state = authority.State;
        for (var i = 0; i < authority.OperationCount; i++)
        {
            var receipt = await product.ReadAsync(ProductConfigurationKeys.GitHubAuthorityOperation(authority.OperationId, i), token);
            if (receipt is null) { state = GitHubAuthorityState.Unknown; break; }
            var operation = FactoryReservationRecords.Parse<FoundationOperation>(receipt.Value);
            if (operation.Kind != OperationKind || operation.FactoryId != authority.FactoryId
                || operation.OperationId != authority.OperationId || operation.Index != i || !Enum.IsDefined(operation.State))
                throw new JsonException();
            if (operation.State is FoundationOperationState.Intent or FoundationOperationState.InFlight or FoundationOperationState.Unknown)
                state = GitHubAuthorityState.Unknown;
            else if (operation.State == FoundationOperationState.Failed && state is not (GitHubAuthorityState.Unknown or GitHubAuthorityState.Blocked))
                state = GitHubAuthorityState.Failed;
            operations.Add(operation.State is FoundationOperationState.Intent or FoundationOperationState.InFlight
                ? operation with { State = FoundationOperationState.Unknown } : operation);
        }
        if (state == GitHubAuthorityState.Verified)
        {
            ValidateCompletedEffects(admission, authority, operations);
            if (authority.Stage != "verified" || authority.Proof is not { State: GitHubAuthorityState.Verified } proof
                || !HasVerifiedScope(authority, proof))
                throw new JsonException();
        }
        return admission with
        {
            GitHubAuthority = authority with { State = state }, GitHubOperations = operations,
            State = state == GitHubAuthorityState.Verified ? FactoryReservationState.Reserved : Enum.Parse<FactoryReservationState>(state.ToString()),
            Detail = "Trusted product GitHub authority progress and historical observed proof, not a fresh access check. "
                + "Inspection never reissues key copies, grants or host deployment. " + authority.Detail,
            Foundation = state == GitHubAuthorityState.Verified ? admission.Foundation with
                { PendingStages = admission.Foundation.PendingStages.Where(s => s != "GitHub authentication").ToArray() } : admission.Foundation,
        };
    }
}
