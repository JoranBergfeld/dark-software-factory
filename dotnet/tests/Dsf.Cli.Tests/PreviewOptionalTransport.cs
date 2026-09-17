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
    private SupplementalWorkflowTransport? supplemental;
    private HttpMessageInvoker? supplementalNative;
    internal int WebQueries;
    internal string WebPayload = """{"webResults":[]}""";
    private IEnumerable<JsonNode> HostedResources => Resources.Values.Concat(supplemental?.Resources.Values ?? Enumerable.Empty<JsonNode>());

    internal async Task SelectWebAsync()
    {
        supplemental = new(scenario.Arm);
        supplementalNative = new(supplemental, false);
        supplemental.HostedProof = (request, token) => PreviewHostAsync(request, token);
        var selected = await FactorySupplementalWorkflowTests.PrepareWebAsync(scenario.Machine);
        Assert.True(selected.Exit == 0, (string?)selected.Result["detail"] + "\n" + string.Join("\n", HostFailures)
            + "\n" + string.Join("\n", Requests.TakeLast(10)));
        Selection["supplemental"] = new JsonArray(new JsonObject { ["kind"] = "webiq", ["runtimeImage"] = FactoryTelemetryWorkflowTests.Image,
            ["audienceClientId"] = "ab444444-4444-4444-4444-444444444444", ["metadataRoleDefinitionId"] = WebRole });
    }

    private async Task<HttpResponseMessage?> OptionalNativeAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (supplemental is null) return null;
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (uri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal))
        {
            Assert.Equal("/secrets/webiq-api-key/" + new string('a', 32), uri.AbsolutePath);
            return Reply(new { id = uri.GetLeftPart(UriPartial.Path), value = "private-web-key", attributes = new
            { enabled = true, created = scenario.Source.Source!.Clock.Now.AddDays(-1).ToUnixTimeSeconds(),
                updated = scenario.Source.Source.Clock.Now.AddDays(-1).ToUnixTimeSeconds(),
                nbf = scenario.Source.Source.Clock.Now.AddDays(-1).ToUnixTimeSeconds(),
                exp = scenario.Source.Source.Clock.Now.AddDays(1).ToUnixTimeSeconds() } });
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
        if (request.Method == HttpMethod.Get && path.EndsWith(WebRole.Split('/').Last(), StringComparison.Ordinal))
            return Reply(new { id = WebRole, properties = new { type = "CustomRole", roleName = "Preview WebIQ metadata",
                assignableScopes = new[] { BoundaryPlanFixture.Root }, permissions = new[] { new
                { actions = FactoryPreviewSupplementalValidation.MetadataActions("webiq"), notActions = Array.Empty<string>(),
                    dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } } });
        if (supplemental.Resources.ContainsKey(path) || path.EndsWith("/" + FactorySupplementalValidation.SecretsRole, StringComparison.Ordinal)
            || path.Contains("-webiq", StringComparison.Ordinal) && !path.Contains("-preview-webiq", StringComparison.Ordinal))
            return await supplementalNative!.SendAsync(request, token);
        return null;
    }
}
