using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed class ModelsWorkflowTransport(FactoryFoundationTests.FoundationTransport foundation) : HttpMessageHandler
{
    private readonly HttpMessageInvoker prior = new(foundation, false);
    internal const string Account = "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/model-serving/providers/microsoft.cognitiveservices/accounts/shared-models";
    internal const string Role = "/subscriptions/22222222-2222-2222-2222-222222222222/providers/microsoft.authorization/roledefinitions/aaaaaaaa-1111-2222-3333-444444444444";
    internal const string Endpoint = "https://shared-models.services.ai.azure.com";
    internal JsonNode Manifest => Record("dsf/factory/manifest");
    internal JsonNode Record(string key) => JsonNode.Parse((string)JsonNode.Parse(foundation.Product.ListJson())!.AsArray()
        .Single(r => (string?)r!["key"] == key)!["value"]!)!;
    internal JsonNode Topology => new JsonObject
    {
        ["runtimeImage"] = FactoryTelemetryWorkflowTests.Image,
        ["audienceClientId"] = FactoryTelemetryWorkflowTests.Audience,
        ["validForSeconds"] = 3600,
        ["routes"] = new JsonArray(Route("synthesis", "OpenAI", "gpt", "gpt-4.1", "2025-04-14", true),
            Route("embedding", "OpenAI", "text-embedding", "text-embedding-3-small", "1", true),
            Route("juror-1", "OpenAI", "gpt", "gpt-4.1", "2025-04-14", false),
            Route("juror-2", "DeepSeek", "deepseek", "DeepSeek-V3-0324", "1", false),
            Route("juror-3", "xAI", "grok", "grok-3", "1", false)),
    };
    internal List<string> Writes { get; } = [];
    internal Dictionary<string, JsonNode> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal string Scenario { get; set; } = "";
    internal bool IncludeMaaSChat { get; set; } = true;
    internal int Proofs { get; private set; }
    internal Action? OnProof { get; set; }
    internal Func<CancellationToken, Task>? BeforeProofAsync { get; set; }
    internal Action<JsonNode>? MutateProof { get; set; }
    internal Action<HttpRequestMessage>? OnRequest { get; set; }
    internal Action<string>? AfterWrite { get; set; }

    private JsonNode Route(string name, string format, string family, string model, string version, bool create) => new JsonObject
    {
        ["name"] = name, ["create"] = create, ["accountResourceId"] = Account,
        ["deployment"] = create ? "df-" + Guid.Parse((string)Manifest["identity"]!["factoryId"]!).ToString("N")[..14] + "-" + name : name,
        ["format"] = format, ["family"] = family, ["model"] = model, ["version"] = version,
        ["sku"] = "GlobalStandard", ["capacity"] = 1, ["dimensions"] = name == "embedding" ? 1536 : 0,
        ["tenantId"] = BoundaryPlanFixture.Tenant, ["location"] = "swedencentral", ["endpoint"] = Endpoint,
        ["modelServingOnly"] = true, ["connectivityReview"] = "Existing private DNS and approved private endpoint; no networking changes.",
        ["inferenceRoleDefinitionId"] = Role,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        OnRequest?.Invoke(request);
        token.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        if (uri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal)) return await prior.SendAsync(request, token);
        if (uri.Host == "models.private.azurecontainerapps.io")
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/proof", uri.AbsolutePath);
            var authority = Record("dsf/factory/models");
            Assert.Equal("checking", (string?)authority["stage"]);
            Assert.Equal("Partial", (string?)authority["state"]);
            Proofs++;
            OnProof?.Invoke();
            if (BeforeProofAsync is not null) await BeforeProofAsync(token);
            var config = authority["configuration"]!;
            var operationId = Guid.Parse((string)authority["operationId"]!);
            var identity = Record("dsf/factory/model-bindings/" + operationId + "/model");
            var observations = Topology["routes"]!.AsArray().Select(r => new ModelWorkloadObservation(
                (string)r!["name"]!, (string)authority["approvalFingerprint"]!, ModelCapabilityState.Verified, ModelCapabilityReason.None,
                (string)r["model"]!, Guid.NewGuid().ToString(), new string('e', 64), 120,
                (int)r["dimensions"]!, (string?)r["name"] == "embedding" ? [new[] { "one", "two" }, new[] { "three" }] : [])).ToArray();
            var proof = new ModelCapabilityProof(Guid.Parse((string)config["factoryId"]!), operationId,
                Guid.Parse((string)authority["checkId"]!), (string)authority["approvalFingerprint"]!, (string)identity["principalId"]!,
                new ReservationClock().GetUtcNow(), new ReservationClock().GetUtcNow().AddSeconds((int)config["validForSeconds"]!),
                ModelCapabilityState.Verified, observations);
            var serialized = JsonSerializer.SerializeToNode(proof, FactoryGitHubJson.Options)!;
            MutateProof?.Invoke(serialized);
            return Reply(HttpStatusCode.OK, serialized);
        }
        Assert.Equal("management.azure.com", uri.Host);
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (request.Method == HttpMethod.Get)
        {
            if (Resources.TryGetValue(path, out var resource)) return Reply(HttpStatusCode.OK, resource);
            if (path.EndsWith("/roleassignments", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(Resources.Values
                    .Where(r => ((string?)r["id"])?.Contains("/roleassignments/", StringComparison.Ordinal) == true).Select(r => r.DeepClone()).ToArray()) });
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToNode(new { value = new[] { new
                    { actions = new[] { "Microsoft.Authorization/roleAssignments/write" }, notActions = Array.Empty<string>() } } })!);
            if (path.EndsWith("/denyassignments", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray() });
            if (path.EndsWith("/roledefinitions/" + Role.Split('/').Last(), StringComparison.Ordinal)) return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToNode(new
            {
                id = Role, properties = new { type = "CustomRole", permissions = new[] { new
                {
                    actions = new[] { "Microsoft.CognitiveServices/accounts/read", "Microsoft.CognitiveServices/accounts/deployments/read",
                        "Microsoft.Authorization/roleAssignments/read", "Microsoft.Authorization/roleDefinitions/read" },
                    notActions = Array.Empty<string>(), notDataActions = Array.Empty<string>(),
                    dataActions = new[] { "Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action",
                        "Microsoft.CognitiveServices/accounts/OpenAI/deployments/embeddings/action" }
                        .Concat(IncludeMaaSChat ? ["Microsoft.CognitiveServices/accounts/MaaS/chat/completions/action"] : Array.Empty<string>()).ToArray(),
                } } },
            })!);
            if (path.EndsWith("/roledefinitions/516239f1-63e1-4d78-a4de-a74fb236a071", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToNode(new
                {
                    id = "/subscriptions/" + (string)Manifest["identity"]!["subscriptionId"]!
                        + "/providers/microsoft.authorization/roledefinitions/516239f1-63e1-4d78-a4de-a74fb236a071",
                    properties = new { type = "BuiltInRole", permissions = new[] { new
                    {
                        actions = Array.Empty<string>(), notActions = Array.Empty<string>(), notDataActions = Array.Empty<string>(),
                        dataActions = new[] { "Microsoft.AppConfiguration/configurationStores/keyValues/read",
                            "Microsoft.AppConfiguration/configurationStores/label/read" },
                    } } },
                })!);
            if (path.EndsWith("/operations", StringComparison.Ordinal) && Resources.ContainsKey(path[..^11]))
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(Resources.Values
                    .Where(r => ((string?)r["id"])?.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal) == true)
                    .Select((r, i) => JsonSerializer.SerializeToNode(new { operationId = i.ToString(), properties = new
                    { targetResource = new { id = (string)r["id"]! }, provisioningOperation = "Create",
                        provisioningState = "Succeeded", statusCode = "Created", trackingId = Guid.NewGuid().ToString() } })).ToArray()) });
            if (path == Account) return Reply(HttpStatusCode.OK, new JsonObject
            {
                ["id"] = Account, ["kind"] = "AIServices", ["location"] = "swedencentral", ["systemData"] = SystemData(),
                ["properties"] = new JsonObject { ["endpoint"] = Scenario == "native-endpoint-set" ? "https://shared-models.cognitiveservices.azure.com/" : Endpoint + "/",
                    ["endpoints"] = new JsonObject { ["Azure AI Model Inference API"] = Endpoint + "/models" }, ["publicNetworkAccess"] = "Disabled",
                    ["customSubDomainName"] = "shared-models", ["provisioningState"] = "Succeeded",
                    ["privateEndpointConnections"] = new JsonArray(), ["networkAcls"] = new JsonObject { ["defaultAction"] = "Deny" } },
            });
            if (path.StartsWith(Account + "/deployments/", StringComparison.Ordinal))
            {
                var route = Topology["routes"]!.AsArray().Single(r => ((string)r!["deployment"]!).ToLowerInvariant() == path.Split('/').Last())!;
                if ((bool)route["create"]! || Scenario == "missing-juror" && (string?)route["name"] == "juror-2")
                    return Reply(HttpStatusCode.NotFound, new JsonObject());
                return Reply(HttpStatusCode.OK, Deployment(route));
            }
            var response = await prior.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.OK && path.Contains("/managedenvironments/", StringComparison.Ordinal))
            {
                var environment = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                environment["properties"]!["vnetConfiguration"] = new JsonObject { ["internal"] = true };
                response.Dispose();
                return Reply(HttpStatusCode.OK, environment);
            }
            return response;
        }
        Writes.Add(path);
        Assert.Equal(HttpMethod.Put, request.Method);
        if (Scenario == "denied-grant" && path.Contains("/roleassignments/", StringComparison.Ordinal))
            return Reply(HttpStatusCode.Forbidden, new JsonObject { ["error"] = "unrelated-secret" });
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        if (path.StartsWith(Account + "/deployments/", StringComparison.Ordinal))
        {
            var route = Topology["routes"]!.AsArray().Single(r => (string?)r!["deployment"] == path.Split('/').Last())!;
            Assert.True((bool)route["create"]!);
            Assert.Equal((string?)route["model"], (string?)body["properties"]!["model"]!["name"]);
            Assert.False(Resources.ContainsKey(path));
            Resources[path] = Deployment(route);
        }
        else if (path.Contains("/roleassignments/", StringComparison.Ordinal))
        {
            body["id"] = path;
            body["properties"]!["scope"] = path[..path.IndexOf("/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal)];
            body["properties"]!["createdOn"] = new ReservationClock().GetUtcNow();
            body["properties"]!["createdBy"] = BoundaryPlanFixture.Principal;
            body["properties"]!["condition"] = null;
            Resources[path] = body;
        }
        else
        {
            Assert.Contains("/microsoft.resources/deployments/", path);
            var authority = Record("dsf/factory/models");
            var bindings = new[] { "model", "code", "hosting" }.Select(n => Record("dsf/factory/model-bindings/"
                + (string)authority["operationId"]! + "/" + n)).ToArray();
            var config = authority["configuration"]!;
            var prefix = "df-" + Guid.Parse((string)Manifest["identity"]!["factoryId"]!).ToString("N")[..14];
            var host = path[..path.IndexOf("/providers/", StringComparison.Ordinal)] + "/providers/microsoft.app/containerapps/" + prefix + "-models";
            Resources[host] = JsonSerializer.SerializeToNode(new
            {
                id = host, systemData = SystemData(),
                identity = new { type = "UserAssigned", userAssignedIdentities = new Dictionary<string, object> { [(string)bindings[0]["target"]!] = new { } } },
                properties = new
                {
                    managedEnvironmentId = (string)bindings[2]["target"]!,
                    configuration = new { activeRevisionsMode = "Single", ingress = new
                        { external = true, allowInsecure = false, targetPort = 8080, fqdn = "models.private.azurecontainerapps.io" } },
                    template = new { containers = new[] { new { name = "models", image = (string)config["runtimeImage"]!,
                        command = new[] { "dotnet", "dsf-runtime.dll" }, args = new[] { "serve-models" }, env = new[]
                        {
                            new { name = "DSF_PRODUCT", value = (string)Manifest["identity"]!["productKey"]! },
                            new { name = "AZURE_APPCONFIG_ENDPOINT", value = (string)config["productEndpoint"]! },
                            new { name = "DSF_FACTORY_PROFILE_PIN", value = (string)config["profilePin"]! },
                            new { name = "AZURE_CLIENT_ID", value = (string)bindings[0]["clientId"]! },
                        } } } },
                },
            })!;
            Resources[host + "/authconfigs/current"] = JsonSerializer.SerializeToNode(new
            {
                id = host + "/authconfigs/current", systemData = SystemData(), properties = new
                {
                    platform = new { enabled = true }, globalValidation = new { unauthenticatedClientAction = "Return401", excludedPaths = Array.Empty<string>() },
                    httpSettings = new { requireHttps = true }, login = new { tokenStore = new { enabled = false } },
                    identityProviders = new { azureActiveDirectory = new { enabled = true,
                        registration = new { clientId = (string)config["audienceClientId"]!, openIdIssuer = "https://login.microsoftonline.com/" + BoundaryPlanFixture.Tenant + "/v2.0" },
                        validation = new { allowedAudiences = new[] { (string)config["audienceClientId"]!, "api://" + (string)config["audienceClientId"]! },
                            defaultAuthorizationPolicy = new { allowedPrincipals = new { identities = new[]
                                { BoundaryPlanFixture.Principal, (string)bindings[1]["principalId"]!, (string)bindings[0]["principalId"]! } } } } } },
                },
            })!;
            Resources[path] = JsonSerializer.SerializeToNode(new { id = path, properties = new
                { provisioningState = "Succeeded", correlationId = Guid.NewGuid().ToString() } })!;
        }
        AfterWrite?.Invoke(path);
        if (Scenario == "lost-create-response") throw new HttpRequestException("unrelated-secret");
        return Reply(HttpStatusCode.Created, Resources[path]);
    }

    internal static JsonNode Deployment(JsonNode route) => new JsonObject
    {
        ["id"] = Account + "/deployments/" + (string)route["deployment"]!, ["etag"] = "\"deployment-1\"",
        ["systemData"] = SystemData(), ["sku"] = new JsonObject { ["name"] = (string)route["sku"]!, ["capacity"] = (int)route["capacity"]! },
        ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded", ["versionUpgradeOption"] = "NoAutoUpgrade",
            ["model"] = new JsonObject { ["format"] = (string)route["format"]!, ["name"] = (string)route["model"]!, ["version"] = (string)route["version"]! } },
    };
    internal static JsonObject SystemData() => new()
    {
        ["createdAt"] = new ReservationClock().GetUtcNow(), ["lastModifiedAt"] = new ReservationClock().GetUtcNow(),
        ["createdBy"] = BoundaryPlanFixture.Principal,
    };
    internal static HttpResponseMessage Reply(HttpStatusCode status, JsonNode body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-correlation-request-id", Guid.NewGuid().ToString());
        response.Headers.Date = new ReservationClock().GetUtcNow();
        return response;
    }
}
