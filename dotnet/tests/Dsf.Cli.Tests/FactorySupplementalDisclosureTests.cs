using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactorySupplementalDisclosureTests
{
    private const string Secret = "secret-key-do-not-print";
    private const string Content = "Public keyboard accessibility guidance.";

    [Theory]
    [InlineData("normal", false)]
    [InlineData("hostname", false)]
    [InlineData("hostname-case", false)]
    [InlineData("hostname-idn", false)]
    [InlineData("path-query-fragment", false)]
    [InlineData("title", false)]
    [InlineData("content", true)]
    [InlineData("content-case", true)]
    [InlineData("mixed-invalid", true)]
    [InlineData("provider-error", true)]
    public async Task Real_host_cli_and_serialized_provenance_never_disclose_provider_reflected_key(string scenario, bool blocked)
    {
        var url = scenario switch
        {
            "hostname" => "https://" + Secret + ".example.com/research",
            "hostname-case" => "https://" + Secret.ToUpperInvariant() + ".EXAMPLE.com/research",
            "hostname-idn" => "https://" + Secret + ".b\u00fccher.example/research",
            "path-query-fragment" => "https://example.com/" + Secret + "?access_token=" + Secret + "#" + Secret,
            _ => "https://example.com/research",
        };
        var rows = new JsonArray(new JsonObject
        {
            ["url"] = url, ["title"] = scenario == "title" ? Secret : "Research",
            ["content"] = scenario == "content" ? Secret : scenario == "content-case" ? Secret.ToUpperInvariant() : Content,
        });
        if (scenario == "mixed-invalid")
            rows.Add(new JsonObject { ["url"] = "https://example.com/second", ["content"] = Secret });
        var payload = new JsonObject { ["webResults"] = rows };
        if (scenario == "provider-error") payload["errorCode"] = Secret;
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var observations = new List<HostObservation>();
        transport.HostedProof = async (request, token) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var host = transport.Resources.Single(p => p.Key.Contains("/containerapps/", StringComparison.Ordinal)
                && p.Key.EndsWith("-webiq", StringComparison.Ordinal)).Value;
            var environment = host["properties"]!["template"]!["containers"]![0]!["env"]!.AsArray()
                .ToDictionary(e => (string)e!["name"]!, e => (string?)e!["value"]);
            var config = JsonSerializer.Deserialize<SupplementalHostConfiguration>(environment["DSF_SUPPLEMENTAL_CONFIG"]!, FactoryGitHubJson.Options)!;
            using var upstream = new WebTransport(config, payload.ToJsonString(), scenario == "provider-error" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            using var http = new HttpClient(upstream);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var runner = new HostObservation(config, await request.Content!.ReadAsStringAsync(token));
            var exit = await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], environment, output, error,
                RuntimeDependencies.Production() with
                {
                    WebHostRunner = runner, SourceCredential = new SourceCredential(config),
                    SourceHttp = http, SourceClock = new ReservationClock(),
                }, token);
            Assert.Equal(0, exit);
            Assert.Equal(blocked ? 4 : 6, upstream.Requests.Count);
            AssertSecretAbsent(output.ToString() + error + string.Join('\n', upstream.Requests));
            observations.Add(runner);
            return new HttpResponseMessage(runner.ProofStatus)
            { Content = new StringContent(runner.Proof, Encoding.UTF8, "application/json") };
        };

        var result = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        var serialized = arm.Product.Export();
        arm.Product = ReservationTestStore.Restore(serialized);
        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        observer.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var status = await observer.InvokeAsync("supplemental");
        Assert.Equal(serialized, arm.Product.Export());
        var durable = JsonNode.Parse(arm.Product.ListJson())!.AsArray().Single(r =>
            ((string)r!["key"]!).StartsWith("dsf/factory/supplemental-proofs/", StringComparison.Ordinal))!;
        var proof = JsonNode.Parse((string)durable["value"]!)!;
        AssertSecretAbsent(serialized + result.Terminal.Output + result.Terminal.Error + status.Terminal.Output + status.Terminal.Error);
        Assert.Equal(blocked ? 1 : 0, result.Exit);
        Assert.Equal(blocked ? "Blocked" : "ReadySupplemental", (string?)status.Result["supplemental"]!["state"]);
        var observation = Assert.Single(observations);
        AssertSecretAbsent(observation.Proof + observation.Evidence + string.Join('\n', observation.Logs.Messages));
        Assert.Equal(blocked ? HttpStatusCode.Conflict : HttpStatusCode.OK, observation.ProofStatus);
        Assert.Equal(observation.ProofStatus, observation.EvidenceStatus);
        var evidence = JsonNode.Parse(observation.Evidence)!;
        if (blocked)
        {
            Assert.Equal(scenario == "provider-error" ? "PermissionDenied" : "MalformedResponse", (string?)proof["reason"]);
            Assert.Empty(proof["records"]!.AsArray());
            Assert.Empty(evidence["items"]!.AsArray());
        }
        else
        {
            var reference = "webiq:sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(url, FactoryGitHubJson.Options)));
            Assert.Equal("None", (string?)proof["reason"]);
            Assert.Equal(reference, (string?)Assert.Single(proof["records"]!.AsArray())!["reference"]);
            var item = Assert.Single(evidence["items"]!.AsArray())!;
            Assert.Equal(reference, (string?)item["reference"]);
            Assert.Equal(Content, (string?)item["content"]);
        }
        Assert.Equal(3, transport.Writes.Count);
        Assert.Null(result.Result["telemetry"]);
        Assert.False((bool)result.Result["isActivated"]!);
    }

    [Fact]
    public async Task Cli_rejects_old_hostname_bearing_host_proof_before_persistence()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm) { ChangeProof = proof =>
        {
            proof["reason"] = "None";
            proof["records"] = new JsonArray(new JsonObject
            {
                ["reference"] = "webiq:https://" + Secret + ".example.com/#" + new string('a', 64),
                ["contentSha256"] = new string('b', 64),
            });
        } };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await FactorySupplementalWorkflowTests.PrepareWebAsync(machine);
        AssertSecretAbsent(arm.Product.Export() + result.Terminal.Output + result.Terminal.Error);
        Assert.Equal(1, result.Exit);
        Assert.Equal("MalformedResponse", (string?)result.Result["supplemental"]!["checks"]![0]!["reason"]);
    }

    private static void AssertSecretAbsent(string value) =>
        Assert.DoesNotContain(Secret, value, StringComparison.OrdinalIgnoreCase);

    private sealed class SourceCredential(SupplementalHostConfiguration config) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => GetTokenAsync(context, token).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
        {
            var now = new ReservationClock().GetUtcNow();
            var claims = JsonSerializer.Serialize(new
            {
                ver = "1.0", oid = config.SourcePrincipalId, tid = config.TenantId, appid = config.SourceClientId,
                xms_mirid = config.SourceIdentityId, aud = context.Scopes.Single()[..^"/.default".Length],
                nbf = now.AddMinutes(-1).ToUnixTimeSeconds(), exp = now.AddMinutes(30).ToUnixTimeSeconds(),
            });
            return ValueTask.FromResult(new AccessToken("header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)) + ".signature", now.AddMinutes(30)));
        }
    }

    private sealed class WebTransport(SupplementalHostConfiguration config, string payload, HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.GetLeftPart(UriPartial.Path) == config.SecretUri)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("?api-version=7.4", request.RequestUri.Query);
                var now = new ReservationClock().GetUtcNow();
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = config.SecretUri, value = Secret,
                    attributes = new { enabled = true, exp = now.AddDays(1).ToUnixTimeSeconds(),
                        created = now.AddDays(-1).ToUnixTimeSeconds(), updated = now.AddDays(-1).ToUnixTimeSeconds() } }) };
            }
            Assert.Equal("https://api.microsoft.ai/v3/search/web", request.RequestUri.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Secret, request.Headers.GetValues("x-apikey").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(config.Source.Query, (string?)body["query"]);
            Assert.Equal("text", (string?)body["contentFormat"]);
            return new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class HostObservation(SupplementalHostConfiguration config, string request) : IWebHostRunner
    {
        public HttpStatusCode ProofStatus { get; private set; }
        public HttpStatusCode EvidenceStatus { get; private set; }
        public string Proof { get; private set; } = "";
        public string Evidence { get; private set; } = "";
        public RecordedLogs Logs { get; } = new();
        public async Task RunAsync(WebApplication app, CancellationToken token)
        {
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            app.Services.GetRequiredService<ILoggerFactory>().AddProvider(Logs);
            app.Use(async (context, next) =>
            {
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] = "aad";
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = config.OperatorPrincipalId;
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { auth_typ = "aad", claims = new[]
                    {
                        new { typ = "http://schemas.microsoft.com/identity/claims/objectidentifier", val = config.OperatorPrincipalId },
                        new { typ = "http://schemas.microsoft.com/identity/claims/tenantid", val = config.TenantId },
                    } })));
                await next(context);
            });
            await app.StartAsync(token);
            try
            {
                using var http = new HttpClient();
                using var proof = await http.PostAsync(app.Urls.Single() + "/proof", new StringContent(request, Encoding.UTF8, "application/json"), token);
                ProofStatus = proof.StatusCode;
                Proof = await proof.Content.ReadAsStringAsync(token);
                using var evidence = await http.PostAsync(app.Urls.Single() + "/evidence", new StringContent(request, Encoding.UTF8, "application/json"), token);
                EvidenceStatus = evidence.StatusCode;
                Evidence = await evidence.Content.ReadAsStringAsync(token);
            }
            finally { await app.StopAsync(CancellationToken.None); }
        }
    }

    private sealed class RecordedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }
        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
