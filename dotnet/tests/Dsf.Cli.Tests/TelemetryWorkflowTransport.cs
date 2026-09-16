using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed class TelemetryWorkflowTransport(FactoryFoundationTests.FoundationTransport foundation) : HttpMessageHandler
{
    private readonly HttpMessageInvoker prior = new(foundation, false);
    public List<JsonNode> Grants { get; } = [];
    public List<JsonNode> Deployments { get; } = [];
    public Dictionary<string, JsonNode> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Proofs { get; private set; }
    public bool CurrentReaderRole { get; init; }
    public bool BroaderReaderRole { get; set; }
    public JsonNode? ExternalAssignment { get; init; }
    public bool LoseGrantResponse { get; init; }
    public bool DenyGrant { get; init; }
    public bool DenyHost { get; init; }
    public bool InvalidDeploymentOperationId { get; init; }
    public Action? OnProof { get; set; }
    public Action<JsonNode>? ChangeProof { get; init; }
    public JsonNode Manifest => Record(ProductConfigurationKeys.FactoryManifest);
    private JsonNode Record(string key) => JsonNode.Parse((string)JsonNode.Parse(foundation.Product.ListJson())!.AsArray()
        .Single(r => (string?)r!["key"] == key)!["value"]!)!;
    private FactoryTelemetry Authority => JsonSerializer.Deserialize<FactoryTelemetry>(
        Record(ProductConfigurationKeys.FactoryTelemetry).ToJsonString(), FactoryGitHubJson.Options)!;

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
            OnProof?.Invoke();
            var authority = Authority;
            var selected = authority.Configuration;
            var now = new ReservationClock().GetUtcNow();
            var record = new TelemetryRecordReference(BoundaryPlanFixture.Logs.ToLowerInvariant(),
                "44444444-4444-4444-4444-444444444444", "AppRequests", FactoryTelemetryWorkflowTests.Component,
                now.AddHours(-1), "record-123", 1);
            var proof = new TelemetryProof(TelemetryState.Ready, TelemetryReason.None, selected.FactoryId, authority.OperationId,
                authority.ApprovalFingerprint, selected.SourceIdentity.PrincipalId!, selected.ApplicationEnvironment,
                now, now.AddSeconds(-selected.FreshnessSeconds), now, [record], "Real hosted proof")
            { CheckId = authority.CheckId, Queries = [new(record.BackendResourceId, record.Table, TelemetryReason.None,
                FactoryTelemetryValidation.Hash(selected.Queries["AppRequests"]), Guid.NewGuid().ToString(), 1, record)] };
            var payload = JsonSerializer.SerializeToNode(proof, FactoryGitHubJson.Options)!;
            ChangeProof?.Invoke(payload);
            return Reply((string?)payload["state"] == "Ready" ? HttpStatusCode.OK : HttpStatusCode.Conflict, payload);
        }
        Assert.Equal("management.azure.com", uri.Host);
        if (request.Method == HttpMethod.Get)
        {
            if (Resources.TryGetValue(path, out var found)) return Reply(HttpStatusCode.OK, found);
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToNode(new { value = new[] { new
                { actions = new[] { "Microsoft.Authorization/roleAssignments/write", "Microsoft.Resources/deployments/write" },
                    notActions = Array.Empty<string>(), dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } })!);
            if (path.EndsWith("/denyassignments", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray() });
            if (path.EndsWith("/roleassignments", StringComparison.Ordinal))
            {
                var scope = path[..^"/providers/microsoft.authorization/roleassignments".Length];
                var matching = Grants.Where(g => ((string)g["properties"]!["scope"]!).StartsWith(scope, StringComparison.OrdinalIgnoreCase));
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(matching.Select(g => g.DeepClone())
                    .Concat(ExternalAssignment is null ? Array.Empty<JsonNode>() : new[] { ExternalAssignment.DeepClone() }).ToArray()) });
            }
            if (path.Contains("/roledefinitions/", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject { ["id"] = path, ["properties"] = new JsonObject
                {
                    ["type"] = "BuiltInRole", ["permissions"] = JsonSerializer.SerializeToNode(new[] { new
                    {
                        actions = path.EndsWith("73c42c96-874c-492b-b04d-ab87d138a893", StringComparison.Ordinal)
                            ? new[] { "*/read", "Microsoft.OperationalInsights/workspaces/analytics/query/action", "Microsoft.OperationalInsights/workspaces/search/action" }
                            : Array.Empty<string>(),
                        notActions = new[] { "Microsoft.OperationalInsights/workspaces/sharedKeys/read" },
                        dataActions = path.EndsWith("516239f1-63e1-4d78-a4de-a74fb236a071", StringComparison.Ordinal)
                            ? new[] { "Microsoft.AppConfiguration/configurationStores/keyValues/read" }
                            : BroaderReaderRole ? new[] { "*" } : CurrentReaderRole ? new[] { "Microsoft.OperationalInsights/workspaces/jobs/export/action" } : Array.Empty<string>(),
                        notDataActions = Array.Empty<string>(),
                    } }),
                } });
            if (path.EndsWith("/microsoft.insights/components", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(Component()) });
            if (path == FactoryTelemetryWorkflowTests.Component) return Reply(HttpStatusCode.OK, Component());
            if (path == BoundaryPlanFixture.Logs.ToLowerInvariant())
                return Reply(HttpStatusCode.OK, new JsonObject { ["id"] = path,
                    ["type"] = "Microsoft.OperationalInsights/workspaces", ["location"] = "swedencentral",
                    ["properties"] = new JsonObject { ["customerId"] = "44444444-4444-4444-4444-444444444444",
                        ["features"] = new JsonObject { ["enableLogAccessUsingOnlyResourcePermissions"] = false } } });
            if (path == BoundaryPlanFixture.Logs.ToLowerInvariant() + "/tables")
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(new JsonObject
                    { ["name"] = "AppRequests", ["properties"] = new JsonObject { ["plan"] = "Analytics" } }) });
            if (path.EndsWith("/operations", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(Resources.Values.Where(v =>
                    ((string?)v["id"])?.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal) == true).Select((v, i) => (JsonNode)new JsonObject
                    { ["operationId"] = InvalidDeploymentOperationId ? "secret-provider-detail" : i.ToString(), ["properties"] = new JsonObject { ["targetResource"] = new JsonObject { ["id"] = (string)v["id"]! },
                        ["provisioningOperation"] = "Create", ["provisioningState"] = "Succeeded", ["statusCode"] = "Created",
                        ["trackingId"] = Guid.NewGuid().ToString() } }).ToArray()) });
            var priorResponse = await prior.SendAsync(request, token);
            if (priorResponse.StatusCode == HttpStatusCode.OK && path.Contains("/managedenvironments/", StringComparison.Ordinal))
            {
                var environment = JsonNode.Parse(await priorResponse.Content.ReadAsStringAsync(token))!;
                environment["properties"]!["vnetConfiguration"] = new JsonObject { ["internal"] = true };
                priorResponse.Dispose();
                return Reply(HttpStatusCode.OK, environment);
            }
            return priorResponse;
        }
        Assert.Equal(HttpMethod.Put, request.Method);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        if (path.Contains("/roleassignments/", StringComparison.Ordinal))
        {
            if (DenyGrant) return Reply(HttpStatusCode.Forbidden, new JsonObject { ["error"] = "secret-provider-detail" });
            var grant = body.DeepClone();
            grant["id"] = path;
            var props = grant["properties"]!;
            props["scope"] = path[..path.IndexOf("/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal)];
            props["createdOn"] = new ReservationClock().GetUtcNow();
            props["createdBy"] = BoundaryPlanFixture.Principal;
            props["condition"] = null;
            Grants.Add(grant);
            Resources[path] = grant;
            if (LoseGrantResponse) throw new HttpRequestException("secret-provider-detail");
            return Reply(HttpStatusCode.Created, grant);
        }
        Assert.Contains("/deployments/", path);
        Deployments.Add(body);
        if (DenyHost) return Reply(HttpStatusCode.Forbidden, new JsonObject { ["error"] = "secret-provider-detail" });
        var config = Authority.Configuration;
        var host = path[..path.IndexOf("/providers/", StringComparison.Ordinal)]
            + "/providers/microsoft.app/containerapps/" + (string)body["properties"]!["parameters"]!["namePrefix"]!["value"]! + "-telemetry";
        Resources[host] = new JsonObject
        {
            ["id"] = host, ["systemData"] = SystemData(),
            ["identity"] = new JsonObject { ["type"] = "UserAssigned", ["userAssignedIdentities"] = new JsonObject { [config.SourceIdentity.Target] = new JsonObject() } },
            ["properties"] = new JsonObject { ["managedEnvironmentId"] = config.HostingEnvironment.Target,
                ["configuration"] = new JsonObject { ["activeRevisionsMode"] = "Single",
                    ["ingress"] = new JsonObject { ["external"] = true, ["allowInsecure"] = false, ["targetPort"] = 8080,
                        ["fqdn"] = "telemetry.private.azurecontainerapps.io" } },
                ["template"] = new JsonObject { ["containers"] = new JsonArray(new JsonObject
                    { ["name"] = "telemetry", ["image"] = config.RuntimeImage, ["command"] = new JsonArray("dotnet", "dsf-runtime.dll"),
                        ["args"] = new JsonArray("serve-telemetry"), ["env"] = new JsonArray(
                            Env("AZURE_CLIENT_ID", config.SourceIdentity.ClientId!), Env("DSF_PRODUCT", (string)Manifest["identity"]!["productKey"]!),
                            Env("AZURE_APPCONFIG_ENDPOINT", config.ProductEndpoint), Env("DSF_FACTORY_PROFILE_PIN", (string)Manifest["profilePin"]!)) }) } },
        };
        var policy = new JsonObject { ["id"] = host + "/authconfigs/current", ["systemData"] = SystemData(), ["properties"] = new JsonObject
        {
            ["platform"] = new JsonObject { ["enabled"] = true },
            ["globalValidation"] = new JsonObject { ["unauthenticatedClientAction"] = "Return401", ["excludedPaths"] = new JsonArray() },
            ["httpSettings"] = new JsonObject { ["requireHttps"] = true }, ["login"] = new JsonObject { ["tokenStore"] = new JsonObject { ["enabled"] = false } },
            ["identityProviders"] = new JsonObject { ["azureActiveDirectory"] = new JsonObject { ["enabled"] = true,
                ["registration"] = new JsonObject { ["clientId"] = config.AudienceClientId,
                    ["openIdIssuer"] = "https://login.microsoftonline.com/" + BoundaryPlanFixture.Tenant + "/v2.0" },
                ["validation"] = new JsonObject { ["allowedAudiences"] = new JsonArray(config.AudienceClientId, "api://" + config.AudienceClientId),
                    ["defaultAuthorizationPolicy"] = new JsonObject { ["allowedPrincipals"] = new JsonObject
                        { ["identities"] = new JsonArray(BoundaryPlanFixture.Principal) } } } } },
        } };
        Resources[host + "/authconfigs/current"] = policy;
        Resources[path] = new JsonObject { ["id"] = path, ["properties"] = new JsonObject
            { ["correlationId"] = Guid.NewGuid().ToString(), ["provisioningState"] = "Succeeded" } };
        return Reply(HttpStatusCode.Created, Resources[path]);
    }

    private static JsonNode Component() => new JsonObject { ["id"] = FactoryTelemetryWorkflowTests.Component,
        ["type"] = "Microsoft.Insights/components", ["location"] = "swedencentral", ["tags"] = new JsonObject { ["environment"] = "production" },
        ["properties"] = new JsonObject { ["WorkspaceResourceId"] = BoundaryPlanFixture.Logs.ToLowerInvariant() } };
    private static JsonObject Env(string name, string value) => new() { ["name"] = name, ["value"] = value };
    private static JsonObject SystemData() => new() { ["createdAt"] = new ReservationClock().GetUtcNow(), ["lastModifiedAt"] = new ReservationClock().GetUtcNow() };
    private static HttpResponseMessage Reply(HttpStatusCode status, JsonNode value)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-correlation-request-id", Guid.NewGuid().ToString());
        response.Headers.Date = new ReservationClock().GetUtcNow();
        return response;
    }
}
