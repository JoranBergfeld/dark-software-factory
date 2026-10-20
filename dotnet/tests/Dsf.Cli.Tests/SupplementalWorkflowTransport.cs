using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed class SupplementalWorkflowTransport(FactoryFoundationTests.FoundationTransport foundation) : HttpMessageHandler
{
    public static readonly string Search = BoundaryPlanFixture.Root.ToLowerInvariant() + "/resourcegroups/knowledge/providers/microsoft.search/searchservices/product-search";
    private readonly HttpMessageInvoker prior = new(foundation, false);
    public Dictionary<string, JsonNode> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = [];
    public List<JsonNode> Grants { get; } = [];
    public int Proofs { get; private set; }
    public Action? OnProof { get; set; }
    public Func<Task>? BeforeProofAsync { get; set; }
    public bool LoseIdentityResponse { get; init; }
    public bool BroaderRole { get; set; }
    public JsonNode? ExternalAssignment { get; set; }
    public Action<JsonNode>? ChangeProof { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HostedProof { get; set; }
    public JsonNode Record(string key) => JsonNode.Parse((string)JsonNode.Parse(foundation.Product.ListJson())!.AsArray()
        .Single(r => (string?)r!["key"] == key)!["value"]!)!;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (uri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal)) return await prior.SendAsync(request, token);
        if (uri.Host.EndsWith(".azurecontainerapps.io", StringComparison.Ordinal))
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/proof", path);
            Proofs++;
            if (BeforeProofAsync is not null) await BeforeProofAsync();
            OnProof?.Invoke();
            if (HostedProof is not null) return await HostedProof(request, token);
            var call = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var config = Record(ProductConfigurationKeys.FactorySupplemental)["configuration"]!;
            var kind = uri.Host.Split('.')[0];
            var identity = Resources.Single(p => p.Key.Contains("/userassignedidentities/", StringComparison.Ordinal) && p.Key.EndsWith("-" + kind, StringComparison.Ordinal)).Value;
            var proof = JsonSerializer.SerializeToNode(new
            {
                factoryId = (string)config["factoryId"]!,
                checkId = (string)call["checkId"]!,
                configurationRevision = (string)call["configurationRevision"]!,
                kind,
                sourcePrincipalId = (string)identity["properties"]!["principalId"]!,
                state = "ReadySupplemental",
                reason = "Empty",
                observedAt = new ReservationClock().GetUtcNow(),
                records = Array.Empty<object>(),
                nativeSourceRevision = new string('e', 64),
                secretVersion = kind == "webiq" ? new string('a', 32) : null,
                coverage = Dsf.Core.Onboarding.FactorySupplementalValidation.Coverage,
            })!;
            ChangeProof?.Invoke(proof);
            return Reply((string?)proof["state"] == "ReadySupplemental" ? HttpStatusCode.OK : HttpStatusCode.Conflict, proof);
        }
        Assert.Equal("management.azure.com", uri.Host);
        if (request.Method == HttpMethod.Get)
        {
            if (path == Search) return Reply(HttpStatusCode.OK, new JsonObject
            {
                ["id"] = Search,
                ["systemData"] = new JsonObject { ["createdAt"] = "2020-01-01T00:00:00Z" },
                ["properties"] = new JsonObject { ["disableLocalAuth"] = true, ["publicNetworkAccess"] = "Disabled" }
            });
            if (Resources.TryGetValue(path, out var resource)) return Reply(HttpStatusCode.OK, resource);
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToNode(new
                {
                    value = new[] { new
                {
                    actions = new[] { "Microsoft.Authorization/roleAssignments/write", "Microsoft.Resources/deployments/write",
                        "Microsoft.ManagedIdentity/userAssignedIdentities/write", "Microsoft.App/containerApps/write", "Microsoft.App/containerApps/authConfigs/write" },
                    notActions = Array.Empty<string>(),
                } }
                })!);
            if (path.EndsWith("/denyassignments", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray() });
            if (path.EndsWith("/roleassignments", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject
                {
                    ["value"] = new JsonArray(Grants.Where(g =>
                    Uri.UnescapeDataString(uri.Query).Contains((string)g["properties"]!["principalId"]!, StringComparison.Ordinal)).Select(g => g.DeepClone())
                    .Concat(ExternalAssignment is null ? [] : new[] { ExternalAssignment.DeepClone() }).ToArray())
                });
            if (path.Contains("/roledefinitions/", StringComparison.Ordinal))
            {
                var metadata = path.EndsWith("77777777-7777-7777-7777-777777777777", StringComparison.Ordinal);
                var search = path.EndsWith(Dsf.Core.Onboarding.FactorySupplementalValidation.SearchDataRole, StringComparison.Ordinal);
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToNode(new
                {
                    id = "/subscriptions/" + BoundaryPlanFixture.Subscription + "/providers/microsoft.authorization/roledefinitions/" + path.Split('/').Last(),
                    properties = new
                    {
                        type = metadata ? "CustomRole" : "BuiltInRole",
                        permissions = new[] { new { actions = metadata ? new[]
                    {
                        "Microsoft.Search/searchServices/read", "Microsoft.Search/searchServices/indexes/read",
                        "Microsoft.Search/searchServices/knowledgebases/read", "Microsoft.Search/searchServices/knowledgesources/read",
                    } : Array.Empty<string>(), notActions = Array.Empty<string>(),
                        dataActions = BroaderRole ? new[] { "*" } : metadata ? Array.Empty<string>() : search ? new[] { "Microsoft.Search/searchServices/indexes/documents/read" }
                            : new[] { "Microsoft.KeyVault/vaults/secrets/getSecret/action", "Microsoft.KeyVault/vaults/secrets/readMetadata/action" },
                        notDataActions = Array.Empty<string>() } },
                    }
                })!);
            }
            if (path.EndsWith("/operations", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject
                {
                    ["value"] = new JsonArray(Resources.Values.Where(v =>
                    ((string?)v["id"])?.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal) == true
                    && ((string)v["id"]!).Contains(path.Contains("-foundryiq/", StringComparison.Ordinal) ? "-foundryiq" : "-webiq", StringComparison.Ordinal)).Select((v, i) => (JsonNode)new JsonObject
                    {
                        ["operationId"] = i.ToString(),
                        ["properties"] = new JsonObject
                        {
                            ["targetResource"] = new JsonObject { ["id"] = (string)v["id"]! },
                            ["provisioningOperation"] = "Create",
                            ["provisioningState"] = "Succeeded",
                            ["statusCode"] = "Created",
                            ["trackingId"] = Guid.NewGuid().ToString()
                        },
                    }).ToArray())
                });
            var response = await prior.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.OK && (path.Contains("/managedenvironments/", StringComparison.Ordinal)
                || path.Contains("/vaults/", StringComparison.Ordinal)))
            {
                var native = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                native["properties"]!["vnetConfiguration"] = new JsonObject { ["internal"] = true };
                native["properties"]!["enableRbacAuthorization"] = true;
                native["properties"]!["publicNetworkAccess"] = "Disabled";
                native["properties"]!["accessPolicies"] = new JsonArray();
                response.Dispose();
                return Reply(HttpStatusCode.OK, native);
            }
            return response;
        }
        Assert.Equal(HttpMethod.Put, request.Method);
        Writes.Add(path);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        if (path.Contains("/userassignedidentities/", StringComparison.Ordinal))
        {
            var native = new JsonObject
            {
                ["id"] = path,
                ["systemData"] = SystemData(),
                ["properties"] = new JsonObject
                {
                    ["tenantId"] = BoundaryPlanFixture.Tenant,
                    ["principalId"] = path.EndsWith("-webiq", StringComparison.Ordinal) ? "88888888-8888-8888-8888-888888888888" : "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    ["clientId"] = path.EndsWith("-webiq", StringComparison.Ordinal) ? "99999999-9999-9999-9999-999999999999" : "cccccccc-cccc-cccc-cccc-cccccccccccc"
                }
            };
            Resources[path] = native;
            if (LoseIdentityResponse) throw new HttpRequestException("secret-provider-detail");
            return Reply(HttpStatusCode.Created, native);
        }
        if (path.Contains("/roleassignments/", StringComparison.Ordinal))
        {
            var native = body.DeepClone();
            native["id"] = path;
            native["properties"]!["scope"] = path[..path.IndexOf("/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal)];
            native["properties"]!["createdBy"] = BoundaryPlanFixture.Principal;
            native["properties"]!["createdOn"] = new ReservationClock().GetUtcNow();
            native["properties"]!["condition"] = null;
            Resources[path] = native;
            Grants.Add(native);
            return Reply(HttpStatusCode.Created, native);
        }
        Assert.Contains("/deployments/", path);
        var parameters = body["properties"]!["parameters"]!;
        string P(string key) => (string)parameters[key]!["value"]!;
        var sourceKind = P("sourceKind");
        var host = path[..path.IndexOf("/providers/", StringComparison.Ordinal)] + "/providers/microsoft.app/containerapps/" + P("namePrefix") + "-" + sourceKind;
        var identityPair = Resources.Single(p => p.Key.Contains("/userassignedidentities/", StringComparison.Ordinal) && p.Key.EndsWith("-" + sourceKind, StringComparison.Ordinal));
        Resources[host] = new JsonObject
        {
            ["id"] = host,
            ["systemData"] = SystemData(),
            ["identity"] = new JsonObject { ["type"] = "UserAssigned", ["userAssignedIdentities"] = new JsonObject { [identityPair.Key] = new JsonObject() } },
            ["properties"] = new JsonObject
            {
                ["managedEnvironmentId"] = host[..host.IndexOf("/providers/", StringComparison.Ordinal)] + "/providers/microsoft.app/managedenvironments/" + P("namePrefix") + "-hosting",
                ["configuration"] = new JsonObject
                {
                    ["activeRevisionsMode"] = "Single",
                    ["ingress"] = new JsonObject { ["external"] = true, ["allowInsecure"] = false, ["targetPort"] = 8080, ["fqdn"] = sourceKind + ".private.azurecontainerapps.io" }
                },
                ["template"] = new JsonObject
                {
                    ["containers"] = new JsonArray(new JsonObject
                    {
                        ["image"] = P("runtimeImage"),
                        ["command"] = new JsonArray("dotnet", "dsf-runtime.dll"),
                        ["args"] = new JsonArray("serve-supplemental"),
                        ["env"] = new JsonArray(Env("DSF_PRODUCT", P("productKey")), Env("AZURE_APPCONFIG_ENDPOINT", P("productEndpoint")),
                        Env("DSF_FACTORY_PROFILE_PIN", P("profilePin")), Env("AZURE_CLIENT_ID", (string)identityPair.Value["properties"]!["clientId"]!),
                        Env("DSF_SUPPLEMENTAL_CONFIG", P("sourceConfiguration"))),
                    })
                },
            },
        };
        Resources[host + "/authconfigs/current"] = new JsonObject
        {
            ["id"] = host + "/authconfigs/current",
            ["systemData"] = SystemData(),
            ["properties"] = new JsonObject
            {
                ["platform"] = new JsonObject { ["enabled"] = true },
                ["globalValidation"] = new JsonObject { ["unauthenticatedClientAction"] = "Return401", ["excludedPaths"] = new JsonArray() },
                ["httpSettings"] = new JsonObject { ["requireHttps"] = true },
                ["login"] = new JsonObject { ["tokenStore"] = new JsonObject { ["enabled"] = false } },
                ["identityProviders"] = new JsonObject
                {
                    ["azureActiveDirectory"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["registration"] = new JsonObject { ["clientId"] = P("audienceClientId"), ["openIdIssuer"] = "https://login.microsoftonline.com/" + P("tenantId") + "/v2.0" },
                        ["validation"] = new JsonObject
                        {
                            ["allowedAudiences"] = new JsonArray(P("audienceClientId"), "api://" + P("audienceClientId")),
                            ["defaultAuthorizationPolicy"] = new JsonObject { ["allowedPrincipals"] = new JsonObject { ["identities"] = new JsonArray(P("operatorPrincipalId")) } }
                        },
                    }
                },
            },
        };
        Resources[path] = new JsonObject { ["id"] = path, ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded", ["correlationId"] = Guid.NewGuid().ToString() } };
        return Reply(HttpStatusCode.Created, Resources[path]);
    }

    private static JsonObject Env(string name, string value) => new() { ["name"] = name, ["value"] = value };
    private static JsonObject SystemData() => new()
    {
        ["createdAt"] = new ReservationClock().GetUtcNow(),
        ["lastModifiedAt"] = new ReservationClock().GetUtcNow(),
        ["createdBy"] = BoundaryPlanFixture.Principal
    };
    private static HttpResponseMessage Reply(HttpStatusCode status, JsonNode value)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-correlation-request-id", Guid.NewGuid().ToString());
        return response;
    }
}
