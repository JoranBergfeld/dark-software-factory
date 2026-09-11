using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.Query;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class VendorSourceProtocolTests
{
    // Microsoft Learn's exact Log Analytics table/column/row example, not a live recording:
    // https://learn.microsoft.com/azure/azure-monitor/logs/api/response-format
    private const string AzureMonitorResponse = """
        {"tables":[{"name":"PrimaryResult","columns":[
          {"name":"Category","type":"string"},{"name":"count_","type":"long"}
        ],"rows":[["Administrative",20839],["Recommendation",122],["Alert",64],["ServiceHealth",11]]}]}
        """;

    private const string ProjectedAzureMonitorResponse = """
        {"tables":[{"name":"PrimaryResult","columns":[
          {"name":"Reference","type":"string"},{"name":"Summary","type":"string"}
        ],"rows":[["Administrative","20839 events"],["Recommendation","122 events"]]}]}
        """;

    [Fact]
    public async Task Azuremonitor_gather_sends_KQL_and_maps_the_actual_SDK_response()
    {
        var credential = new VendorCredential();
        using var handler = new VendorHttpHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://api.loganalytics.io/v1/workspaces/00000000-0000-0000-0000-000000000001/query",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("AzureActivity | summarize count() by Category | project Reference=Category, Summary=strcat(count_, ' events')",
                body.RootElement.GetProperty("query").GetString());
            Assert.Equal(TimeSpan.FromHours(24),
                System.Xml.XmlConvert.ToTimeSpan(body.RootElement.GetProperty("timespan").GetString()!));
            return JsonResponse(ProjectedAzureMonitorResponse);
        });
        using var httpClient = new HttpClient(handler);
        var integration = MonitorIntegration(httpClient, credential);

        var evidence = await integration.GatherAsync("azuremonitor", "acme", CancellationToken.None);

        Assert.Collection(evidence,
            item => Assert.Equal(new EvidenceItem("azuremonitor", "Administrative", "20839 events"), item),
            item => Assert.Equal(new EvidenceItem("azuremonitor", "Recommendation", "122 events"), item));
        Assert.Equal(["https://api.loganalytics.io//.default"], credential.RequestedScopes);
    }

    [Theory]
    [InlineData(200, """
        {"tables":[{"name":"PrimaryResult","columns":[{"name":"Reference","type":"string"},{"name":"Summary","type":"string"}],
          "rows":[["AM-1","partial"]]}],
          "error":{"code":"PartialError","message":"Some data could not be returned.",
          "details":[{"code":"PartialQueryFailure","message":"Query execution has exceeded the allowed limits."}]}}
        """)]
    [InlineData(403, """{"error":{"code":"InsufficientAccessError","message":"The caller is not authorized."}}""")]
    [InlineData(200, """
        {"tables":[{"name":"PrimaryResult","columns":[{"name":"Reference","type":"string"},{"name":"Summary","type":"string"}],
          "rows":[["AM-1",null]]}]}
        """)]
    public async Task Azuremonitor_gather_rejects_partial_query_errors_and_unusable_evidence(int status, string payload)
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(payload, (HttpStatusCode)status)));
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MonitorIntegration(httpClient).GatherAsync("azuremonitor", "acme", CancellationToken.None));
    }

    private static AzureMonitorIntegration MonitorIntegration(HttpClient httpClient, TokenCredential? credential = null) =>
        new(
            new Dictionary<string, string?>
            {
                ["DSF_AZUREMONITOR_WORKSPACE_ID"] = "00000000-0000-0000-0000-000000000001",
                ["DSF_AZUREMONITOR_QUERY"] = "AzureActivity | summarize count() by Category | project Reference=Category, Summary=strcat(count_, ' events')",
            },
            new AzureMonitorLogsGateway(new LogsQueryClient(credential ?? new VendorCredential(),
                new LogsQueryClientOptions { Transport = new HttpClientTransport(httpClient), Retry = { MaxRetries = 0 } })));

    [Fact]
    public async Task Azuremonitor_gather_requires_explicit_evidence_projection_instead_of_dropping_vendor_rows()
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(AzureMonitorResponse)));
        using var httpClient = new HttpClient(handler);
        var client = new LogsQueryClient(new VendorCredential(),
            new LogsQueryClientOptions { Transport = new HttpClientTransport(httpClient), Retry = { MaxRetries = 0 } });
        var integration = new AzureMonitorIntegration(
            new Dictionary<string, string?>
            {
                ["DSF_AZUREMONITOR_WORKSPACE_ID"] = "00000000-0000-0000-0000-000000000001",
                ["DSF_AZUREMONITOR_QUERY"] = "AzureActivity | summarize count() by Category",
            },
            new AzureMonitorLogsGateway(client));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => integration.GatherAsync("azuremonitor", "acme", CancellationToken.None));

        Assert.Contains("DSF_AZUREMONITOR_QUERY", error.Message);
        Assert.Contains("Reference", error.Message);
        Assert.Contains("Summary", error.Message);
    }

    // Synthetic payload following Microsoft's webiq 0.1.8 wheel:
    // webiq/resources/__init__.py, auth.py, types/generated/models.py (WebResponse/WebResult).
    // https://pypi.org/project/webiq/0.1.8/ ; not a recording or a live-service test.
    // Wheel SHA-256: 5d927edb46cf2742e7c5ca54683c205e724b6e51285d4f4b8712e56e18a4e913.
    private const string WebIqResponse = """
        {
          "webResults": [{
            "title": "Example research",
            "url": "https://example.com/research",
            "content": "Customers report slow checkout.",
            "lastUpdatedAt": "2026-09-01T12:00:00Z",
            "language": "en",
            "isAdult": false
          }],
          "querySignals": { "freshness": true },
          "traceId": "fixture-trace"
        }
        """;

    // Adapted (text shortened) from Microsoft's retrieve guide, GA 2026-04-01:
    // https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-retrieve#review-the-response
    // The semantic fields inside content.text belong to the operator's index, not a vendor DTO.
    private const string FoundryIqResponse = """
        {
          "response": [{
            "role": "assistant",
            "content": [{
              "type": "text",
              "text": "[{\"ref_id\":\"0\",\"title\":\"Urban Structure\",\"terms\":\"Phoenix\",\"content\":\"City blocks form a visible grid at night.\"}]"
            }]
          }],
          "activity": [{
            "type": "searchIndex",
            "id": 2,
            "knowledgeSourceName": "earth-ks",
            "queryTime": "2025-11-04T19:25:23.683Z",
            "count": 1,
            "elapsedMs": 1137,
            "searchIndexArguments": {
              "search": "Phoenix at night",
              "sourceDataFields": [],
              "searchFields": [],
              "semanticConfigurationName": "en-semantic-config"
            }
          }],
          "references": [{
            "type": "searchIndex",
            "id": "0",
            "activitySource": 2,
            "docKey": "earth_at_night_508_page_104_verbalized",
            "sourceData": null
          }]
        }
        """;

    [Fact]
    public async Task Foundryiq_gather_retrieves_from_Azure_Search_and_preserves_grounding_and_document_identity()
    {
        var credential = new VendorCredential();
        using var handler = new VendorHttpHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://acme-search.search.windows.net/knowledgebases('roadmap')/retrieve?api-version=2026-04-01",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var intent = Assert.Single(body.RootElement.GetProperty("intents").EnumerateArray());
            Assert.Equal("semantic", intent.GetProperty("type").GetString());
            Assert.Equal("Phoenix at night", intent.GetProperty("search").GetString());
            Assert.True(body.RootElement.GetProperty("includeActivity").GetBoolean());
            return JsonResponse(FoundryIqResponse);
        });
        using var client = new HttpClient(handler);
        var integration = new FoundryIqIntegration(
            new Dictionary<string, string?>
            {
                ["DSF_FOUNDRYIQ_SEARCH_ENDPOINT"] = "https://acme-search.search.windows.net",
                ["DSF_FOUNDRYIQ_KNOWLEDGE_BASE"] = "roadmap",
                ["DSF_FOUNDRYIQ_QUERY"] = "Phoenix at night",
            },
            new FoundryIqKnowledgeGateway(credential, client));

        var evidence = await integration.GatherAsync("foundryiq", "acme", CancellationToken.None);

        Assert.Equal(["https://search.azure.com/.default"], credential.RequestedScopes);
        var item = Assert.Single(evidence);
        Assert.Equal("foundryiq", item.SourceKind);
        Assert.Contains("earth_at_night_508_page_104_verbalized", item.Reference);
        Assert.Contains("earth-ks", item.Reference);
        Assert.Contains("roadmap", item.Reference);
        Assert.Contains("City blocks form a visible grid at night.", item.Summary);
        Assert.Contains("Phoenix", item.Summary);
    }

    [Theory]
    [InlineData(206, FoundryIqResponse)]
    [InlineData(403, """{"error":{"code":"Forbidden","message":"Access denied."}}""")]
    [InlineData(200, "{}")]
    [InlineData(200, """{"response":[],"activity":[{"id":1,"type":"searchIndex","error":{"code":"403"}}],"references":[]}""")]
    [InlineData(200, """{"response":null,"activity":[],"references":[]}""")]
    [InlineData(200, """{"response":[{"content":[{"type":"text","text":"not json"}]}],"activity":[],"references":[]}""")]
    [InlineData(200, """{"response":[{"content":[{"type":"text","text":"[{\"ref_id\":\"0\",\"text\":\"untraceable\"}]"}]}],"activity":[],"references":[]}""")]
    public async Task Foundryiq_gather_rejects_partial_failed_or_ungrounded_responses(int status, string payload)
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(payload, (HttpStatusCode)status)));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FoundryIntegration(client).GatherAsync("foundryiq", "acme", CancellationToken.None));
    }

    [Fact]
    public async Task Foundryiq_gather_accepts_an_explicit_empty_retrieval()
    {
        using var handler = new VendorHttpHandler(_ =>
            Task.FromResult(JsonResponse("""{"response":[],"activity":[],"references":[]}""")));
        using var client = new HttpClient(handler);

        Assert.Empty(await FoundryIntegration(client).GatherAsync("foundryiq", "acme", CancellationToken.None));
    }

    [Theory]
    [InlineData("azureBlob", "blobUrl", "https://storage.blob.core.windows.net/docs/policy.txt")]
    [InlineData("indexedOneLake", "docUrl", "https://onelake.dfs.fabric.microsoft.com/workspace/policy.txt")]
    [InlineData("web", "url", "https://example.com/policy")]
    public async Task Foundryiq_gather_maps_all_GA_reference_variants(string type, string urlField, string url)
    {
        var payload = JsonSerializer.Serialize(new
        {
            response = new[] { new { content = new[] { new { type = "text", text = """[{"ref_id":"0","policyText":"Grounded policy."}]""" } } } },
            activity = new[] { new { id = 0, type, knowledgeSourceName = "policies" } },
            references = new[] { new Dictionary<string, object> { ["id"] = "0", ["type"] = type, ["activitySource"] = 0, [urlField] = url } },
        });
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(payload)));
        using var client = new HttpClient(handler);

        var evidence = await FoundryIntegration(client).GatherAsync("foundryiq", "acme", CancellationToken.None);

        Assert.Equal(url, Assert.Single(evidence).Reference);
        Assert.Contains("Grounded policy.", evidence[0].Summary);
    }

    private static FoundryIqIntegration FoundryIntegration(HttpClient client) =>
        new(
            new Dictionary<string, string?>
            {
                ["DSF_FOUNDRYIQ_SEARCH_ENDPOINT"] = "https://acme-search.search.windows.net",
                ["DSF_FOUNDRYIQ_KNOWLEDGE_BASE"] = "roadmap",
                ["DSF_FOUNDRYIQ_QUERY"] = "Phoenix at night",
            },
            new FoundryIqKnowledgeGateway(new VendorCredential(), client));

    [Theory]
    [InlineData("""{"kind":"foundryiq","product":"acme","evidence":[]}""")]
    [InlineData("""{"kind":"webiq","product":"other-product","evidence":[]}""")]
    [InlineData("""{"kind":"webiq","product":"acme","evidence":[{"sourceKind":"webiq","summary":"missing reference"}]}""")]
    [InlineData("""{"kind":"webiq","product":"acme","evidence":[{"sourceKind":"foundryiq","reference":"r","summary":"wrong kind"}]}""")]
    [InlineData("""{"kind":"webiq","product":"acme","evidence":[{"sourceKind":"webiq","reference":"r","summary":""}]}""")]
    public async Task Served_gather_rejects_misrouted_or_malformed_agent_evidence(string payload)
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(payload)));
        using var client = new HttpClient(handler);
        var gatherer = new SourceAgentEvidenceGatherer("webiq", "acme", new Uri("https://agent.example"), client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gatherer.GatherAsync(new ConveyorRun { ProductHints = ["acme"] }, CancellationToken.None));
    }

    [Fact]
    public async Task Registry_prefers_a_typed_adapter_over_an_explicit_generic_fallback()
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(ProjectedAzureMonitorResponse)));
        using var client = new HttpClient(handler);
        var registry = new SourceIntegrationRegistry(
            new Dictionary<string, ISourceIntegration> { [" AZUREMONITOR "] = MonitorIntegration(client) },
            new HttpSourceIntegration(new Dictionary<string, string?>()));

        var evidence = await registry.Resolve("azuremonitor").GatherAsync("azuremonitor", "acme", CancellationToken.None);

        Assert.Equal(2, evidence.Count);
        Assert.Equal("Administrative", evidence[0].Reference);
    }

    [Fact]
    public async Task Registry_allows_an_explicit_generic_fallback_for_known_kinds_only()
    {
        using var handler = new VendorHttpHandler(_ =>
            Task.FromResult(JsonResponse("""[{"reference":"generic-1","summary":"generic evidence"}]""")));
        using var client = new HttpClient(handler);
        var registry = new SourceIntegrationRegistry(
            new Dictionary<string, ISourceIntegration>(),
            new HttpSourceIntegration(
                new Dictionary<string, string?> { ["DSF_SOURCE_WEBIQ_ENDPOINT"] = "https://source.example" }, client));

        var evidence = await registry.Resolve("webiq").GatherAsync("webiq", "acme", CancellationToken.None);

        Assert.Equal("generic-1", Assert.Single(evidence).Reference);
        Assert.Throws<RuntimeConfigurationException>(() => registry.Resolve("sentry"));
        Assert.Throws<RuntimeConfigurationException>(() => registry.Resolve("grafana"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentHost_entry_serves_health_card_and_typed_evidence(bool explicitVerb)
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(WebIqResponse)));
        using var client = new HttpClient(handler);
        var env = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "acme",
            ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
            ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example",
            ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt",
            ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embedding",
            ["DSF_WEBIQ_QUERY"] = "research",
            ["WEBIQ_API_KEY"] = "fixture-key",
        };
        var runner = new SourceHostRunner();
        var dependencies = RuntimeDependencies.Production(env) with
        {
            WebHostRunner = runner,
            SourceIntegrationRegistry = new SourceIntegrationRegistry(new Dictionary<string, ISourceIntegration>
            {
                ["webiq"] = new WebIqIntegration(env, new WebIqSearchGateway(client)),
            }),
        };
        string[] options = ["--kind", "webiq", "--host", "127.0.0.1", "--port", "0"];
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Dsf.AgentHost.AgentHostApplication.InvokeAsync(
            explicitVerb ? ["serve-agent", .. options] : options, env, stdout, stderr, dependencies, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Equal("https://example.com/research", Assert.Single(runner.Evidence).Reference);
    }

    private sealed class SourceHostRunner : IWebHostRunner
    {
        public IReadOnlyList<EvidenceItem> Evidence { get; private set; } = [];

        public async Task RunAsync(WebApplication app, CancellationToken cancellationToken)
        {
            await using var host = app;
            await app.StartAsync(cancellationToken);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                using var health = await client.GetAsync("/healthz", cancellationToken);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                var card = await client.GetFromJsonAsync<JsonElement>(SourceAgentCard.CardRoute, cancellationToken);
                Assert.Equal("webiq", card.GetProperty("kind").GetString());
                var gatherer = new SourceAgentEvidenceGatherer("webiq", "acme", client.BaseAddress, client);
                Evidence = await gatherer.GatherAsync(
                    new ConveyorRun { SourceKinds = ["webiq"], ProductHints = ["acme"] }, cancellationToken);
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Reusable_host_runner_stops_when_its_caller_cancels()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = app.Lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var cancellation = new CancellationTokenSource();
        var running = new WebApplicationHostRunner().RunAsync(app, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await app.StopAsync();
            await running;
        }
    }

    [Fact]
    public async Task Webiq_gather_uses_the_Microsoft_SDK_wire_contract_including_the_v3_path()
    {
        using var handler = new VendorHttpHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.microsoft.ai/v3/search/web", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("x-apikey")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("checkout research", body.RootElement.GetProperty("query").GetString());
            Assert.Equal("text", body.RootElement.GetProperty("contentFormat").GetString());
            return JsonResponse(WebIqResponse);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.microsoft.ai/v3") };
        var integration = new WebIqIntegration(
            new Dictionary<string, string?>
            {
                ["DSF_WEBIQ_QUERY"] = "checkout research",
                ["WEBIQ_API_KEY"] = "fixture-key",
            },
            new WebIqSearchGateway(client));

        var evidence = await integration.GatherAsync("webiq", "acme", CancellationToken.None);

        var item = Assert.Single(evidence);
        Assert.Equal("webiq", item.SourceKind);
        Assert.Equal("https://example.com/research", item.Reference);
        Assert.Contains("Customers report slow checkout.", item.Summary);
    }

    [Theory]
    [InlineData(200, "{}")]
    [InlineData(200, """{"errorCode":"InternalError","userMessage":"failed"}""")]
    [InlineData(200, """{"webResults":[{"title":"untraceable"}]}""")]
    [InlineData(200, """{"webResults":[{"url":"https://example.com/empty"}]}""")]
    [InlineData(206, WebIqResponse)]
    [InlineData(429, """{"errorCode":"RateLimitExceeded","retryAfter":"60s","traceId":"fixture-trace"}""")]
    [InlineData(430, """{"errorCode":"ConcurrentRequestLimitExceeded","retryAfter":"30s"}""")]
    public async Task Webiq_gather_rejects_partial_failed_or_untraceable_results(int status, string payload)
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(payload, (HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        var integration = new WebIqIntegration(
            new Dictionary<string, string?> { ["DSF_WEBIQ_QUERY"] = "research", ["WEBIQ_API_KEY"] = "fixture-key" },
            new WebIqSearchGateway(client));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => integration.GatherAsync("webiq", "acme", CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"webResults":[]}""")]
    [InlineData("""{"webResults":null,"traceId":"fixture-trace"}""")]
    public async Task Webiq_gather_accepts_an_explicit_empty_result(string payload)
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(payload)));
        using var client = new HttpClient(handler);
        var integration = new WebIqIntegration(
            new Dictionary<string, string?> { ["DSF_WEBIQ_QUERY"] = "research", ["WEBIQ_API_KEY"] = "fixture-key" },
            new WebIqSearchGateway(client));

        Assert.Empty(await integration.GatherAsync("webiq", "acme", CancellationToken.None));
    }

    [Fact]
    public async Task Webiq_gather_uses_the_ADR0020_default_vault_secret_name()
    {
        var secretReader = new WebIqSecretReader();
        using var handler = new VendorHttpHandler(request =>
        {
            Assert.Equal("fixture-vault-key", Assert.Single(request.Headers.GetValues("x-apikey")));
            return Task.FromResult(JsonResponse(WebIqResponse));
        });
        using var client = new HttpClient(handler);
        var integration = new WebIqIntegration(
            new Dictionary<string, string?>
            {
                ["DSF_WEBIQ_QUERY"] = "research",
                ["AZURE_KEYVAULT_URI"] = "https://acme.vault.azure.net",
            },
            new WebIqSearchGateway(client), secretReader);

        Assert.Single(await integration.GatherAsync("webiq", "acme", CancellationToken.None));
        Assert.Equal("webiq-api-key", secretReader.SecretName);
    }

    [Fact]
    public async Task Webiq_gather_rejects_queries_beyond_the_SDK_limit_before_sending()
    {
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(WebIqResponse)));
        using var client = new HttpClient(handler);
        var integration = new WebIqIntegration(
            new Dictionary<string, string?> { ["DSF_WEBIQ_QUERY"] = new string('q', 1001), ["WEBIQ_API_KEY"] = "fixture-key" },
            new WebIqSearchGateway(client));

        var error = await Assert.ThrowsAsync<RuntimeConfigurationException>(() =>
            integration.GatherAsync("webiq", "acme", CancellationToken.None));

        Assert.Contains("DSF_WEBIQ_QUERY", error.Message);
    }

    [Fact]
    public async Task Foundryiq_gather_rejects_grounding_with_only_empty_semantic_fields()
    {
        var payload = JsonNode.Parse(FoundryIqResponse)!;
        payload["response"]![0]!["content"]![0]!["text"] = """[{"ref_id":"0","content":""}]""";
        using var handler = new VendorHttpHandler(_ => Task.FromResult(JsonResponse(payload.ToJsonString())));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FoundryIntegration(client).GatherAsync("foundryiq", "acme", CancellationToken.None));
    }

    private sealed class WebIqSecretReader : IPrivateKeySecretReader
    {
        public string? SecretName { get; private set; }

        public Task<string> GetSecretAsync(Uri vaultUri, string secretName, CancellationToken cancellationToken)
        {
            SecretName = secretName;
            return Task.FromResult("fixture-vault-key");
        }
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class VendorHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class VendorCredential : TokenCredential
    {
        public string[] RequestedScopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestedScopes = requestContext.Scopes;
            return new AccessToken("fixture-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
