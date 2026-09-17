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

internal sealed partial class PreviewPreparationTransport
{
    internal readonly Dictionary<string, string> SeedDocuments = new(StringComparer.Ordinal);
    internal int SeedCreates;
    internal string? SeedFailure { get; set; }
    internal Action? BeforeSeed { get; set; }
    internal string LastPreviewResponse = "";
    internal readonly List<string> HostFailures = [];

    private async Task<HttpResponseMessage> PreviewHostAsync(HttpRequestMessage request, CancellationToken token, bool modelProof = false)
    {
        var runner = new PreviewRunner(this, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token),
            request.Headers.Authorization?.Parameter);
        using var http = new HttpClient(this, false) { Timeout = Timeout.InfiniteTimeSpan };
        var council = scenario.Arm.CreatedResources.Values.Single(r => ((string?)r["id"])?.EndsWith("-council", StringComparison.Ordinal) == true);
        var settings = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = Manifest.Identity.ProductKey, ["AZURE_APPCONFIG_ENDPOINT"] = Authority.Configuration.ProductEndpoint,
            ["DSF_FACTORY_PROFILE_PIN"] = Manifest.ProfilePin, ["AZURE_CLIENT_ID"] = (string?)council["properties"]!["clientId"],
        };
        var verb = "serve-models";
        var binding = council;
        if (!modelProof)
        {
            var native = HostedResources.Single(r => (string?)r["properties"]?["configuration"]?["ingress"]?["fqdn"] == request.RequestUri.Host);
            var container = native["properties"]!["template"]!["containers"]![0]!;
            verb = (string)container["args"]![0]!;
            settings = container["env"]!.AsArray().ToDictionary(e => (string)e!["name"]!, e => (string?)e!["value"]);
            var identity = native["identity"]!["userAssignedIdentities"]!.AsObject().Single().Key;
            binding = scenario.Arm.CreatedResources.Values.Concat(Telemetry.Resources.Values).Concat(supplemental?.Resources.Values ?? Enumerable.Empty<JsonNode>())
                .Single(r => string.Equals((string?)r["id"], identity, StringComparison.OrdinalIgnoreCase));
        }
        scenario.Source.Source!.Values = Values();
        var credential = new PreviewCredential(this, binding);
        var error = new StringWriter();
        var exit = await RuntimeCliApplication.InvokeAsync([verb], settings, new StringWriter(), error,
            RuntimeDependencies.Production() with { PreviewHttp = http, PreviewCredential = credential,
                PreviewClock = scenario.Source.Source.Clock, ModelHttp = http, ModelCredential = credential,
                ModelClock = scenario.Source.Source.Clock, SourceHttp = http, SourceCredential = credential,
                SourceClock = scenario.Source.Source.Clock, TelemetryHttp = http, TelemetryCredential = credential,
                TelemetryClock = scenario.Source.Source.Clock, WebHostRunner = runner }, token);
        Assert.True(exit == 0, error.ToString());
        LastPreviewResponse = runner.Body;
        if (runner.Status != HttpStatusCode.OK) HostFailures.Add(verb + ": " + runner.Body);
        return new(runner.Status) { Content = new StringContent(runner.Body, Encoding.UTF8, "application/json") };
    }

    private async Task<HttpResponseMessage> ModelProbeAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = await request.Content!.ReadFromJsonAsync<JsonNode>(cancellationToken: token) ?? throw new JsonException();
        var authority = FactoryModelsValidation.ReadPreparation(Manifest, Values());
        var route = authority.Routes!.Single(r => r.Selection.Deployment == (string?)body["model"]).Selection;
        HttpResponseMessage response;
        var prompt = (string?)body["messages"]?[0]?["content"];
        var probe = prompt?.Contains("CAPABILITY PROBE", StringComparison.Ordinal) == true
            || route.Name == "embedding" && body["input"]!.AsArray().All(v => ((string)v!).Contains("CAPABILITY PROBE", StringComparison.Ordinal));
        if (!probe) return NativeEvaluationResponse(route, body, prompt);
        if (route.Name == "embedding")
        {
            Assert.Equal("/openai/v1/embeddings", request.RequestUri!.AbsolutePath);
            Assert.All(body["input"]!.AsArray(), value => Assert.Contains("CAPABILITY PROBE", (string?)value));
            response = Reply(new { model = route.Model, data = Enumerable.Range(0, 3).Select(i => new
                { index = i, embedding = Enumerable.Range(0, route.Dimensions).Select(d => d == (i < 2 ? 0 : 1) ? 1.0 : 0.0).ToArray() }).ToArray() });
        }
        else
        {
            Assert.Equal("/openai/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Contains("CAPABILITY PROBE", (string?)body["messages"]![0]!["content"]);
            response = Reply(new { model = route.Model, choices = new[] { new { finish_reason = "stop",
                message = new { content = (route.Name == "synthesis" ? "" : "GO: ")
                    + "Equivalent problem descriptions should retain their stable identifier, preventing duplicated ownership and preserving traceability." } } } });
        }
        response.Headers.Add("x-request-id", Guid.NewGuid().ToString());
        return response;
    }

    private async Task<HttpResponseMessage> SeedAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.True(request.RequestUri!.AbsolutePath.Contains("/colls/council-preview/docs", StringComparison.Ordinal)
            || request.RequestUri.AbsolutePath.Contains("/colls/runs/docs", StringComparison.Ordinal));
        Assert.Equal("[\"" + Manifest.Identity.ProductKey + "\"]", request.Headers.GetValues("x-ms-documentdb-partitionkey").Single());
        Assert.False(request.Headers.Contains("x-ms-documentdb-is-upsert"));
        var path = request.RequestUri.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            path += "/" + (string)body["id"]!;
            if (SeedDocuments.ContainsKey(path)) return new(HttpStatusCode.Conflict);
            if (path.Contains("/council-preview/", StringComparison.Ordinal)) { BeforeSeed?.Invoke(); SeedCreates++; }
            body["_rid"] = "PD5DALigD00BAAAAAAAAAA==";
            body["_etag"] = "\"" + Guid.NewGuid().ToString("D") + "\"";
            SeedDocuments.Add(path, body.ToJsonString());
            if (SeedFailure == "lost") throw new HttpRequestException("lost-private-native-seed-response");
            if (SeedFailure == "canceled") throw new OperationCanceledException(token);
        }
        else if (request.Method == HttpMethod.Put)
        {
            if (!SeedDocuments.TryGetValue(path, out var before)) return new(HttpStatusCode.NotFound);
            var original = JsonNode.Parse(before)!;
            if ((string?)original["_etag"] != request.Headers.GetValues("If-Match").Single()) return new(HttpStatusCode.PreconditionFailed);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            body["_rid"] = original["_rid"]!.DeepClone();
            body["_etag"] = "\"" + Guid.NewGuid().ToString("D") + "\"";
            SeedDocuments[path] = body.ToJsonString();
            AfterEvaluationWrite?.Invoke(body);
        }
        else Assert.Equal(HttpMethod.Get, request.Method);
        if (!SeedDocuments.TryGetValue(path, out var text)) return new(HttpStatusCode.NotFound);
        var response = new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", (string?)JsonNode.Parse(text)!["_etag"]);
        return response;
    }

    private sealed class PreviewCredential(PreviewPreparationTransport transport, JsonNode identity) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => GetTokenAsync(context, token).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
        {
            var claims = new { oid = (string?)identity["properties"]!["principalId"], appid = (string?)identity["properties"]!["clientId"],
                tid = transport.Manifest.Identity.TenantId, xms_mirid = (string?)identity["id"], ver = "1.0",
                aud = context.Scopes.Single()[..^"/.default".Length],
                exp = transport.Scenario.Source.Source!.Clock.Now.AddHours(1).ToUnixTimeSeconds(),
                nbf = transport.Scenario.Source.Source.Clock.Now.AddMinutes(-1).ToUnixTimeSeconds() };
            return ValueTask.FromResult(new AccessToken("test." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class PreviewRunner(PreviewPreparationTransport transport, string path, string payload, string? bearer) : IWebHostRunner
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
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = Timeout.InfiniteTimeSpan };
                var principal = transport.Manifest.Identity.AzureActorId;
                string? application = null;
                if (bearer?.StartsWith("test.", StringComparison.Ordinal) == true)
                {
                    var encoded = bearer.Split('.')[1].Replace('-', '+').Replace('_', '/');
                    var claims = JsonNode.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')))!;
                    principal = (string)claims["oid"]!;
                    application = (string?)claims["appid"];
                }
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", principal);
                client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
                { auth_typ = "aad", claims = new[] { new { typ = "oid", val = principal },
                    new { typ = "tid", val = transport.Manifest.Identity.TenantId }, new { typ = "appid", val = application ?? "" } } })));
                using var response = await client.PostAsync(path, new StringContent(payload, Encoding.UTF8, "application/json"), token);
                Status = response.StatusCode;
                Body = await response.Content.ReadAsStringAsync(token);
            }
            finally { await app.StopAsync(CancellationToken.None); }
        }
    }
}
