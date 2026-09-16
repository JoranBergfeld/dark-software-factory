using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class FactorySupplementalHostTests
{
    internal const string Tenant = "11111111-1111-1111-1111-111111111111";
    internal const string Principal = "22222222-2222-2222-2222-222222222222";
    internal const string Client = "33333333-3333-3333-3333-333333333333";
    internal const string Operator = "44444444-4444-4444-4444-444444444444";
    internal const string Factory = "55555555-5555-5555-5555-555555555555";
    internal const string Identity = "/subscriptions/66666666-6666-6666-6666-666666666666/resourcegroups/factory/providers/microsoft.managedidentity/userassignedidentities/df-55555555555555-webiq";
    internal static readonly string Revision = new('a', 64);
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T20:00:00Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_payload_and_returned_reference_bounds_never_release_partial_ready_evidence(bool oversizedPayload)
    {
        var host = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        using var transport = new SupplementalTransport { WebPayload = JsonSerializer.Serialize(new { webResults = new[]
        {
            new { url = oversizedPayload ? "https://example.com" : "https://" + host,
                content = oversizedPayload ? new string('x', 256 * 1024) : "Public research." },
        } }) };
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner { Route = "/evidence" };
        await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], Environment(), new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential(), SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, runner.Status);
        var evidence = JsonNode.Parse(runner.Body)!;
        Assert.Equal("CapacityExceeded", (string?)evidence["proof"]!["reason"]);
        Assert.Empty(evidence["items"]!.AsArray());
        Assert.True(Encoding.UTF8.GetByteCount(runner.Body) <= 8192);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("v2-uri")]
    public async Task Wrong_caller_revision_or_native_identity_never_accesses_external_source(string scenario)
    {
        using var transport = new SupplementalTransport();
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner
        {
            CallerId = scenario == "caller" ? Principal : Operator,
            RequestRevision = scenario == "revision" ? new string('f', 64) : Revision,
        };
        await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], Environment(), new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential { IdentityId = scenario == "identity" ? Identity + "-foreign" : Identity,
                    Version = scenario == "v2-uri" ? "2.0" : "1.0" },
                SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(scenario == "caller" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict, runner.Status);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Foundry_evidence_preserves_real_extractive_fields_and_source_scoped_provenance()
    {
        using var transport = new FoundryTransport { Scenario = "evidence" };
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner { Route = "/evidence" };
        await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], FoundryEnvironment(), new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential { IdentityId = Identity.Replace("-webiq", "-foundryiq", StringComparison.Ordinal) },
                SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, runner.Status);
        var result = JsonNode.Parse(runner.Body)!;
        Assert.Equal("None", (string?)result["proof"]!["reason"]);
        Assert.Single(result["items"]!.AsArray());
        Assert.Contains("Public keyboard accessibility.", (string)result["items"]![0]!["content"]!);
        Assert.StartsWith("foundryiq:https://product-search.search.windows.net#product-kb/product-docs/",
            (string)result["items"]![0]!["reference"]!);
        Assert.DoesNotContain("provider-document-id", runner.Body);
        Assert.Equal(11, transport.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticated_evidence_surface_returns_real_content_but_never_provider_key_echoes(bool echo)
    {
        using var transport = new SupplementalTransport { WebPayload = JsonSerializer.Serialize(new { webResults = new[]
        {
            new { url = "https://example.com/research?access_token=private-provider-detail", title = "Research",
                content = echo ? "secret-key-do-not-print" : "Public keyboard accessibility guidance for retail checkout." },
        } }) };
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner { Route = "/evidence" };
        await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], Environment(), new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential(), SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(echo ? HttpStatusCode.Conflict : HttpStatusCode.OK, runner.Status);
        var evidence = JsonNode.Parse(runner.Body)!;
        Assert.Equal(echo ? "Blocked" : "ReadySupplemental", (string?)evidence["proof"]!["state"]);
        if (echo) Assert.Empty(evidence["items"]!.AsArray());
        else Assert.Equal("Public keyboard accessibility guidance for retail checkout.", (string?)evidence["items"]![0]!["content"]);
        Assert.DoesNotContain("secret-key-do-not-print", runner.Body);
        Assert.DoesNotContain("private-provider-detail", runner.Body);
    }

    [Theory]
    [InlineData("wrong-secret-scope")]
    [InlineData("private-query")]
    [InlineData("cross-factory-identity")]
    public async Task Unapproved_host_configuration_never_reads_a_secret_or_sends_a_query(string scenario)
    {
        var env = Environment();
        var config = JsonNode.Parse(env["DSF_SUPPLEMENTAL_CONFIG"]!)!;
        if (scenario == "wrong-secret-scope") config["secretUri"] = "https://owner.vault.azure.net/secrets/github-app-private-key/version";
        if (scenario == "private-query") config["source"]!["publicQueryApproved"] = false;
        if (scenario == "cross-factory-identity") config["sourceIdentityId"] = Identity.Replace("55555555555555", "aaaaaaaaaaaaaa", StringComparison.Ordinal);
        env["DSF_SUPPLEMENTAL_CONFIG"] = config.ToJsonString();
        using var transport = new SupplementalTransport();
        using var http = new HttpClient(transport);
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], env, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = new SupplementalRunner(), SourceHttp = http,
                SourceCredential = new SupplementalCredential(), SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(1, exit);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("""{"webResults":[],"nextLink":"https://provider.example/secret-key-do-not-print"}""", 200, "Incomplete")]
    [InlineData("""{"webResults":[],"partial":true}""", 200, "Incomplete")]
    [InlineData("""{"webResults":[{"url":"https://user:secret-key-do-not-print@example.com","content":"data"}]}""", 200, "MalformedResponse")]
    [InlineData("""{"webResults":[]}""", 403, "PermissionDenied")]
    [InlineData("""{"webResults":[]}""", 206, "Incomplete")]
    public async Task Partial_denied_or_malformed_web_responses_block_selected_source_without_secret_echo(string payload, int status, string reason)
    {
        using var transport = new SupplementalTransport { WebPayload = payload, WebStatus = (HttpStatusCode)status };
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner();
        await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], Environment(), new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential(), SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, runner.Status);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal("Blocked", (string?)proof["state"]);
        Assert.Equal(reason, (string?)proof["reason"]);
        Assert.Empty(proof["records"]!.AsArray());
        Assert.DoesNotContain("secret-key-do-not-print", runner.Body);
    }

    [Fact]
    public async Task Real_foundry_workload_validates_existing_knowledge_base_and_exact_sources_without_model_or_crawl_requests()
    {
        var env = FoundryEnvironment();
        var foundryIdentity = Identity.Replace("-webiq", "-foundryiq", StringComparison.Ordinal);
        using var transport = new FoundryTransport();
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner();
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], env, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential { IdentityId = foundryIdentity }, SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.True(runner.Status == HttpStatusCode.OK, runner.Body);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal("ReadySupplemental", (string?)proof["state"]);
        Assert.Equal("Empty", (string?)proof["reason"]);
        Assert.Equal(11, transport.Requests.Count);
        Assert.DoesNotContain("secret-key", runner.Body);
    }

    [Theory]
    [InlineData("missing-kb", "MissingKnowledgeBase")]
    [InlineData("denied-data", "PermissionDenied")]
    [InlineData("partial", "Incomplete")]
    [InlineData("other-index", "AuthorityChanged")]
    [InlineData("other-kb", "AuthorityChanged")]
    [InlineData("web-source", "Unsupported")]
    [InlineData("changed-source", "Unsupported")]
    [InlineData("unsupported-activity", "Unsupported")]
    [InlineData("partial-metadata", "Incomplete")]
    public async Task Foundry_requires_complete_current_native_knowledge_and_source_specific_data_authority(string scenario, string reason)
    {
        using var transport = new FoundryTransport { Scenario = scenario };
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner();
        await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], FoundryEnvironment(), new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential { IdentityId = Identity.Replace("-webiq", "-foundryiq", StringComparison.Ordinal) },
                SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, runner.Status);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal("Blocked", (string?)proof["state"]);
        Assert.Equal(reason, (string?)proof["reason"]);
        Assert.Empty(proof["records"]!.AsArray());
        Assert.DoesNotContain("secret-provider-detail", runner.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_revoked_product_secret_is_never_replaced_by_environment_or_owner_key(bool revoke)
    {
        using var transport = new SupplementalTransport { MissingSecret = !revoke, RevokeSecret = revoke };
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner();
        await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], Environment(), new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SupplementalCredential(), SourceClock = new SupplementalClock() }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, runner.Status);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal(revoke ? "PermissionDenied" : "MissingSecret", (string?)proof["reason"]);
        Assert.Empty(proof["records"]!.AsArray());
        Assert.Equal(revoke ? 3 : 1, transport.Requests.Count);
    }

    internal static Dictionary<string, string?> FoundryEnvironment()
    {
        var env = Environment();
        var configuration = JsonNode.Parse(env["DSF_SUPPLEMENTAL_CONFIG"]!)!;
        var foundryIdentity = Identity.Replace("-webiq", "-foundryiq", StringComparison.Ordinal);
        configuration["sourceIdentityId"] = foundryIdentity;
        configuration["secretUri"] = null;
        configuration["source"] = new JsonObject
        {
            ["kind"] = "foundryiq", ["query"] = "Public retail accessibility research", ["publicQueryApproved"] = true,
            ["searchResourceId"] = "/subscriptions/66666666-6666-6666-6666-666666666666/resourcegroups/knowledge/providers/microsoft.search/searchservices/product-search",
            ["knowledgeBase"] = "product-kb", ["knowledgeBaseETag"] = "kb-v1", ["dedicatedSearchService"] = true,
            ["metadataRoleDefinitionId"] = "/subscriptions/66666666-6666-6666-6666-666666666666/providers/microsoft.authorization/roledefinitions/77777777-7777-7777-7777-777777777777",
            ["knowledgeSources"] = new JsonArray(new JsonObject
            {
                ["name"] = "product-docs", ["indexName"] = "product-index", ["sourceETag"] = "source-v1", ["indexETag"] = "index-v1",
            }),
        };
        env["DSF_SUPPLEMENTAL_CONFIG"] = configuration.ToJsonString();
        return env;
    }

    [Fact]
    public async Task Real_web_workload_reads_only_versioned_product_secret_and_returns_honest_empty_supplemental_proof()
    {
        using var transport = new SupplementalTransport();
        using var http = new HttpClient(transport);
        var runner = new SupplementalRunner();
        var error = new StringWriter();
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-supplemental"], Environment(), new StringWriter(), error,
            RuntimeDependencies.Production() with
            {
                WebHostRunner = runner, SourceHttp = http, SourceCredential = new SupplementalCredential(),
                SourceClock = new SupplementalClock(),
            }, CancellationToken.None);

        Assert.True(exit == 0, error.ToString());
        Assert.Equal(HttpStatusCode.OK, runner.Status);
        var proof = JsonNode.Parse(runner.Body)!;
        Assert.Equal("ReadySupplemental", (string?)proof["state"]);
        Assert.Equal("Empty", (string?)proof["reason"]);
        Assert.Empty(proof["records"]!.AsArray());
        Assert.Equal(3, transport.Requests.Count);
        Assert.DoesNotContain("secret-key-do-not-print", runner.Body);
        Assert.DoesNotContain("WEBIQ_API_KEY", runner.Body);
    }

    internal static Dictionary<string, string?> Environment() => new()
    {
        ["DSF_PRODUCT"] = "widget", ["AZURE_APPCONFIG_ENDPOINT"] = "https://dsfo-" + Factory.Replace("-", "") + ".azconfig.io",
        ["DSF_FACTORY_PROFILE_PIN"] = new string('b', 64), ["AZURE_CLIENT_ID"] = Client,
        ["WEBIQ_API_KEY"] = "unapproved-environment-key",
        ["DSF_SUPPLEMENTAL_CONFIG"] = JsonSerializer.Serialize(new
        {
            factoryId = Factory, tenantId = Tenant, operatorPrincipalId = Operator,
            sourceIdentityId = Identity, sourcePrincipalId = Principal, sourceClientId = Client, configurationRevision = Revision,
            source = new { kind = "webiq", query = "Public retail accessibility research", publicQueryApproved = true,
                secretName = "webiq-api-key", secretVersion = new string('c', 32) },
            secretUri = "https://df-55555555555555-vault.vault.azure.net/secrets/webiq-api-key/" + new string('c', 32),
        }),
    };

    internal sealed class SupplementalClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    internal sealed class SupplementalCredential : TokenCredential
    {
        public string IdentityId { get; init; } = Identity;
        public string Version { get; init; } = "1.0";
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => GetTokenAsync(context, token).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
        {
            var claims = new JsonObject
            {
                ["ver"] = Version, ["oid"] = Principal, ["tid"] = Tenant, [Version == "2.0" ? "azp" : "appid"] = Client,
                ["xms_mirid"] = IdentityId, ["aud"] = context.Scopes.Single()[..^"/.default".Length],
                ["nbf"] = Now.AddMinutes(-1).ToUnixTimeSeconds(), ["exp"] = Now.AddMinutes(30).ToUnixTimeSeconds(),
            }.ToJsonString();
            return ValueTask.FromResult(new AccessToken("header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)) + ".signature", Now.AddMinutes(30)));
        }
    }

    internal sealed class FoundryTransport : HttpMessageHandler
    {
        public string Scenario { get; init; } = "";
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("product-search.search.windows.net", request.RequestUri!.Host);
            Assert.Equal("?api-version=2026-04-01", request.RequestUri.Query);
            Requests.Add(request.Method + " " + request.RequestUri.AbsoluteUri);
            if (Scenario == "missing-kb") return Json(HttpStatusCode.NotFound, """{"error":"secret-provider-detail"}""");
            if (request.Method == HttpMethod.Get)
            {
                var body = JsonNode.Parse(request.RequestUri.AbsolutePath switch
                {
                    "/knowledgebases" => """{"value":[{"name":"product-kb"}]}""",
                    "/knowledgesources" => """{"value":[{"name":"product-docs"}]}""",
                    "/knowledgebases('product-kb')" => """{"name":"product-kb","@odata.etag":"kb-v1","outputMode":"extractiveData","knowledgeSources":[{"name":"product-docs"}]}""",
                    "/knowledgesources('product-docs')" => """{"name":"product-docs","@odata.etag":"source-v1","kind":"searchIndex","searchIndexParameters":{"searchIndexName":"product-index"}}""",
                    "/indexes" => """{"value":[{"name":"product-index","@odata.etag":"index-v1","fields":[{"name":"id","type":"Edm.String","key":true}]}]}""",
                    _ => throw new Xunit.Sdk.XunitException("Unexpected metadata path: " + request.RequestUri.AbsolutePath),
                })!;
                if (request.RequestUri.AbsolutePath.StartsWith("/knowledgesources", StringComparison.Ordinal))
                {
                    if (Scenario == "web-source") body["kind"] = "web";
                    if (Scenario == "changed-source" && Requests.Count > 5) body["@odata.etag"] = "replacement";
                }
                if (Scenario == "other-index" && request.RequestUri.AbsolutePath == "/indexes")
                    body["value"]!.AsArray().Add(new JsonObject { ["name"] = "another-product", ["@odata.etag"] = "other" });
                if (Scenario == "other-kb" && request.RequestUri.AbsolutePath == "/knowledgebases")
                    body["value"]!.AsArray().Add(new JsonObject { ["name"] = "another-product" });
                if (Scenario == "partial-metadata" && request.RequestUri.AbsolutePath == "/indexes")
                    body["continuationToken"] = "secret-provider-detail";
                return Json(HttpStatusCode.OK, body.ToJsonString());
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/knowledgebases('product-kb')/retrieve", request.RequestUri.AbsolutePath);
            var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("semantic", (string?)query["intents"]![0]!["type"]);
            Assert.True((bool)query["includeActivity"]!);
            var retrieval = JsonNode.Parse("""{"response":[],"activity":[{"id":1,"type":"searchIndex","knowledgeSourceName":"product-docs","count":0}],"references":[]}""")!;
            if (Scenario == "unsupported-activity") retrieval["activity"]![0]!["type"] = "web";
            if (Scenario == "evidence")
            {
                retrieval["response"]!.AsArray().Add(new JsonObject { ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text", ["text"] = """[{"ref_id":"0","guidance":"Public keyboard accessibility."}]""",
                }) });
                retrieval["references"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = "0", ["type"] = "searchIndex", ["activitySource"] = 1, ["docKey"] = "provider-document-id",
                });
            }
            return Json(Scenario == "denied-data" ? HttpStatusCode.Forbidden : Scenario == "partial" ? HttpStatusCode.PartialContent : HttpStatusCode.OK,
                retrieval.ToJsonString());
        }
    }

    internal sealed class SupplementalTransport : HttpMessageHandler
    {
        public string WebPayload { get; init; } = """{"webResults":[]}""";
        public HttpStatusCode WebStatus { get; init; } = HttpStatusCode.OK;
        public bool MissingSecret { get; init; }
        public bool RevokeSecret { get; init; }
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Host == "df-55555555555555-vault.vault.azure.net")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/secrets/webiq-api-key/" + new string('c', 32), request.RequestUri.AbsolutePath);
                if (MissingSecret || RevokeSecret && Requests.Count > 1)
                    return Json(MissingSecret ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, """{"error":"secret-provider-detail"}""");
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = "secret-key-do-not-print",
                    id = request.RequestUri.GetLeftPart(UriPartial.Path), attributes = new { enabled = true,
                        created = Now.AddDays(-1).ToUnixTimeSeconds(), updated = Now.AddDays(-1).ToUnixTimeSeconds(),
                        nbf = Now.AddDays(-1).ToUnixTimeSeconds(), exp = Now.AddDays(1).ToUnixTimeSeconds() } }));
            }
            Assert.Equal("https://api.microsoft.ai/v3/search/web", request.RequestUri.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("secret-key-do-not-print", request.Headers.GetValues("x-apikey").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("Public retail accessibility research", (string?)body["query"]);
            Assert.Equal("text", (string?)body["contentFormat"]);
            return Json(WebStatus, WebPayload);
        }
    }

    internal sealed class SupplementalRunner : IWebHostRunner
    {
        public string Route { get; init; } = "/proof";
        public string CallerId { get; init; } = Operator;
        public string RequestRevision { get; init; } = Revision;
        public HttpStatusCode Status { get; private set; }
        public string Body { get; private set; } = "";
        public async Task RunAsync(WebApplication app, CancellationToken cancellationToken)
        {
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            app.Use(async (context, next) =>
            {
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] = "aad";
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = CallerId;
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { auth_typ = "aad", claims = new[]
                    {
                        new { typ = "http://schemas.microsoft.com/identity/claims/objectidentifier", val = CallerId },
                        new { typ = "http://schemas.microsoft.com/identity/claims/tenantid", val = Tenant },
                    } })));
                await next(context);
            });
            await app.StartAsync(cancellationToken);
            try
            {
                using var http = new HttpClient();
                using var response = await http.PostAsJsonAsync(app.Urls.Single() + Route,
                    new { checkId = Guid.NewGuid(), configurationRevision = RequestRevision }, cancellationToken);
                Status = response.StatusCode;
                Body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            finally { await app.StopAsync(CancellationToken.None); }
        }
    }
    internal static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
