using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed partial class PreviewPreparationTransport
{
    internal readonly List<string> NativeWrites = [];
    internal readonly Dictionary<string, JsonNode[]> DeploymentOperations = new(StringComparer.OrdinalIgnoreCase);
    internal string? NativeFailure { get; set; }
    internal Action<string>? AfterNativeWrite { get; set; }

    private Task<HttpResponseMessage> CreatePreviewAsync(HttpRequestMessage request, JsonNode body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = request.RequestUri!.AbsolutePath.ToLowerInvariant();
        var serialized = Values().Where(p => p.Key.Contains("/preview-preparation/", StringComparison.Ordinal)
            && p.Key.Contains("/effects/", StringComparison.Ordinal) && !p.Key.Contains("/receipts/", StringComparison.Ordinal))
            .Select(p => JsonNode.Parse(p.Value)!).Single(e => (string?)e["target"] == target);
        Assert.Equal("Dispatched", (string?)serialized["state"]);
        Assert.Equal((string?)serialized["requestId"], request.Headers.GetValues("x-ms-client-request-id").Single());
        Assert.Equal((string?)serialized["bodySha256"], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(body.ToJsonString(FactoryGitHubJson.Options)))));
        Assert.False(Resources.ContainsKey(target), "Native create cannot adopt or redeploy an existing target.");
        if (NativeFailure == "denied") return Task.FromResult(NativeReply(HttpStatusCode.Forbidden, new JsonObject { ["error"] = "private-provider-detail" }));
        NativeWrites.Add(target);
        if (target.Contains("/roleassignments/", StringComparison.Ordinal))
        {
            var native = body.DeepClone();
            native["id"] = target;
            native["properties"]!["scope"] = target[..target.IndexOf("/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal)];
            native["properties"]!["createdOn"] = scenario.Source.Source!.Clock.Now;
            native["properties"]!["updatedOn"] = scenario.Source.Source.Clock.Now;
            native["properties"]!["createdBy"] = Manifest.Identity.AzureActorId;
            Resources.Add(target, native);
        }
        else
        {
            var values = body["properties"]!["parameters"]!;
            string Value(string key) => (string)values[key]!["value"]!;
            var prefix = Value("namePrefix");
            var group = target[..target.IndexOf("/providers/", StringComparison.Ordinal)];
            var account = group + "/providers/microsoft.documentdb/databaseaccounts/" + prefix + "-cosmos";
            var product = Value("productKey");
            // Routes/bindings are separately stored; these identities are native resources from the real predecessor deployments.
            var council = scenario.Arm.CreatedResources[group + "/providers/microsoft.managedidentity/userassignedidentities/" + prefix + "-council"];
            var code = scenario.Arm.CreatedResources[group + "/providers/microsoft.managedidentity/userassignedidentities/" + prefix + "-code"];
            var telemetryIdentity = Authority.Configuration.SourceIdentity;
            var councilPrincipal = (string)council["properties"]!["principalId"]!;
            var councilClient = (string)council["properties"]!["clientId"]!;
            var role = account + "/sqlroledefinitions/" + Value("stateRoleId");
            var created = new List<string>();
            Add(account + "/sqldatabases/" + product + "/containers/council-preview", new JsonObject
            {
                ["resource"] = new JsonObject { ["id"] = "council-preview", ["_rid"] = "PD5DALigD00=",
                    ["partitionKey"] = new JsonObject { ["kind"] = "Hash", ["version"] = 2, ["paths"] = new JsonArray("/product") } },
            });
            Add(role, new JsonObject { ["roleName"] = "dsf-preview-state", ["type"] = "CustomRole",
                ["assignableScopes"] = new JsonArray(account), ["permissions"] = new JsonArray(new JsonObject
                { ["dataActions"] = JsonSerializer.SerializeToNode(FactoryPreviewValidation.StateDataActions) }) });
            foreach (var (parameter, name) in new[] { ("runsAssignmentId", "runs"), ("previewAssignmentId", "council-preview") })
                Add(account + "/sqlroleassignments/" + Value(parameter), new JsonObject
                { ["principalId"] = councilPrincipal, ["roleDefinitionId"] = role, ["scope"] = account + "/dbs/" + product + "/colls/" + name });
            var endpoint = "https://" + prefix + "-preview.private.azurecontainerapps.io";
            var optionalSources = values["supplementalSources"]!["value"]!.AsArray();
            JsonNode OptionalIdentity(JsonNode selected) => supplemental!.Resources.Values.Single(r =>
                ((string?)r["id"])?.Contains("/userassignedidentities/", StringComparison.Ordinal) == true
                && ((string)r["id"]!).EndsWith("/" + prefix + "-" + (string)selected["kind"]!, StringComparison.Ordinal));
            foreach (var (suffix, verb, nativeIdentity, client, image, audience, callers) in new[]
            {
                ("preview", "serve-preview", (string)council["id"]!, councilClient, Value("runtimeImage"), Value("audienceClientId"),
                    new[] { Value("operatorPrincipalId") }.Concat(optionalSources.Select(s => (string)OptionalIdentity(s!)["properties"]!["principalId"]!)).ToArray()),
                ("context", "serve-source-context", (string)code["id"]!, (string)code["properties"]!["clientId"]!, Value("runtimeImage"), Value("audienceClientId"), new[] { councilPrincipal }),
                ("preview-models", "serve-preview-models", (string)council["id"]!, councilClient, Value("modelRuntimeImage"), Value("modelAudienceClientId"), new[] { councilPrincipal }),
                ("preview-telemetry", "serve-preview-telemetry", telemetryIdentity.Target, telemetryIdentity.ClientId!, Value("telemetryRuntimeImage"), Value("telemetryAudienceClientId"), new[] { councilPrincipal }),
            }.Concat(optionalSources.Select(s => ("preview-" + (string)s!["kind"]!, "serve-preview-supplemental",
                (string)OptionalIdentity(s!)["id"]!, (string)OptionalIdentity(s!)["properties"]!["clientId"]!,
                (string)s!["runtimeImage"]!, (string)s!["audienceClientId"]!, new[] { councilPrincipal }))))
            {
                var host = group + "/providers/microsoft.app/containerapps/" + prefix + "-" + suffix;
                var env = new JsonArray();
                foreach (var pair in new[] { ("DSF_PRODUCT", product), ("AZURE_APPCONFIG_ENDPOINT", Value("productEndpoint")),
                    ("DSF_FACTORY_PROFILE_PIN", Value("profilePin")), ("AZURE_CLIENT_ID", client) })
                    env.Add(new JsonObject { ["name"] = pair.Item1, ["value"] = pair.Item2 });
                var selected = optionalSources.SingleOrDefault(s => suffix == "preview-" + (string)s!["kind"]!);
                if (selected is not null)
                {
                    env.Add(new JsonObject { ["name"] = "DSF_SUPPLEMENTAL_CONFIG", ["value"] = (string)selected["workloadConfiguration"]! });
                    env.Add(new JsonObject { ["name"] = "DSF_PREVIEW_COORDINATOR", ["value"] = JsonSerializer.Serialize(
                        new PreviewSupplementalCoordinator(endpoint, Value("audienceClientId"), councilPrincipal, councilClient), FactoryGitHubJson.Options) });
                }
                Add(host, new JsonObject
                {
                    ["provisioningState"] = "Succeeded", ["managedEnvironmentId"] = group + "/providers/microsoft.app/managedenvironments/" + prefix + "-hosting",
                    ["configuration"] = new JsonObject { ["activeRevisionsMode"] = "Single", ["ingress"] = new JsonObject
                        { ["external"] = true, ["allowInsecure"] = false, ["targetPort"] = 8080, ["fqdn"] = prefix + "-" + suffix + ".private.azurecontainerapps.io" } },
                    ["template"] = new JsonObject { ["containers"] = new JsonArray(new JsonObject
                        { ["name"] = suffix, ["image"] = image, ["command"] = new JsonArray("dotnet", "dsf-runtime.dll"), ["args"] = new JsonArray(verb),
                            ["env"] = env, ["resources"] = new JsonObject { ["cpu"] = 0.25, ["memory"] = "0.5Gi" } }),
                        ["scale"] = new JsonObject { ["minReplicas"] = 1, ["maxReplicas"] = 1 } },
                });
                Resources[host]["identity"] = new JsonObject { ["type"] = "UserAssigned", ["userAssignedIdentities"] = new JsonObject { [nativeIdentity] = new JsonObject() } };
                var authorization = new JsonObject { ["allowedPrincipals"] = new JsonObject { ["identities"] = JsonSerializer.SerializeToNode(callers) } };
                if (suffix != "preview") authorization["allowedApplications"] = new JsonArray(councilClient);
                Add(host + "/authconfigs/current", new JsonObject
                {
                    ["platform"] = new JsonObject { ["enabled"] = true },
                    ["globalValidation"] = new JsonObject { ["unauthenticatedClientAction"] = "Return401", ["excludedPaths"] = new JsonArray() },
                    ["httpSettings"] = new JsonObject { ["requireHttps"] = true },
                    ["login"] = new JsonObject { ["tokenStore"] = new JsonObject { ["enabled"] = false } },
                    ["identityProviders"] = new JsonObject { ["azureActiveDirectory"] = new JsonObject { ["enabled"] = true,
                        ["registration"] = new JsonObject { ["clientId"] = audience, ["openIdIssuer"] = "https://login.microsoftonline.com/" + Value("tenantId") + "/v2.0" },
                        ["validation"] = new JsonObject { ["allowedAudiences"] = new JsonArray(audience, "api://" + audience), ["defaultAuthorizationPolicy"] = authorization } } },
                });
            }
            var correlation = Guid.NewGuid().ToString();
            Resources.Add(target, new JsonObject { ["id"] = target, ["properties"] = new JsonObject
                { ["provisioningState"] = NativeFailure == "partial" ? "Failed" : "Succeeded", ["correlationId"] = correlation,
                    ["outputs"] = new JsonObject { ["councilEndpoint"] = new JsonObject { ["type"] = "String", ["value"] = endpoint } } } });
            DeploymentOperations.Add(target, created.Select((resource, i) => (JsonNode)new JsonObject
            {
                ["id"] = target + "/operations/" + i.ToString("x"), ["operationId"] = i.ToString("x"),
                ["properties"] = new JsonObject { ["provisioningOperation"] = "Create", ["provisioningState"] = "Succeeded",
                    ["targetResource"] = new JsonObject { ["id"] = resource }, ["statusCode"] = "Created", ["trackingId"] = Guid.NewGuid().ToString() },
            }).ToArray());
            void Add(string resource, JsonObject properties)
            {
                var native = new JsonObject { ["id"] = resource, ["properties"] = properties };
                if (resource.Contains("/microsoft.app/", StringComparison.Ordinal))
                    native["systemData"] = new JsonObject { ["createdAt"] = scenario.Source.Source!.Clock.Now, ["lastModifiedAt"] = scenario.Source.Source.Clock.Now };
                Resources.Add(resource, native);
                created.Add(resource);
            }
        }
        AfterNativeWrite?.Invoke(target);
        if (NativeFailure == "lost") throw new HttpRequestException("private-lost-provider-response");
        if (NativeFailure == "canceled") throw new OperationCanceledException(token);
        return Task.FromResult(NativeReply(HttpStatusCode.Created, Resources[target]));
    }

    private static HttpResponseMessage NativeReply(HttpStatusCode status, JsonNode value)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-correlation-request-id", Guid.NewGuid().ToString());
        return response;
    }
}
