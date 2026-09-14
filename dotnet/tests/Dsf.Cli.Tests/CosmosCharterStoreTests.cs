using System.Net;
using System.Text.Json;
using Dsf.Cli;
using Dsf.Core.Charters;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CosmosCharterStoreTests
{
    [Fact]
    public void Creating_the_default_store_does_not_access_Azure()
    {
        var runner = new RecordingAzureCliRunner();
        var handler = new StubHttpMessageHandler();
        using var client = new HttpClient(handler);

        CosmosCharterStore.FromEnvironment(client, runner);

        Assert.Empty(runner.Invocations);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null, null, "published-db", "demo.documents.azure.com", "published-db")]
    [InlineData(" ", " ", "published-db", "demo.documents.azure.com", "published-db")]
    [InlineData("https://override.documents.azure.com/", null, "published-db", "override.documents.azure.com", "published-db")]
    [InlineData(null, "override-db", "published-db", "demo.documents.azure.com", "override-db")]
    [InlineData("https://override.documents.azure.com/", "override-db", "published-db", "override.documents.azure.com", "override-db")]
    [InlineData(null, null, null, "demo.documents.azure.com", "demo")]
    public async Task Owner_index_settings_are_used_unless_explicitly_overridden(
        string? endpointOverride,
        string? databaseOverride,
        string? publishedDatabase,
        string expectedHost,
        string expectedDatabase)
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.NotFound, "{}"));
        var runner = new RecordingAzureCliRunner(
            IndexResponse("demo", "https://demo.documents.azure.com/", publishedDatabase),
            new AzureCliInvocationResult(0, """{"accessToken":"cosmos-token"}""", ""));
        var store = IndexedStore(handler, runner, endpointOverride, databaseOverride);

        Assert.Null(await store.GetCharterAsync("demo", CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectedHost, request.Host);
        Assert.Equal($"/dbs/{expectedDatabase}/colls/charters/docs/demo", request.Path);
    }

    [Fact]
    public async Task Owner_index_resolution_keeps_products_isolated()
    {
        var handler = new StubHttpMessageHandler(
            Response(HttpStatusCode.NotFound, "{}"),
            Response(HttpStatusCode.NotFound, "{}"));
        var token = new AzureCliInvocationResult(0, """{"accessToken":"cosmos-token"}""", "");
        var runner = new RecordingAzureCliRunner(
            IndexResponse("one", "https://one.documents.azure.com/", "one-db"),
            token,
            IndexResponse("two", "https://two.documents.azure.com/", "two-db"),
            token);
        var store = IndexedStore(handler, runner);

        await store.GetCharterAsync("one", CancellationToken.None);
        await store.GetCharterAsync("two", CancellationToken.None);

        Assert.Equal(["one.documents.azure.com", "two.documents.azure.com"], handler.Requests.Select(r => r.Host));
        Assert.Equal(
            ["/dbs/one-db/colls/charters/docs/one", "/dbs/two-db/colls/charters/docs/two"],
            handler.Requests.Select(r => r.Path));
        Assert.Contains("one", runner.Invocations[0]);
        Assert.Contains("two", runner.Invocations[2]);
    }

    [Fact]
    public async Task Missing_index_endpoint_names_the_product_and_configuration_authority()
    {
        var handler = new StubHttpMessageHandler();
        var runner = new RecordingAzureCliRunner(IndexResponse("demo", null, "demo-db"));
        var store = IndexedStore(handler, runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCharterAsync("demo", CancellationToken.None));

        Assert.Contains("demo", error.Message, StringComparison.Ordinal);
        Assert.Contains("AZURE_COSMOS_ENDPOINT", error.Message, StringComparison.Ordinal);
        Assert.Contains("owner App Configuration runtime index", error.Message, StringComparison.Ordinal);
        Assert.Contains("dsf new", error.Message, StringComparison.Ordinal);
        Assert.Single(runner.Invocations);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0, "[]", "", "no published runtime index")]
    [InlineData(1, "", "access denied", "access denied")]
    public async Task Owner_lookup_failures_are_not_hidden_by_local_overrides(
        int exitCode, string output, string errorOutput, string expectedError)
    {
        var handler = new StubHttpMessageHandler();
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(exitCode, output, errorOutput));
        var store = IndexedStore(handler, runner, "https://override.documents.azure.com/", "override-db");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCharterAsync("demo", CancellationToken.None));

        Assert.Contains(expectedError, error.Message, StringComparison.Ordinal);
        Assert.Single(runner.Invocations);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_during_owner_lookup_does_not_access_Cosmos()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler();
        var runner = new RecordingAzureCliRunner(cancellation);
        var store = IndexedStore(handler, runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetCharterAsync("demo", cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    private static CosmosCharterStore IndexedStore(
        StubHttpMessageHandler handler,
        RecordingAzureCliRunner runner,
        string? endpoint = null,
        string? database = null) =>
        new(new HttpClient(handler), runner, endpoint, database,
            "https://owner.azconfig.io", new AzureCliAppConfigurationClient(runner));

    private static AzureCliInvocationResult IndexResponse(string product, string? endpoint, string? database)
    {
        var values = new Dictionary<string, string> { ["DSF_PRODUCT"] = product };
        if (endpoint is not null)
        {
            values["AZURE_COSMOS_ENDPOINT"] = endpoint;
        }
        if (database is not null)
        {
            values["DSF_COSMOS_DATABASE"] = database;
        }
        return new AzureCliInvocationResult(0, JsonSerializer.Serialize(
            values.Select(entry => new { key = entry.Key, value = entry.Value, label = product })), "");
    }

    [Fact]
    public async Task GetCharter_reads_the_product_document_from_the_charters_container()
    {
        var handler = new StubHttpMessageHandler(
            Response(
                HttpStatusCode.OK,
                """
                {
                  "id": "demo",
                  "product": "demo",
                  "stored": {
                    "product": "demo",
                    "repository": "acme/demo",
                    "status": "OK",
                    "sourceSha": "abc123",
                    "sourceRef": "main",
                    "content": "charter",
                    "lastSyncedAt": "2024-01-01T00:00:00+00:00"
                  }
                }
                """));
        var store = new CosmosCharterStore(
            new HttpClient(handler),
            new RecordingAzureCliRunner(new AzureCliInvocationResult(0, """{"accessToken":"cosmos-token"}""", "")),
            "https://demo.documents.azure.com:443/",
            "demo");

        var stored = await store.GetCharterAsync("demo", CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("acme/demo", stored!.Repository);
        Assert.Equal(CharterStatus.Ok, stored.Status);
        Assert.Equal("abc123", stored.SourceSha);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/dbs/demo/colls/charters/docs/demo", request.Path);
        Assert.Equal("[\"demo\"]", request.PartitionKey);
        Assert.Contains("cosmos-token", request.Authorization!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCharter_requests_an_Entra_token_for_the_fixed_cosmos_data_plane_resource()
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.NotFound, "{}"));
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, """{"accessToken":"cosmos-token"}""", ""));
        var store = new CosmosCharterStore(
            new HttpClient(handler),
            runner,
            "https://demo.documents.azure.com:443/",
            "demo");

        await store.GetCharterAsync("demo", CancellationToken.None);

        var invocation = Assert.Single(runner.Invocations);
        var scopeIndex = invocation.ToList().IndexOf("--scope");
        Assert.True(scopeIndex >= 0, "expected a --scope argument");
        Assert.Equal("https://cosmos.azure.com/.default", invocation[scopeIndex + 1]);
    }

    [Fact]
    public async Task GetCharter_returns_null_when_the_product_has_no_document()
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.NotFound, "{}"));
        var store = new CosmosCharterStore(
            new HttpClient(handler),
            new RecordingAzureCliRunner(new AzureCliInvocationResult(0, """{"accessToken":"cosmos-token"}""", "")),
            "https://demo.documents.azure.com:443/",
            "demo");

        Assert.Null(await store.GetCharterAsync("demo", CancellationToken.None));
    }

    [Fact]
    public async Task PutCharter_upserts_the_product_document()
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.OK, "{}"));
        var store = new CosmosCharterStore(
            new HttpClient(handler),
            new RecordingAzureCliRunner(new AzureCliInvocationResult(0, """{"accessToken":"cosmos-token"}""", "")),
            "https://demo.documents.azure.com:443/",
            "demo");

        await store.PutCharterAsync(
            new StoredCharter("demo", "acme/demo", null, CharterStatus.Missing, null, "main", null, null, "gone"),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/dbs/demo/colls/charters/docs", request.Path);
        Assert.Equal("true", request.IsUpsert);
        using var payload = JsonDocument.Parse(request.Body!);
        Assert.Equal("demo", payload.RootElement.GetProperty("id").GetString());
        Assert.Equal("acme/demo", payload.RootElement.GetProperty("stored").GetProperty("repository").GetString());
        Assert.Equal("MISSING", payload.RootElement.GetProperty("stored").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Missing_cosmos_endpoint_fails_loudly_naming_the_setting()
    {
        var handler = new StubHttpMessageHandler();
        var store = new CosmosCharterStore(
            new HttpClient(handler),
            new RecordingAzureCliRunner(),
            endpoint: null,
            database: "demo");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCharterAsync("demo", CancellationToken.None));

        Assert.Contains("AZURE_COSMOS_ENDPOINT", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Failed_cosmos_requests_fail_loudly_with_the_status_and_detail()
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.Forbidden, "no data-plane role"));
        var store = new CosmosCharterStore(
            new HttpClient(handler),
            new RecordingAzureCliRunner(new AzureCliInvocationResult(0, """{"accessToken":"cosmos-token"}""", "")),
            "https://demo.documents.azure.com:443/",
            "demo");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetCharterAsync("demo", CancellationToken.None));

        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        Assert.Contains("no data-plane role", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<RecordedCosmosRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(
                new RecordedCosmosRequest(
                    request.Method,
                    request.RequestUri!.Host,
                    request.RequestUri!.PathAndQuery,
                    request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                    Header(request, "Authorization"),
                    Header(request, "x-ms-documentdb-partitionkey"),
                    Header(request, "x-ms-documentdb-is-upsert")));
            return responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("No response configured.");
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.First() : null;
    }

    private sealed record RecordedCosmosRequest(
        HttpMethod Method,
        string Host,
        string Path,
        string? Body,
        string? Authorization,
        string? PartitionKey,
        string? IsUpsert);
}
