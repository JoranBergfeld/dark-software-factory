using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

internal sealed partial class PreviewContextTransport
{
    internal const string TelemetryEndpoint = "https://preview-telemetry.private.azurecontainerapps.io";
    internal FactoryTelemetry? Telemetry;
    internal PreviewTelemetrySelection? TelemetrySelection;
    internal List<JsonElement> TelemetryQueries { get; } = [];
    internal Func<JsonNode, JsonNode>? ChangeTelemetryQueryResponse;
    internal Func<string, string>? ChangeTelemetryResponse;
    internal Func<HttpRequestMessage, HttpResponseMessage?>? TelemetryNativeResponse;
    internal Action? OnTelemetryQuery;
    internal string? TelemetryTokenIdentity;
    internal string? TelemetryTokenTenant;

    private async Task PrepareTelemetryAsync(bool mixedTables, bool multipleBackends, string? selectedTable)
    {
        using var native = new FactoryTelemetryHostTests.TelemetryTransport(mixedTables: mixedTables, multipleBackends: multipleBackends, table: selectedTable);
        using var http = new HttpClient(native, false);
        var runner = new FactoryTelemetryHostTests.TelemetryRunner(native);
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], native.Environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, TelemetryHttp = http,
                TelemetryCredential = new FactoryTelemetryHostTests.TelemetryCredential(native), TelemetryClock = Source.Clock }, CancellationToken.None));
        Assert.True(runner.Response == HttpStatusCode.OK, runner.Body);
        var proof = JsonSerializer.Deserialize<TelemetryProof>(runner.Body, FactorySourceValidation.RecordJson)!;
        var key = ProductConfigurationKeys.TelemetryProof(native.Authority.OperationId, proof.CheckId!.Value);
        var hostEndpoint = "https://telemetry.private.azurecontainerapps.io";
        Telemetry = native.Authority with { State = TelemetryState.Ready, Stage = "observed", Proof = proof, HostEndpoint = hostEndpoint,
            LastCheckReference = key, LastCheckSha256 = FactoryTelemetryValidation.Hash(proof),
            VerifiedProofReference = key, VerifiedProofSha256 = FactoryTelemetryValidation.Hash(proof) };
        foreach (var pair in native.Values.Where(p => p.Key.Contains("/telemetry", StringComparison.Ordinal))) Source.Values[pair.Key] = pair.Value;
        Source.Values[key] = JsonSerializer.Serialize(proof, FactoryGitHubJson.Options);
        Source.Values[ProductConfigurationKeys.FactoryTelemetry] = JsonSerializer.Serialize(Telemetry with { Proof = null }, FactoryGitHubJson.Options);
        var currentManifest = JsonSerializer.Deserialize<FactoryManifest>(Source.Values[ProductConfigurationKeys.FactoryManifest], FactorySourceValidation.RecordJson)!;
        Source.Manifest = currentManifest with { TelemetryOperationId = Telemetry.OperationId, TelemetryApprovalFingerprint = Telemetry.ApprovalFingerprint };
        Source.Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(Source.Manifest, FactoryGitHubJson.Options);
        Models = FactoryModelsValidation.ReadPreparation(Source.Manifest, Source.Values);
        var config = Telemetry.Configuration;
        var effects = FactoryTelemetryValidation.ReadCompletedEffects(Telemetry, Source.Values);
        var identity = config.SourceIdentity;
        Native[identity.Target] = JsonSerializer.SerializeToNode(new { id = identity.Target,
            systemData = new { createdAt = identity.Incarnation, lastModifiedAt = identity.Incarnation },
            properties = new { principalId = identity.PrincipalId, clientId = identity.ClientId, tenantId = Source.Manifest.Identity.TenantId } })!;
        foreach (var backend in config.Backends)
        {
            Native[backend.ResourceId] = JsonSerializer.SerializeToNode(new { id = backend.ResourceId, properties = new
                { customerId = backend.WorkspaceId, features = new { enableLogAccessUsingOnlyResourcePermissions = false, dataAuthorizationMode = false } } })!;
            foreach (var table in backend.Tables)
            {
                var target = backend.ResourceId + "/tables/" + table;
                Native[target] = JsonSerializer.SerializeToNode(new { id = target, properties = new { plan = "Analytics", protectionLevel = "General" } })!;
            }
            foreach (var app in backend.ApplicationResourceIds)
                Native[app] = JsonSerializer.SerializeToNode(new { id = app, properties = new { WorkspaceResourceId = backend.ResourceId } })!;
        }
        var revisions = new Dictionary<string, string>();
        foreach (var grant in config.AccessGrants)
        {
            var effect = effects.Single(e => e.Target == grant.Target);
            Native[grant.Target] = JsonSerializer.SerializeToNode(new { id = grant.Target, properties = new { scope = grant.Scope,
                roleDefinitionId = grant.RoleDefinitionId, principalId = grant.PrincipalId, principalType = "ServicePrincipal",
                createdOn = effect.Incarnation, updatedOn = effect.Incarnation, createdBy = Source.Manifest.Identity.AzureActorId,
                condition = (string?)null, conditionVersion = (string?)null } })!;
            JsonNode role;
            if (grant.RoleDefinitionId.EndsWith("/516239f1-63e1-4d78-a4de-a74fb236a071", StringComparison.Ordinal))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + grant.Scope
                    + "/providers/microsoft.authorization/roledefinitions/516239f1-63e1-4d78-a4de-a74fb236a071?api-version=2022-04-01");
                request.Headers.Authorization = new("Bearer", new IntentModelCredential(Source).GetToken(
                    new TokenRequestContext(["https://management.azure.com/.default"]), CancellationToken.None).Token);
                using var response = await inner.SendAsync(request, CancellationToken.None);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                role = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            }
            else
                role = JsonSerializer.SerializeToNode(new { id = grant.RoleDefinitionId, properties = new { roleName = "Monitoring Data Reader",
                    type = "BuiltInRole", assignableScopes = new[] { "/" }, permissions = new[] { new {
                        actions = new[] { "*/read", "Microsoft.OperationalInsights/workspaces/analytics/query/action",
                            "Microsoft.OperationalInsights/workspaces/search/action", "Microsoft.Support/*" },
                        notActions = new[] { "Microsoft.OperationalInsights/workspaces/sharedKeys/read" },
                        dataActions = new[] { "Microsoft.OperationalInsights/workspaces/jobs/export/action" }, notDataActions = Array.Empty<string>() } } } })!;
            Native[grant.RoleDefinitionId] = role;
            revisions[grant.RoleDefinitionId] = FactoryModelsValidation.Hash(JsonSerializer.SerializeToElement(role["properties"]));
        }
        var oldTarget = FactoryTelemetryValidation.HostTarget(config);
        var oldHost = Source.Resources[Source.HostTarget].DeepClone();
        oldHost["id"] = oldTarget;
        oldHost["systemData"] = JsonSerializer.SerializeToNode(new { createdAt = effects[^2].Incarnation, lastModifiedAt = effects[^2].Incarnation });
        oldHost["identity"]!["userAssignedIdentities"] = new JsonObject { [identity.Target] = new JsonObject() };
        var container = oldHost["properties"]!["template"]!["containers"]![0]!;
        container["image"] = config.RuntimeImage;
        container["args"] = new JsonArray("serve-telemetry");
        foreach (var value in container["env"]!.AsArray())
            if ((string?)value!["name"] == "AZURE_CLIENT_ID") value["value"] = identity.ClientId;
        oldHost["properties"]!["configuration"]!["ingress"]!["fqdn"] = new Uri(hostEndpoint).Host;
        Native[oldTarget] = oldHost;
        var oldAuth = Source.Resources[Source.HostTarget + "/authconfigs/current"].DeepClone();
        oldAuth["id"] = oldTarget + "/authconfigs/current";
        oldAuth["systemData"] = oldHost["systemData"]!.DeepClone();
        var aad = oldAuth["properties"]!["identityProviders"]!["azureActiveDirectory"]!;
        aad["registration"]!["clientId"] = config.AudienceClientId;
        aad["validation"]!["allowedAudiences"] = new JsonArray(config.AudienceClientId, "api://" + config.AudienceClientId);
        aad["validation"]!["defaultAuthorizationPolicy"]!["allowedPrincipals"]!["identities"] = new JsonArray(Source.Manifest.Identity.AzureActorId);
        Native[oldTarget + "/authconfigs/current"] = oldAuth;
        var metadataRole = "/subscriptions/" + Source.Manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd";
        var metadata = JsonSerializer.SerializeToNode(new { id = metadataRole, properties = new { type = "CustomRole", roleName = "DSF telemetry preview metadata",
            assignableScopes = new[] { "/subscriptions/" + Source.Manifest.Identity.SubscriptionId }, permissions = new[] {
                new { actions = FactoryPreviewTelemetryValidation.MetadataActions, notActions = Array.Empty<string>(),
                    dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } } })!;
        Native[metadataRole] = metadata;
        TelemetrySelection = new(FactoryPreviewTelemetryValidation.Purpose, Telemetry.ApprovalFingerprint, FactoryModelsValidation.Hash(effects),
            identity, config.Backends.SelectMany(b => b.ApplicationResourceIds).Distinct().Order(StringComparer.Ordinal).ToArray(),
            "example.azurecr.io/dsf/runtime@sha256:" + new string('6', 64), "adadadad-adad-adad-adad-adadadadadad",
            FactoryPreviewTelemetryValidation.Projection, 2,
            config.Queries.Keys.ToDictionary(table => table, table => FactoryPreviewTelemetryValidation.Query(table, 2)),
            metadataRole, FactoryModelsValidation.Hash(JsonSerializer.SerializeToElement(metadata["properties"])), revisions);
        EnforceCouncilScopes = true;
    }

    internal async Task<PreviewContextRunner> RunTelemetryAsync(bool coordinator = false, string? caller = null, string? client = null,
        PreviewCapabilityRequest? request = null)
    {
        var writes = Source.Writes.Count;
        var inference = Intent.Models.Inference.Count;
        var runner = new PreviewContextRunner(Source) { Route = coordinator ? "/telemetry-evidence" : "/evidence",
            Request = request ?? Request, Caller = caller ?? (coordinator ? Source.Manifest.Identity.AzureActorId : Models.Bindings![0].PrincipalId!),
            Client = client ?? Models.Bindings![0].ClientId! };
        using var http = new HttpClient(this, false);
        var environment = Source.Environment;
        environment["AZURE_CLIENT_ID"] = coordinator ? Models.Bindings![0].ClientId : Telemetry!.Configuration.SourceIdentity.ClientId;
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync([coordinator ? "serve-preview" : "serve-preview-telemetry"], environment,
            new StringWriter(), new StringWriter(), RuntimeDependencies.Production() with { WebHostRunner = runner,
                PreviewHttp = http, PreviewCredential = new IntentModelCredential(Source), PreviewClock = Source.Clock,
                TelemetryHttp = http, TelemetryCredential = new PreviewTelemetryCredential(this), TelemetryClock = Source.Clock }, CancellationToken.None));
        Assert.Equal(writes, Source.Writes.Count);
        Assert.Equal(inference, Intent.Models.Inference.Count);
        Assert.DoesNotContain(Requests, r => r.Method is "PUT" or "PATCH" or "DELETE");
        Assert.DoesNotContain(Requests, r => r.Method == "POST" && r.Uri.AbsolutePath != "/evidence" && r.Uri.Host != "api.loganalytics.io");
        return runner;
    }

    private async Task<HttpResponseMessage> QueryTelemetryAsync(HttpRequestMessage request, string principal, CancellationToken token)
    {
        Assert.Equal(Telemetry!.Configuration.SourceIdentity.PrincipalId, principal);
        Assert.Equal(HttpMethod.Post, request.Method);
        var input = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
        TelemetryQueries.Add(input);
        OnTelemetryQuery?.Invoke();
        if (TelemetryNativeResponse?.Invoke(request) is { } custom) return custom;
        var backend = Telemetry.Configuration.Backends.Single(b => request.RequestUri!.AbsolutePath == "/v1/workspaces/" + b.WorkspaceId + "/query");
        var query = input.GetProperty("query").GetString()!;
        var table = query.Split(' ')[0];
        var projected = query.Contains(", Name", StringComparison.Ordinal) || query.Contains(", Message", StringComparison.Ordinal)
            || query.Contains(", ExceptionType", StringComparison.Ordinal);
        var columns = new List<object> { new { name = "TimeGenerated", type = "datetime" }, new { name = "_ResourceId", type = "string" },
            new { name = "_ItemId", type = "string" }, new { name = "TenantId", type = "string" }, new { name = "Type", type = "string" } };
        var values = new List<object> { Source.Clock.Now.AddMinutes(-1).ToString("O"), backend.ApplicationResourceIds[0],
            "application-record-1", backend.WorkspaceId, table };
        if (projected)
        {
            columns.AddRange(FactoryPreviewTelemetryValidation.Fields(table).Select(f => new { name = f.Name, type = f.Type }));
            values.AddRange(table switch
            {
                "AppRequests" => new object[] { "POST /shifts/confirm", false, "409", 42.5 },
                "AppDependencies" => ["Shift reservation", "SQL", false, "409", 8.0],
                "AppExceptions" => ["InvalidOperationException", "Shift already assigned."],
                "AppTraces" => ["Shift confirmation rejected: already assigned.", 2],
                "AppEvents" => ["ShiftConfirmationRejected"],
                _ => throw new InvalidOperationException(),
            });
        }
        var body = JsonSerializer.SerializeToNode(new { tables = new[] { new { name = "PrimaryResult", columns, rows = new[] { values } } } })!;
        var response = SourceInventoryTransport.Json(ChangeTelemetryQueryResponse?.Invoke(body) ?? body);
        response.Headers.Add("x-ms-request-id", "aabbccdd-aaaa-bbbb-cccc-ddeeff112233");
        return response;
    }

    private sealed class PreviewTelemetryCredential(PreviewContextTransport owner) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext request, CancellationToken token)
        {
            var source = owner.Telemetry!.Configuration.SourceIdentity;
            var claims = new { oid = owner.TelemetryTokenIdentity ?? source.PrincipalId, appid = source.ClientId,
                tid = owner.TelemetryTokenTenant ?? owner.Source.Manifest.Identity.TenantId, xms_mirid = source.Target, ver = "1.0",
                aud = request.Scopes.Single()[..^"/.default".Length], nbf = owner.Source.Clock.Now.AddMinutes(-1).ToUnixTimeSeconds(),
                exp = owner.Source.Clock.Now.AddHours(1).ToUnixTimeSeconds() };
            return new("test." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
                + ".test", owner.Source.Clock.Now.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken token) => ValueTask.FromResult(GetToken(request, token));
    }
}
