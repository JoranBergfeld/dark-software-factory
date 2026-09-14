using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class CosmosSweepLeaseGatewayTests
{
    [Fact]
    public async Task Independent_workers_use_atomic_Cosmos_creation_and_owner_conditional_release()
    {
        using var handler = new LeaseHandler();
        using var client = new HttpClient(handler);
        var gateway = new AzureCosmosDocumentGateway(new LeaseCredential(), client);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway).TryAcquireAsync(
                "acme", DateTimeOffset.UnixEpoch.AddDays(index), TimeSpan.FromSeconds(index + 1),
                CancellationToken.None)));

        var winner = Assert.Single(attempts, attempt => attempt is not null);
        Assert.NotNull(winner);
        Assert.Equal(15, attempts.Count(attempt => attempt is null));
        Assert.Equal(16, handler.Requests.Count(request => request.Method == HttpMethod.Post));
        Assert.All(handler.Requests, request => Assert.Equal("[\"acme\"]", request.PartitionKey));
        Assert.DoesNotContain(handler.Requests, request => request.Upsert);

        await winner.DisposeAsync();

        var delete = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Equal("\"lease-version\"", delete.IfMatch);
        Assert.Equal("/dbs/dsf/colls/runs/docs/sweep-lease", delete.Path);
        Assert.Null(handler.Document);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_changed_or_missing_owner_is_not_reported_as_a_successful_release(HttpStatusCode status)
    {
        using var handler = new LeaseHandler { DeleteStatus = status };
        using var client = new HttpClient(handler);
        var gateway = new AzureCosmosDocumentGateway(new LeaseCredential(), client);
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);
        var owner = await lease.TryAcquireAsync(
            "acme", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(owner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.DisposeAsync().AsTask());

        Assert.Contains("changed during release", exception.Message);
        Assert.NotNull(handler.Document);
    }

    [Fact]
    public async Task Cosmos_release_errors_surface_without_an_unconditional_delete_retry()
    {
        using var handler = new LeaseHandler { DeleteStatus = HttpStatusCode.ServiceUnavailable };
        using var client = new HttpClient(handler);
        var gateway = new AzureCosmosDocumentGateway(new LeaseCredential(), client);
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);
        var owner = await lease.TryAcquireAsync(
            "acme", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(owner);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => owner.DisposeAsync().AsTask());

        Assert.Contains("503", exception.Message);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.NotNull(handler.Document);
    }

    private sealed class LeaseCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class LeaseHandler : HttpMessageHandler
    {
        private readonly object sync = new();
        public string? Document { get; private set; }
        public HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.NoContent;
        public ConcurrentQueue<(HttpMethod Method, string Path, string PartitionKey, string? IfMatch, bool Upsert)>
            Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var ifMatch = request.Headers.TryGetValues("If-Match", out var matches) ? matches.Single() : null;
            Requests.Enqueue((
                request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.GetValues("x-ms-documentdb-partitionkey").Single(), ifMatch,
                request.Headers.Contains("x-ms-documentdb-is-upsert")));
            lock (sync)
            {
                if (request.Method == HttpMethod.Post)
                {
                    if (Document is not null)
                    {
                        return new(HttpStatusCode.Conflict);
                    }

                    var document = JsonNode.Parse(body!)!.AsObject();
                    Assert.Equal(-1, document["ttl"]!.GetValue<int>());
                    document["_etag"] = "\"lease-version\"";
                    Document = document.ToJsonString();
                    return new(HttpStatusCode.Created);
                }

                if (request.Method == HttpMethod.Get)
                {
                    return Document is null
                        ? new(HttpStatusCode.NotFound)
                        : new(HttpStatusCode.OK)
                        {
                            Content = new StringContent(Document, Encoding.UTF8, "application/json"),
                        };
                }

                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal("\"lease-version\"", ifMatch);
                if (DeleteStatus == HttpStatusCode.NoContent)
                {
                    Document = null;
                }

                return new(DeleteStatus);
            }
        }
    }
}
