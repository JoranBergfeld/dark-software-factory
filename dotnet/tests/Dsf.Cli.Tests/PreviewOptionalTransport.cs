using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed partial class PreviewPreparationTransport
{
    internal const string WebRole = BoundaryPlanFixture.Root + "/providers/microsoft.authorization/roledefinitions/f3333333-3333-3333-3333-333333333333";
    internal const string FoundryRole = BoundaryPlanFixture.Root + "/providers/microsoft.authorization/roledefinitions/f4444444-4444-4444-4444-444444444444";
    private SupplementalWorkflowTransport? supplemental;
    private HttpMessageInvoker? supplementalNative;
    internal int WebQueries;
    internal int FoundryQueries;
    internal string WebPayload = """{"webResults":[]}""";
    internal string FoundryPayload = """{"response":[],"activity":[{"id":1,"type":"searchIndex","knowledgeSourceName":"product-docs","count":0}],"references":[]}""";
    private DateTimeOffset? webSecretCreated;
    private IEnumerable<JsonNode> HostedResources => Resources.Values.Concat(supplemental?.Resources.Values ?? Enumerable.Empty<JsonNode>());

    internal Task SelectWebAsync() => SelectSupplementalAsync(false, true);
    internal Task SelectFoundryAsync() => SelectSupplementalAsync(true, false);
    internal Task SelectBothAsync() => SelectSupplementalAsync(true, true);

    private async Task SelectSupplementalAsync(bool foundry, bool web)
    {
        supplemental = new(scenario.Arm);
        supplementalNative = new(supplemental, false);
        supplemental.HostedProof = (request, token) => PreviewHostAsync(request, token);
        var selection = FactorySupplementalWorkflowTests.Selection();
        if (!web) selection["sources"] = new JsonArray();
        if (foundry) selection["sources"]!.AsArray().Add(FactorySupplementalWorkflowTests.FoundrySelection());
        var selected = await FactorySupplementalWorkflowTests.PrepareSelectionAsync(scenario.Machine, selection);
        Assert.True(selected.Exit == 0, (string?)selected.Result["detail"] + "\n" + string.Join("\n", HostFailures)
            + "\n" + string.Join("\n", Requests.TakeLast(10)));
        var consumers = new JsonArray();
        if (web) consumers.Add(Consumer("webiq", WebRole, "ab444444-4444-4444-4444-444444444444"));
        if (foundry) consumers.Add(Consumer("foundryiq", FoundryRole,
            web ? "ab555555-5555-5555-5555-555555555555" : "ab444444-4444-4444-4444-444444444444"));
        Selection["supplemental"] = consumers;

        static JsonObject Consumer(string kind, string role, string audience) => new()
        {
            ["kind"] = kind, ["runtimeImage"] = FactoryTelemetryWorkflowTests.Image,
            ["audienceClientId"] = audience, ["metadataRoleDefinitionId"] = role,
        };
    }

    private async Task<HttpResponseMessage?> OptionalNativeAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (supplemental is null) return null;
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (uri.Host == "product-search.search.windows.net")
        {
            Assert.Equal("?api-version=2026-04-01", uri.Query);
            var body = uri.AbsolutePath switch
            {
                "/knowledgebases" => """{"value":[{"name":"product-kb"}]}""",
                "/knowledgesources" => """{"value":[{"name":"product-docs"}]}""",
                "/knowledgebases('product-kb')" => """{"name":"product-kb","@odata.etag":"kb-v1","outputMode":"extractiveData","knowledgeSources":[{"name":"product-docs"}]}""",
                "/knowledgesources('product-docs')" => """{"name":"product-docs","@odata.etag":"source-v1","kind":"searchIndex","searchIndexParameters":{"searchIndexName":"product-index"}}""",
                "/indexes" => """{"value":[{"name":"product-index","@odata.etag":"index-v1","fields":[{"name":"id","type":"Edm.String","key":true}]}]}""",
                "/knowledgebases('product-kb')/retrieve" => FoundryPayload,
                _ => throw new InvalidOperationException("Unexpected selected Search request: " + uri.AbsolutePath),
            };
            Assert.Equal(uri.AbsolutePath.EndsWith("/retrieve", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, request.Method);
            if (request.Method == HttpMethod.Post) FoundryQueries++;
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
        if (uri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal))
        {
            Assert.Equal("/secrets/webiq-api-key/" + new string('a', 32), uri.AbsolutePath);
            var created = webSecretCreated ??= scenario.Source.Source!.Clock.Now.AddDays(-1);
            return Reply(new { id = uri.GetLeftPart(UriPartial.Path), value = "private-web-key", attributes = new
            { enabled = true, created = created.ToUnixTimeSeconds(), updated = created.ToUnixTimeSeconds(),
                nbf = created.ToUnixTimeSeconds(), exp = created.AddDays(2).ToUnixTimeSeconds() } });
        }
        if (uri.Host == "api.microsoft.ai")
        {
            Assert.Equal("https://api.microsoft.ai/v3/search/web", uri.AbsoluteUri);
            Assert.Equal("private-web-key", request.Headers.GetValues("x-apikey").Single());
            var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("Public retail accessibility research", (string?)query["query"]);
            WebQueries++;
            return new(HttpStatusCode.OK) { Content = new StringContent(WebPayload, Encoding.UTF8, "application/json") };
        }
        if (uri.Host != "management.azure.com") return null;
        if (Resources.ContainsKey(path)) return null;
        if (path.EndsWith("/roleassignments", StringComparison.Ordinal) || path.EndsWith("/permissions", StringComparison.Ordinal)) return null;
        if (request.Method == HttpMethod.Get && (path.EndsWith("-vault", StringComparison.Ordinal)
            || path.EndsWith("-hosting", StringComparison.Ordinal)))
            return await supplementalNative!.SendAsync(request, token);
        if (request.Method == HttpMethod.Get && (path.EndsWith(WebRole.Split('/').Last(), StringComparison.Ordinal)
            || path.EndsWith(FoundryRole.Split('/').Last(), StringComparison.Ordinal)))
            return Reply(new { id = path.EndsWith(FoundryRole.Split('/').Last(), StringComparison.Ordinal) ? FoundryRole : WebRole,
                properties = new { type = "CustomRole", roleName = "Preview supplemental metadata",
                assignableScopes = new[] { BoundaryPlanFixture.Root }, permissions = new[] { new
                { actions = FactoryPreviewSupplementalValidation.MetadataActions(path.EndsWith(FoundryRole.Split('/').Last(), StringComparison.Ordinal) ? "foundryiq" : "webiq"), notActions = Array.Empty<string>(),
                    dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } } });
        if (supplemental.Resources.ContainsKey(path) || path.EndsWith("/" + FactorySupplementalValidation.SecretsRole, StringComparison.Ordinal)
            || path == SupplementalWorkflowTransport.Search || path.EndsWith("/" + FactorySupplementalValidation.SearchDataRole, StringComparison.Ordinal)
            || path.EndsWith("/77777777-7777-7777-7777-777777777777", StringComparison.Ordinal)
            || path.Contains("-webiq", StringComparison.Ordinal) && !path.Contains("-preview-webiq", StringComparison.Ordinal)
            || path.Contains("-foundryiq", StringComparison.Ordinal) && !path.Contains("-preview-foundryiq", StringComparison.Ordinal))
            return await supplementalNative!.SendAsync(request, token);
        return null;
    }
}
