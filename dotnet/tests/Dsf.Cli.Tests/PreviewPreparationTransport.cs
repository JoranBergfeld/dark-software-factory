using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.Runtime;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed partial class PreviewPreparationTransport(CharterScenario scenario) : HttpMessageHandler
{
    internal const string CouncilRole = BoundaryPlanFixture.Root + "/providers/microsoft.authorization/roledefinitions/f1111111-1111-1111-1111-111111111111";
    internal const string TelemetryRole = BoundaryPlanFixture.Root + "/providers/microsoft.authorization/roledefinitions/f2222222-2222-2222-2222-222222222222";
    internal CharterScenario Scenario => scenario;
    internal readonly TelemetryWorkflowTransport Telemetry = new(scenario.Arm);
    internal readonly Dictionary<string, JsonNode> Resources = new(StringComparer.OrdinalIgnoreCase);
    internal readonly System.Collections.Concurrent.ConcurrentQueue<(string Method, Uri Uri)> Requests = new();
    internal int TelemetryQueries;
    internal string? DeniedAction { get; set; }
    internal bool DenyNativeReads { get; set; }
    internal Func<Uri, HttpStatusCode?>? ArmReadStatus { get; set; }
    internal JsonNode Selection = JsonSerializer.SerializeToNode(new PreviewPreparationSelection(
        Guid.Parse("ab000000-0000-0000-0000-000000000214"), FactoryTelemetryWorkflowTests.Image,
        "ab111111-1111-1111-1111-111111111111", CouncilRole,
        new(FactoryTelemetryWorkflowTests.Image, "ab222222-2222-2222-2222-222222222222"),
        new(FactoryTelemetryWorkflowTests.Image, "ab333333-3333-3333-3333-333333333333", TelemetryRole, 1),
        [], new(2, 0.6, [new("value", 1), new("cost", 1), new("feasibility", 1), new("security", 1), new("strategic-fit", 1)])), FactoryGitHubJson.Options)!;
    private readonly HttpMessageInvoker source = new(scenario.Source, false);
    private readonly HttpMessageInvoker models = new(scenario.Models, false);
    private HttpMessageInvoker? telemetry;

    internal static async Task<PreviewPreparationTransport> CreateAsync(bool publisher = false)
    {
        var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter, telemetry: true, publisher: publisher);
        var adopted = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing",
            "--approve", (string)adopted.Result["revision"]!)).Exit);
        var transport = new PreviewPreparationTransport(scenario);
        transport.telemetry = new(transport.Telemetry, false);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var plan = await scenario.Machine.InvokeAsync("telemetry", FactoryTelemetryWorkflowTests.Preparation);
        var prepared = await scenario.Machine.InvokeAsync("telemetry", [.. FactoryTelemetryWorkflowTests.Preparation,
            "--approve", (string)plan.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.True(prepared.Exit == 0, (string?)prepared.Result["detail"] + prepared.Terminal.Error);
        var clock = scenario.Source.Source!.Clock;
        var operatorNow = scenario.Machine.Boundary.Repository.ReservationClock!.GetUtcNow();
        clock.Now = clock.Now > operatorNow ? clock.Now : operatorNow;
        scenario.Machine.Boundary.Repository.ReservationClock = clock;
        return transport;
    }

    internal Dictionary<string, string> Values() => scenario.Arm.Product.Values();
    internal FactoryManifest Manifest => JsonSerializer.Deserialize<FactoryManifest>(Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
    internal FactoryTelemetry Authority => JsonSerializer.Deserialize<FactoryTelemetry>(Values()[ProductConfigurationKeys.FactoryTelemetry], FactoryGitHubJson.Options)!;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Enqueue((request.Method.Method, request.RequestUri!));
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        if (uri.Host == "source.private.azurecontainerapps.io") return await source.SendAsync(request, token);
        if (HostedResources.Any(r => (string?)r["properties"]?["configuration"]?["ingress"]?["fqdn"] == uri.Host))
            return await PreviewHostAsync(request, token);
        if (uri.Host == "models.private.azurecontainerapps.io") return await PreviewHostAsync(request, token, modelProof: true);
        if (await PublisherGitHubAsync(request, token) is { } github) return github;
        if (uri.Host == "shared-models.services.ai.azure.com") return await ModelProbeAsync(request, token);
        if (uri.Host.EndsWith("-cosmos.documents.azure.com", StringComparison.Ordinal)
            && (path.Contains("/colls/council-preview/", StringComparison.Ordinal) || path.Contains("/colls/runs/", StringComparison.Ordinal)
                || path.Contains("/colls/proposal-publications/", StringComparison.Ordinal)))
        {
            var response = await SeedAsync(request, token);
            if (request.Method == HttpMethod.Get && response.StatusCode == HttpStatusCode.OK && AfterNativeReadResponse is not null)
                await AfterNativeReadResponse(path, JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!);
            return response;
        }
        if (uri.Host == "management.azure.com" && request.Method == HttpMethod.Put)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            if (path.Contains("/deployments/preview-", StringComparison.Ordinal)
                || path.Contains("/deployments/live-", StringComparison.Ordinal)
                || path.Contains("/deployments/publisher-", StringComparison.Ordinal)
                || (string?)body["properties"]?["roleDefinitionId"] is CouncilRole or TelemetryRole or WebRole or FoundryRole)
                return await CreatePreviewAsync(request, body, token);
            if (supplementalNative is not null && (string?)body["properties"]?["roleDefinitionId"] is { } role
                && (role.EndsWith("/" + FactorySupplementalValidation.SecretsRole, StringComparison.Ordinal)
                    || role.EndsWith("/" + FactorySupplementalValidation.SearchDataRole, StringComparison.Ordinal)
                    || role.EndsWith("/77777777-7777-7777-7777-777777777777", StringComparison.Ordinal)))
                return await supplementalNative.SendAsync(request, token);
        }
        if (await OptionalNativeAsync(request, token) is { } optionalResponse) return optionalResponse;
        if (uri.Host == "telemetry.private.azurecontainerapps.io")
        {
            Assert.Equal("/proof", path);
            using var http = new HttpClient(this, false);
            var runner = new TelemetryRunner(this);
            var settings = new Dictionary<string, string?>
            {
                ["DSF_PRODUCT"] = Manifest.Identity.ProductKey, ["AZURE_APPCONFIG_ENDPOINT"] = Authority.Configuration.ProductEndpoint,
                ["DSF_FACTORY_PROFILE_PIN"] = Manifest.ProfilePin, ["AZURE_CLIENT_ID"] = Authority.Configuration.SourceIdentity.ClientId,
            };
            var exit = await RuntimeCliApplication.InvokeAsync(["serve-telemetry"], settings, new StringWriter(), new StringWriter(),
                RuntimeDependencies.Production() with { TelemetryHttp = http, TelemetryCredential = new Credential(this),
                    TelemetryClock = scenario.Source.Source!.Clock, WebHostRunner = runner }, token);
            Assert.Equal(0, exit);
            Assert.True(runner.Status == HttpStatusCode.OK, runner.Body + "\n" + string.Join("\n", Requests.TakeLast(12)));
            return new(runner.Status) { Content = new StringContent(runner.Body, Encoding.UTF8, "application/json") };
        }
        if (uri.Host == "api.loganalytics.io")
            return await ApplicationQueryAsync(request, token);
        if (uri.Host == "management.azure.com" && request.Method == HttpMethod.Get)
        {
            if (ArmReadStatus?.Invoke(uri) is { } status) return new(status);
            if (DenyNativeReads) return new(HttpStatusCode.Forbidden);
            if (path.EndsWith("/sqlroleassignments", StringComparison.Ordinal))
                return Reply(new { value = Resources.Where(p => p.Key.StartsWith(path + "/", StringComparison.Ordinal)).Select(p => p.Value).ToArray() });
            var operationParent = path.LastIndexOf("/operations/", StringComparison.Ordinal);
            if (operationParent > 0 && DeploymentOperations.TryGetValue(path[..operationParent], out var deployment))
                return Reply(deployment.Single(o => (string?)o["id"] == path));
            if (path.Contains("/roledefinitions/", StringComparison.Ordinal))
            {
                var roleId = path.Split('/').Last();
                var originalRole = scenario.Source.Source!.Resources.SingleOrDefault(p =>
                    p.Key.EndsWith("/roledefinitions/" + roleId, StringComparison.OrdinalIgnoreCase)).Value;
                if (originalRole is not null) return Reply(originalRole);
                if (roleId is "f1111111-1111-1111-1111-111111111111" or "f2222222-2222-2222-2222-222222222222")
                    path = roleId[1] == '1' ? CouncilRole : TelemetryRole;
            }
            if (path.EndsWith("/permissions", StringComparison.OrdinalIgnoreCase))
                return Reply(new { value = new[] { new { actions = new[] { "*" },
                    notActions = DeniedAction is null ? Array.Empty<string>() : new[] { DeniedAction },
                    dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } });
            if (path.EndsWith("/roleassignments", StringComparison.OrdinalIgnoreCase))
            {
                var filter = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).GetValueOrDefault("$filter").ToString();
                var scope = path[..^"/providers/microsoft.authorization/roleassignments".Length];
                var all = Resources.Concat(scenario.Source.Resources).Concat(scenario.Models.Resources)
                    .Concat(Telemetry.Resources).Concat(scenario.Source.Source!.Resources)
                    .Concat(supplemental?.Resources ?? new Dictionary<string, JsonNode>())
                    .Where(p => p.Key.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value);
                var assigned = System.Text.RegularExpressions.Regex.Match(filter, @"assignedTo\('([0-9a-f-]+)'\)");
                if (assigned.Success)
                    all = all.Where(r => (string?)r["properties"]!["principalId"] == assigned.Groups[1].Value);
                all = filter.Contains("atScope()", StringComparison.Ordinal)
                    ? all.Where(r => scope.StartsWith((string)r["properties"]!["scope"]!, StringComparison.OrdinalIgnoreCase))
                    : all.Where(r => ((string)r["properties"]!["scope"]!).StartsWith(scope, StringComparison.OrdinalIgnoreCase));
                return Reply(new { value = all.ToArray() });
            }
            if (Resources.TryGetValue(path, out var native)) return Reply(native);
            if (path.EndsWith("/operations", StringComparison.Ordinal) && DeploymentOperations.TryGetValue(path[..^"/operations".Length], out var operations))
                return Reply(new { value = operations });
            if (path is CouncilRole or TelemetryRole) return Reply(new { id = path, properties = new
            {
                type = "CustomRole", roleName = path == CouncilRole ? "Preview council metadata" : "Preview telemetry metadata",
                assignableScopes = new[] { BoundaryPlanFixture.Root },
                permissions = new[] { new { actions = path == CouncilRole ? FactoryPreviewValidation.MetadataActions : FactoryPreviewTelemetryValidation.MetadataActions,
                    notActions = Array.Empty<string>(), dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } },
            } });
            if (scenario.Models.Resources.ContainsKey(path) || path.StartsWith(ModelsWorkflowTransport.Account, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(ModelsWorkflowTransport.Role.Split('/').Last(), StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/roledefinitions/516239f1-63e1-4d78-a4de-a74fb236a071", StringComparison.OrdinalIgnoreCase))
                return await models.SendAsync(request, token);
            if (scenario.Source.Resources.ContainsKey(path) || scenario.Source.Source!.Resources.ContainsKey(path))
                return await source.SendAsync(request, token);
            if (scenario.Arm.CreatedResources.TryGetValue(path, out var foundation)) return Reply(foundation);
            if (path.StartsWith(BoundaryPlanFixture.Logs.ToLowerInvariant() + "/tables/", StringComparison.Ordinal))
                return Reply(new { id = path, name = path.Split('/').Last(), properties = new { plan = "Analytics", protectionLevel = "General" } });
        }
        if (uri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal) && uri.Host != new Uri(scenario.Source.Source!.ProgressEndpoint).Host)
            return await source.SendAsync(request, token);
        if (uri.Host == "management.azure.com") return await telemetry!.SendAsync(request, token);
        using var backend = new HttpMessageInvoker(scenario.Source.WorkloadTransport!(scenario.Source.Source!), true);
        return await backend.SendAsync(request, token);
    }

    private sealed class Credential(PreviewPreparationTransport transport) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => GetTokenAsync(context, token).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
        {
            var identity = transport.Authority.Configuration.SourceIdentity;
            var claims = new { oid = identity.PrincipalId, appid = identity.ClientId, tid = transport.Manifest.Identity.TenantId, xms_mirid = identity.Target };
            return ValueTask.FromResult(new AccessToken("test." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class TelemetryRunner(PreviewPreparationTransport transport) : IWebHostRunner
    {
        internal HttpStatusCode Status;
        internal string Body = "";
        public async Task RunAsync(WebApplication app, CancellationToken token)
        {
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync(token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", transport.Manifest.Identity.AzureActorId);
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
                { auth_typ = "aad", claims = new[] { new { typ = "oid", val = transport.Manifest.Identity.AzureActorId },
                    new { typ = "tid", val = transport.Manifest.Identity.TenantId } } })));
                using var response = await client.PostAsync("/proof", null, token);
                Status = response.StatusCode;
                Body = await response.Content.ReadAsStringAsync(token);
            }
            finally { await app.StopAsync(CancellationToken.None); }
        }
    }

    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopPreviewHosts(); source.Dispose(); models.Dispose(); telemetry?.Dispose(); Telemetry.Dispose(); supplementalNative?.Dispose(); supplemental?.Dispose(); scenario.Dispose(); }
        base.Dispose(disposing);
    }
}
