using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

internal sealed partial class PreviewContextTransport
{
    internal FactorySupplemental? Supplemental;
    internal PreviewSupplementalConfiguration? SupplementalConfiguration;
    internal IReadOnlyList<PreviewSupplementalHost> SupplementalHosts => SupplementalConfiguration?.Hosts ?? [];
    internal static string SupplementalEndpoint(string kind) => "https://preview-" + kind + ".private.azurecontainerapps.io";
    internal Func<HttpRequestMessage, HttpResponseMessage?>? SupplementalNativeResponse { get; set; }
    internal Func<string, HttpResponseMessage?>? SupplementalNamespaceResponse { get; set; }
    internal Func<string, string>? ChangeSupplementalResponse { get; set; }
    internal Func<JsonNode, JsonNode>? ChangeAuthorityResponse { get; set; }
    internal HttpStatusCode? AuthorityStatus { get; set; }
    internal string WebPayload { get; set; } = """{"webResults":[]}""";
    internal string FoundryScenario { get; set; } = "";
    internal string? SupplementalTokenPrincipal { get; set; }
    internal string? SupplementalTokenClient { get; set; }
    internal string? SupplementalTokenTenant { get; set; }
    internal string? SupplementalTokenIdentity { get; set; }
    internal Action<string>? OnSupplementalQuery { get; set; }
    internal List<(string Kind, string Body)> SupplementalQueries { get; } = [];
    internal FoundationOperation SupplementalIdentity(PreviewSupplementalHost host) => Enumerable.Range(0, Supplemental!.OperationCount)
        .Select(i => JsonSerializer.Deserialize<FoundationOperation>(Source.Values[ProductConfigurationKeys.SupplementalOperation(Supplemental.OperationId, i)],
            FactorySourceValidation.RecordJson)!).Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(Supplemental.Configuration!, host.Kind));

    internal void RepinSupplementalEffects()
    {
        var effects = Enumerable.Range(0, Supplemental!.OperationCount).Select(i => JsonSerializer.Deserialize<FoundationOperation>(
            Source.Values[ProductConfigurationKeys.SupplementalOperation(Supplemental.OperationId, i)], FactorySourceValidation.RecordJson)!).ToArray();
        Supplemental = Supplemental with { EffectHashes = effects.Select(FactoryTelemetryValidation.Hash).ToArray() };
        Source.Values[ProductConfigurationKeys.FactorySupplemental] = JsonSerializer.Serialize(Supplemental, FactoryGitHubJson.Options);
        RepinSupplementalConfiguration(SupplementalConfiguration! with { DeploymentRevision = FactoryModelsValidation.Hash(effects) });
    }

    internal void RepinSupplementalConfiguration(PreviewSupplementalConfiguration selection)
    {
        var config = Capability.Configuration with { Supplemental = selection, SupplementalRevision = FactoryModelsValidation.Hash(selection) };
        Source.Values[FactoryPreviewSupplementalValidation.ConfigurationKey(Capability.OperationId)] = JsonSerializer.Serialize(selection, FactoryGitHubJson.Options);
        Capability = Capability with { Configuration = config, ApprovalFingerprint = FactoryModelsValidation.Hash(config) };
        Source.Manifest = Source.Manifest with { PreviewApprovalFingerprint = Capability.ApprovalFingerprint };
        Source.Values[ProductConfigurationKeys.FactoryPreview] = JsonSerializer.Serialize(Capability, FactoryGitHubJson.Options);
        Source.Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(Source.Manifest, FactoryGitHubJson.Options);
        Request = Request with { ApprovalFingerprint = Capability.ApprovalFingerprint };
    }

    private async Task PrepareSupplementalsAsync(IReadOnlyList<string> kinds)
    {
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(Source.Values[ProductConfigurationKeys.FactoryManifest], FactorySourceValidation.RecordJson)!;
        Models = FactoryModelsValidation.ReadPreparation(manifest, Source.Values);
        var operation = Guid.NewGuid();
        var roles = "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/";
        var selections = kinds.Select(kind => kind == "webiq"
            ? new SupplementalSourceSelection("webiq", "Public retail accessibility research", true) { SecretName = "webiq-api-key", SecretVersion = new string('c', 32) }
            : new SupplementalSourceSelection("foundryiq", "Public retail accessibility research", true) {
                SearchResourceId = "/subscriptions/" + manifest.Identity.SubscriptionId + "/resourcegroups/knowledge/providers/microsoft.search/searchservices/product-search",
                KnowledgeBase = "product-kb", KnowledgeBaseETag = "kb-v1", DedicatedSearchService = true,
                MetadataRoleDefinitionId = roles + "77777777-7777-7777-7777-777777777777",
                KnowledgeSources = [new("product-docs", "product-index", "source-v1", "index-v1")] }).ToArray();
        var vault = kinds.Contains("webiq") ? JsonSerializer.Deserialize<FoundationOperation>(AuthorityTransport.ReadFixture()
            .Single(p => p.Key.Contains("/github-bindings/", StringComparison.Ordinal) && p.Key.EndsWith("/vault", StringComparison.Ordinal)).Value, FactorySourceValidation.RecordJson) : null;
        var selection = new SupplementalSelection(1, "example.azurecr.io/dsf/runtime@sha256:" + new string('4', 64),
            "bcbcbcbc-bcbc-bcbc-bcbc-bcbcbcbcbcbc", selections);
        var config = new SupplementalConfiguration(manifest.Identity.FactoryId, manifest.Identity.PlanFingerprint, manifest.ProfilePin, Source.ProductEndpoint,
            Group, "westeurope", selection, Models.Bindings![2], vault) { TemplateSha256 = new string('5', 64) };
        var prerequisite = new Dictionary<string, string>();
        if (vault is not null) Native[vault.Target] = JsonSerializer.SerializeToNode(new { id = vault.Target,
            systemData = new { createdAt = vault.Incarnation, lastModifiedAt = vault.Incarnation }, properties = new {
                enableRbacAuthorization = true, publicNetworkAccess = "Disabled", accessPolicies = Array.Empty<object>() } })!;
        foreach (var selected in selections.Where(s => s.Kind == "foundryiq"))
        {
            Native[selected.SearchResourceId!] = JsonSerializer.SerializeToNode(new { id = selected.SearchResourceId,
                properties = new { disableLocalAuth = true, publicNetworkAccess = "Disabled" } })!;
            prerequisite[selected.SearchResourceId!] = FactoryTelemetryValidation.Hash(JsonSerializer.SerializeToElement(Native[selected.SearchResourceId!]));
        }
        config = config with { ResourceApprovalSha256 = FactoryTelemetryValidation.Hash(FactorySupplementalValidation.Resources(config)), PrerequisiteRevisions = prerequisite };
        var targets = FactorySupplementalValidation.Targets(config);
        var effects = new List<FoundationOperation>();
        var hosts = new List<PreviewSupplementalHost>();
        Supplemental = new(SupplementalState.ReadySupplemental, "Actual source-host verification recorded.") {
            OperationId = operation, ApprovalFingerprint = FactoryTelemetryValidation.Hash(config), Configuration = config, Stage = "observed", CheckId = Guid.NewGuid() };
        foreach (var source in selections)
        {
            var resource = FactorySupplementalValidation.Resources(config).Single(r => r.Kind == source.Kind);
            var principal = source.Kind == "webiq" ? "91919191-9191-9191-9191-919191919191" : "92929292-9292-9292-9292-929292929292";
            var client = source.Kind == "webiq" ? "93939393-9393-9393-9393-939393939393" : "94949494-9494-9494-9494-949494949494";
            var created = Source.Clock.Now.AddMinutes(-1);
            Native[resource.IdentityTarget] = JsonSerializer.SerializeToNode(new { id = resource.IdentityTarget,
                systemData = new { createdAt = created, lastModifiedAt = created },
                properties = new { principalId = principal, clientId = client, tenantId = manifest.Identity.TenantId } })!;
            var identity = Receipt(resource.IdentityTarget, "2024-11-30", FactoryTelemetryValidation.Hash(JsonSerializer.SerializeToElement(
                new { created, principal, client, tenant = manifest.Identity.TenantId, target = resource.IdentityTarget })), created) with
                { PrincipalId = principal, ClientId = client, LogicalIdentity = "factory:supplemental-" + source.Kind };
            effects.Add(identity);
            foreach (var grant in resource.Grants)
            {
                Native[grant.Target] = JsonSerializer.SerializeToNode(new { id = grant.Target, properties = new { scope = grant.Scope,
                    principalId = principal, principalType = "ServicePrincipal", roleDefinitionId = grant.RoleDefinitionId,
                    createdBy = manifest.Identity.AzureActorId, createdOn = created, updatedOn = created, condition = (string?)null, conditionVersion = (string?)null } })!;
                effects.Add(Receipt(grant.Target, "2022-04-01", FactoryTelemetryValidation.Hash(JsonSerializer.SerializeToElement(Native[grant.Target]!["properties"])), created)
                    with { PrincipalId = principal, LogicalIdentity = "factory:supplemental-" + source.Kind, RoleScope = grant.Scope, RoleDefinitionId = grant.RoleDefinitionId });
                var builtin = grant.RoleDefinitionId.EndsWith("/" + FactorySupplementalValidation.SearchDataRole, StringComparison.Ordinal)
                    || grant.RoleDefinitionId.EndsWith("/" + FactorySupplementalValidation.SecretsRole, StringComparison.Ordinal);
                Native[grant.RoleDefinitionId] = Role(grant.RoleDefinitionId, builtin ? "BuiltInRole" : "CustomRole", grant.Actions, grant.DataActions);
            }
            var workload = FactorySupplementalValidation.HostConfiguration(manifest, Supplemental, identity, source.Kind);
            var host = Source.Resources[Source.HostTarget].DeepClone();
            host["id"] = resource.HostTarget;
            host["systemData"] = JsonSerializer.SerializeToNode(new { createdAt = created, lastModifiedAt = created });
            host["identity"]!["userAssignedIdentities"] = new JsonObject { [identity.Target] = new JsonObject() };
            var container = host["properties"]!["template"]!["containers"]![0]!;
            container["image"] = config.Selection.RuntimeImage;
            container["args"] = new JsonArray("serve-supplemental");
            foreach (var env in container["env"]!.AsArray()) if ((string?)env!["name"] == "AZURE_CLIENT_ID") env["value"] = client;
            container["env"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { name = "DSF_SUPPLEMENTAL_CONFIG", value = JsonSerializer.Serialize(workload, FactoryGitHubJson.Options) }));
            host["properties"]!["configuration"]!["ingress"]!["fqdn"] = source.Kind + ".private.azurecontainerapps.io";
            var auth = Source.Resources[Source.HostTarget + "/authconfigs/current"].DeepClone();
            auth["id"] = resource.HostTarget + "/authconfigs/current";
            auth["systemData"] = host["systemData"]!.DeepClone();
            var aad = auth["properties"]!["identityProviders"]!["azureActiveDirectory"]!;
            aad["registration"]!["clientId"] = config.Selection.AudienceClientId;
            aad["validation"]!["allowedAudiences"] = new JsonArray(config.Selection.AudienceClientId, "api://" + config.Selection.AudienceClientId);
            aad["validation"]!["defaultAuthorizationPolicy"]!["allowedPrincipals"]!["identities"] = new JsonArray(manifest.Identity.AzureActorId);
            foreach (var native in new[] { host, auth })
            {
                var target = (string)native["id"]!;
                Native[target] = native;
                effects.Add(Receipt(target, "2025-01-01", FactoryHostedWorkloadPolicy.ObserveExactHost(JsonSerializer.SerializeToElement(native), target).Revision, created));
            }
            var metadataId = roles + (source.Kind == "webiq" ? "95959595-9595-9595-9595-959595959595" : "96969696-9696-9696-9696-969696969696");
            Native[metadataId] = Role(metadataId, "CustomRole", FactoryPreviewSupplementalValidation.MetadataActions(source.Kind), []);
            hosts.Add(new(source.Kind, FactoryModelsValidation.Hash(identity), principal, client, "example.azurecr.io/dsf/runtime@sha256:" + new string('3', 64),
                source.Kind == "webiq" ? "97979797-9797-9797-9797-979797979797" : "98989898-9898-9898-9898-989898989898",
                metadataId, FactoryModelsValidation.Hash(JsonSerializer.SerializeToElement(Native[metadataId]["properties"])),
                FactoryPreviewSupplementalValidation.MetadataScope(config, source)));
        }
        Supplemental = Supplemental with { OperationCount = effects.Count, EffectHashes = effects.Select(FactoryTelemetryValidation.Hash).ToArray() };
        SupplementalConfiguration = new(Supplemental.ApprovalFingerprint!, FactoryModelsValidation.Hash(effects), hosts);
        foreach (var effect in effects) Source.Values[ProductConfigurationKeys.SupplementalOperation(operation, effect.Index)] = JsonSerializer.Serialize(effect, FactoryGitHubJson.Options);
        var checks = new List<SupplementalCheck>();
        foreach (var host in hosts)
        {
            var workload = FactorySupplementalValidation.HostConfiguration(manifest, Supplemental, SupplementalIdentity(host), host.Kind);
            var runner = new PreviewContextRunner(Source) { Route = "/proof", Request = new SupplementalProofRequest(Supplemental.CheckId!.Value, Supplemental.ApprovalFingerprint!) };
            using var http = new HttpClient(this, false);
            var env = Source.Environment;
            env["AZURE_CLIENT_ID"] = host.ClientId;
            env["DSF_SUPPLEMENTAL_CONFIG"] = JsonSerializer.Serialize(workload, FactoryGitHubJson.Options);
            Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], env, new StringWriter(), new StringWriter(),
                RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http, SourceCredential = new PreviewSupplementalCredential(this, host), SourceClock = Source.Clock }, CancellationToken.None));
            Assert.True(runner.Status == HttpStatusCode.OK, runner.Body);
            var proof = JsonSerializer.Deserialize<SupplementalProof>(runner.Body, FactorySourceValidation.RecordJson)!;
            var key = ProductConfigurationKeys.SupplementalProof(operation, Supplemental.CheckId!.Value, host.Kind);
            Source.Values[key] = JsonSerializer.Serialize(proof, FactoryGitHubJson.Options);
            checks.Add(new(host.Kind, proof.State, proof.Reason, key, FactoryTelemetryValidation.Hash(proof)));
        }
        Supplemental = Supplemental with { Checks = checks };
        Source.Values[ProductConfigurationKeys.FactorySupplemental] = JsonSerializer.Serialize(Supplemental, FactoryGitHubJson.Options);
        Source.Manifest = manifest with { SupplementalOperationId = operation, SupplementalApprovalFingerprint = Supplemental.ApprovalFingerprint };
        Source.Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(Source.Manifest, FactoryGitHubJson.Options);
        Requests.Clear();
        SupplementalQueries.Clear();
        EnforceCouncilScopes = true;

        FoundationOperation Receipt(string target, string api, string revision, DateTimeOffset created)
        {
            var resources = FactorySupplementalValidation.Resources(config).Single(r => r.IdentityTarget == target || r.HostTarget == target
                || r.HostTarget + "/authconfigs/current" == target || r.Grants.Any(g => g.Target == target));
            var hosted = resources.HostTarget == target || resources.HostTarget + "/authconfigs/current" == target;
            return new(1, FactorySupplementalValidation.OperationKind,
            manifest.Identity.FactoryId, operation, targets.ToList().FindIndex(t => t.Target == target), target, api, new string('a', 64), Guid.NewGuid(),
            created, FoundationOperationState.Completed, FoundationResourceDisposition.Created) {
            ResourceId = target, Incarnation = created.ToString("O"), NativeRevision = revision, HttpStatus = 201,
            LogicalIdentity = "factory:supplemental-" + resources.Kind,
            ProviderOperationId = hosted ? Guid.NewGuid().ToString("N") : null,
            ProviderRequestId = Guid.NewGuid().ToString(), ProviderCorrelationId = Guid.NewGuid().ToString(),
            DeploymentTarget = hosted ? Group + "/providers/microsoft.resources/deployments/sp-" + operation.ToString("N") + "-" + resources.Kind : null };
        }
        static JsonNode Role(string id, string type, IReadOnlyList<string> actions, IReadOnlyList<string> data) => JsonSerializer.SerializeToNode(
            new { id, properties = new { type, permissions = new[] { new { actions, dataActions = data, notActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } } })!;
    }

    internal async Task<PreviewContextRunner> RunSupplementalsAsync(string? kind = null, string? caller = null, string? client = null,
        object? request = null, string? route = null)
    {
        var requests = Requests.Count;
        var writes = Source.Writes.Count;
        var runner = new PreviewContextRunner(Source) { Route = route ?? (kind is null ? "/supplemental-evidence" : "/evidence"),
            Caller = caller ?? (kind is null ? Source.Manifest.Identity.AzureActorId : Models.Bindings![0].PrincipalId!),
            Client = client ?? Models.Bindings![0].ClientId!, Request = request ?? (kind is null ? Request : new PreviewSupplementalRequest(Request, kind)) };
        using var http = new HttpClient(this, false);
        var env = Source.Environment;
        var host = kind is null ? null : SupplementalHosts.Single(h => h.Kind == kind);
        env["AZURE_CLIENT_ID"] = host?.ClientId ?? Models.Bindings![0].ClientId;
        if (host is not null)
        {
            env["DSF_SUPPLEMENTAL_CONFIG"] = JsonSerializer.Serialize(FactorySupplementalValidation.HostConfiguration(Source.Manifest, Supplemental!, SupplementalIdentity(host), kind!), FactoryGitHubJson.Options);
            env["DSF_PREVIEW_COORDINATOR"] = JsonSerializer.Serialize(new PreviewSupplementalCoordinator(CouncilEndpoint, Capability.Configuration.AudienceClientId,
                Models.Bindings![0].PrincipalId!, Models.Bindings[0].ClientId!), FactoryGitHubJson.Options);
        }
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync([host is null ? "serve-preview" : "serve-preview-supplemental"], env, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, PreviewHttp = http, PreviewCredential = new IntentModelCredential(Source),
                PreviewClock = Source.Clock, SourceHttp = http, SourceCredential = host is null ? null : new PreviewSupplementalCredential(this, host), SourceClock = Source.Clock }, CancellationToken.None));
        Assert.DoesNotContain(Requests.Skip(requests), r => r.Method is "PUT" or "PATCH" or "DELETE");
        Assert.Equal(writes, Source.Writes.Count);
        return runner;
    }

    private async Task<HttpResponseMessage?> SupplementalRequestAsync(HttpRequestMessage request, string principal, CancellationToken token)
    {
        var uri = request.RequestUri!;
        if (uri.Host == new Uri(Source.ProductEndpoint).Host && uri.AbsolutePath == "/kv"
            && Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("key", out var filter)
            && filter.ToString().Contains("supplemental", StringComparison.Ordinal) && SupplementalNamespaceResponse is not null
            && SupplementalNamespaceResponse(filter.ToString()) is { } namespaceReply)
            return namespaceReply;
        var source = SupplementalHosts.SingleOrDefault(h => new Uri(SupplementalEndpoint(h.Kind)).Host == uri.Host);
        if (source is not null)
        {
            Assert.Equal(Models.Bindings![0].PrincipalId, principal);
            var input = JsonSerializer.Deserialize<PreviewSupplementalRequest>(await request.Content!.ReadAsStringAsync(token), FactorySourceValidation.RecordJson)!;
            var result = await RunSupplementalsAsync(source.Kind, request: input);
            return FactorySupplementalHostTests.Json(result.Status, ChangeSupplementalResponse?.Invoke(result.Body) ?? result.Body);
        }
        if (uri.Host == new Uri(CouncilEndpoint).Host && uri.AbsolutePath == "/supplemental-authority")
        {
            var host = SupplementalHosts.Single(h => h.PrincipalId == principal);
            var input = JsonSerializer.Deserialize<PreviewSupplementalRequest>(await request.Content!.ReadAsStringAsync(token), FactorySourceValidation.RecordJson)!;
            var result = await RunSupplementalsAsync(caller: principal, client: host.ClientId, request: input, route: "/supplemental-authority");
            return FactorySupplementalHostTests.Json(AuthorityStatus ?? result.Status,
                ChangeAuthorityResponse is null ? result.Body : ChangeAuthorityResponse(JsonNode.Parse(result.Body)!).ToJsonString());
        }
        if (uri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal) && SupplementalHosts.Any(h => h.PrincipalId == principal))
            throw new Xunit.Sdk.XunitException("Supplemental identity must not read product/source configuration.");
        if (uri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal))
        {
            var web = SupplementalHosts.Single(h => h.Kind == "webiq");
            Assert.Equal(web.PrincipalId, principal);
            Assert.Equal("/secrets/webiq-api-key/" + new string('c', 32), uri.AbsolutePath);
            if (SupplementalNativeResponse?.Invoke(request) is { } custom) return custom;
            return SourceInventoryTransport.Json(new { id = uri.GetLeftPart(UriPartial.Path), value = "secret-key-do-not-print",
                attributes = new { enabled = true, created = Source.Clock.Now.AddDays(-1).ToUnixTimeSeconds(), updated = Source.Clock.Now.AddDays(-1).ToUnixTimeSeconds(),
                    nbf = Source.Clock.Now.AddDays(-1).ToUnixTimeSeconds(), exp = Source.Clock.Now.AddDays(1).ToUnixTimeSeconds() } });
        }
        if (uri.Host == "api.microsoft.ai")
        {
            Assert.Equal("https://api.microsoft.ai/v3/search/web", uri.AbsoluteUri);
            Assert.Equal("secret-key-do-not-print", request.Headers.GetValues("x-apikey").Single());
            var body = await request.Content!.ReadAsStringAsync(token);
            SupplementalQueries.Add(("webiq", body));
            Assert.Equal("Public retail accessibility research", JsonNode.Parse(body)!["query"]!.GetValue<string>());
            OnSupplementalQuery?.Invoke("webiq");
            return SupplementalNativeResponse?.Invoke(request) ?? FactorySupplementalHostTests.Json(HttpStatusCode.OK, WebPayload);
        }
        if (uri.Host == "product-search.search.windows.net")
        {
            Assert.Equal(SupplementalHosts.Single(h => h.Kind == "foundryiq").PrincipalId, principal);
            if (request.Method == HttpMethod.Post)
            {
                SupplementalQueries.Add(("foundryiq", await request.Content!.ReadAsStringAsync(token)));
                OnSupplementalQuery?.Invoke("foundryiq");
            }
            if (SupplementalNativeResponse?.Invoke(request) is { } response) return response;
            using var backend = new FactorySupplementalHostTests.FoundryTransport { Scenario = FoundryScenario };
            using var invoker = new HttpMessageInvoker(backend, false);
            return await invoker.SendAsync(request, token);
        }
        return null;
    }

    private sealed class PreviewSupplementalCredential(PreviewContextTransport owner, PreviewSupplementalHost host) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext request, CancellationToken token)
        {
            var claims = new { oid = owner.SupplementalTokenPrincipal ?? host.PrincipalId, appid = owner.SupplementalTokenClient ?? host.ClientId,
                tid = owner.SupplementalTokenTenant ?? owner.Source.Manifest.Identity.TenantId,
                xms_mirid = owner.SupplementalTokenIdentity ?? owner.SupplementalIdentity(host).Target, ver = "1.0",
                aud = request.Scopes.Single()[..^"/.default".Length], nbf = owner.Source.Clock.Now.AddMinutes(-1).ToUnixTimeSeconds(),
                exp = owner.Source.Clock.Now.AddHours(1).ToUnixTimeSeconds() };
            return new("test." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test",
                owner.Source.Clock.Now.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken token) => ValueTask.FromResult(GetToken(request, token));
    }
}
