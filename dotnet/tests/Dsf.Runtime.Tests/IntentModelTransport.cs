using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

internal static class IntentModelFixture
{
    internal const string SourceText = "Volunteer coordinators use Shiftboard to coordinate volunteer shifts. "
        + "The application assigns volunteers to open shifts and prevents double booking.";

    internal static string Complete(string input)
    {
        if (input.Contains("CHECK_EVERY_CLAIM", StringComparison.Ordinal))
            return """{"complete":true,"checks":[{"claim":0,"verdict":"Supports","explanation":"The documentation explicitly describes coordinating volunteer shifts.","excerpt":"coordinate volunteer shifts"},{"claim":1,"verdict":"Supports","explanation":"The documentation identifies volunteer coordinators as the users.","excerpt":"Volunteer coordinators"},{"claim":2,"verdict":"Supports","explanation":"Assignment and double-booking prevention directly support this goal.","excerpt":"prevents double booking"}],"limitations":[]}""";
        return """
            {"complete":true,"findings":"Documentation describes shift coordination for volunteer coordinators, with assignment and double-booking prevention. Deployment and author authority are not established.",
             "claims":[
              {"field":"Purpose","kind":"InferredPurpose","text":"Coordinate volunteer shifts without double booking.","authority":"Unknown","currency":"Pinned source only","evidence":[{"pathIndex":1,"chunkIndex":0,"excerpt":"coordinate volunteer shifts","explanation":"The documented assignment workflow supports this inferred purpose."}]},
              {"field":"Users","kind":"InferredPurpose","text":"Volunteer coordinators arranging staffed shifts.","authority":"Unknown","currency":"Pinned source only","evidence":[{"pathIndex":1,"chunkIndex":0,"excerpt":"Volunteer coordinators","explanation":"The documentation explicitly identifies these application users."}]},
              {"field":"Goal","kind":"InferredPurpose","text":"Assign volunteers to open shifts while preventing double booking.","authority":"Unknown","currency":"Pinned source only","evidence":[{"pathIndex":1,"chunkIndex":0,"excerpt":"prevents double booking","explanation":"The documented behavior describes assignment and collision prevention."}]}],
             "nonGoals":{"state":"Unknown","reason":"No authoritative non-goals are recorded."},
             "successMeasures":{"state":"Unknown","reason":"No measured targets are recorded."},"limitations":["Deployment correlation and recorded author authority are unknown."]}
            """;
    }
}

internal sealed class IntentModelRunner(SourceInventoryTransport source, string route, JsonNode request) : IWebHostRunner
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
            using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
            http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", source.GitHub.CodePrincipalId);
            http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                auth_typ = "aad", claims = new[] { new { typ = "oid", val = source.GitHub.CodePrincipalId },
                    new { typ = "tid", val = source.Manifest.Identity.TenantId }, new { typ = "appid", val = source.GitHub.CodeClientId } },
            })));
            using var response = await http.PostAsJsonAsync(route, request, token);
            Status = response.StatusCode;
            Body = await response.Content.ReadAsStringAsync(token);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}

internal sealed class IntentNativeModelTransport(SourceInventoryTransport source, HttpMessageHandler arm, Func<string, string>? complete = null) : HttpMessageHandler
{
    private readonly HttpMessageInvoker native = new(arm, false);
    internal readonly List<JsonNode> Completions = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.Host == new Uri(source.ProductEndpoint).Host)
            return SourceInventoryTransport.Json(new { items = source.Values.Select(p => new
                { key = p.Key, value = p.Value, label = (string?)null, etag = "current" }).ToArray() });
        if (request.RequestUri.Host == "management.azure.com") return await native.SendAsync(request, token);
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(source.Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        var authority = FactoryModelsValidation.ReadPreparation(manifest, source.Values);
        var selection = authority.Routes![0].Selection;
        Assert.Equal(new Uri(selection.Endpoint).Host, request.RequestUri.Host);
        Assert.Equal("/openai/v1/chat/completions", request.RequestUri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, request.Method);
        var input = await request.Content!.ReadFromJsonAsync<JsonNode>(cancellationToken: token) ?? throw new JsonException();
        Completions.Add(input);
        var content = (complete ?? IntentModelFixture.Complete)((string)input["messages"]![0]!["content"]!);
        var response = SourceInventoryTransport.Json(new { model = selection.Model, choices = new[]
            { new { finish_reason = "stop", message = new { content } } } });
        response.Headers.Add("x-request-id", Guid.NewGuid().ToString());
        return response;
    }
    protected override void Dispose(bool disposing) { if (disposing) native.Dispose(); base.Dispose(disposing); }
}

internal sealed class IntentModelCredential(SourceInventoryTransport source) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext request, CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(source.Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        var model = FactoryModelsValidation.ReadPreparation(manifest, source.Values).Bindings![0];
        var claims = new { oid = model.PrincipalId, appid = model.ClientId, tid = manifest.Identity.TenantId,
            xms_mirid = model.Target, ver = "1.0", aud = request.Scopes.Single()[..^"/.default".Length],
            nbf = source.Clock.Now.AddMinutes(-1).ToUnixTimeSeconds(), exp = source.Clock.Now.AddHours(1).ToUnixTimeSeconds() };
        return new("test." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test",
            source.Clock.Now.AddHours(1));
    }
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken token) => ValueTask.FromResult(GetToken(request, token));
}
