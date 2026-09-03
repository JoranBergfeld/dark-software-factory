using System.Net;
using System.Text.Json;
using Dsf.Cli;
using Dsf.Core.Charters;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CosmosCharterStoreTests
{
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
        string Path,
        string? Body,
        string? Authorization,
        string? PartitionKey,
        string? IsUpsert);
}
