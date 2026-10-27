using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Runtime.Tests;

internal sealed class PublicationCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) =>
        new("test-publication-workload-token", DateTimeOffset.MaxValue);
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) =>
        ValueTask.FromResult(GetToken(context, token));
}

internal sealed class PublicationTransport : HttpMessageHandler
{
    internal readonly Dictionary<string, string> Documents;
    internal readonly List<(HttpMethod Method, bool Upsert)> Writes = [];
    internal string? ResponseCorruption { get; init; }
    internal Func<HttpRequestMessage, CancellationToken, Task>? BeforeWrite { get; set; }
    internal Action? AfterWrite { get; set; }
    internal bool LoseWriteResponse { get; set; }
    private int version;
    private readonly object sync = new();
    private sealed record RemoteState(Dictionary<string, string> Documents, int Version);
    internal PublicationTransport(string? serialized = null)
    {
        var state = serialized is null ? new RemoteState([], 0) : JsonSerializer.Deserialize<RemoteState>(serialized)!;
        Documents = state.Documents;
        version = state.Version;
    }
    internal string Export() => JsonSerializer.Serialize(new RemoteState(Documents, version));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.Equal("df-db0a6eac42114e-cosmos.documents.azure.com", request.RequestUri!.Host);
        Assert.Equal("[\"acme\"]", request.Headers.GetValues("x-ms-documentdb-partitionkey").Single());
        Assert.Equal("type=aad&ver=1.0&sig=test-publication-workload-token",
            Uri.UnescapeDataString(request.Headers.GetValues("Authorization").Single()));
        var collection = "/dbs/acme/colls/" + FactoryPublication.Container + "/docs";
        if (request.Method == HttpMethod.Get)
        {
            Assert.StartsWith(collection + "/", request.RequestUri.AbsolutePath);
            lock (sync)
                return Documents.TryGetValue(request.RequestUri.Segments[^1], out var stored)
                    ? Response(stored, HttpStatusCode.OK) : new(HttpStatusCode.NotFound);
        }
        var document = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        if (BeforeWrite is not null) await BeforeWrite(request, token);
        HttpResponseMessage response;
        lock (sync)
        {
            var id = (string)document["id"]!;
            var upsert = request.Headers.Contains("x-ms-documentdb-is-upsert");
            Writes.Add((request.Method, upsert));
            Assert.False(upsert);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(collection, request.RequestUri.AbsolutePath);
                if (Documents.ContainsKey(id)) return new(HttpStatusCode.Conflict);
            }
            else
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(collection + "/" + id, request.RequestUri.AbsolutePath);
                if (!Documents.TryGetValue(id, out var previous)) return new(HttpStatusCode.NotFound);
                if (request.Headers.GetValues("If-Match").Single() != (string?)JsonNode.Parse(previous)!["_etag"])
                    return new(HttpStatusCode.PreconditionFailed);
            }
            Assert.Equal("acme", (string?)document["product"]);
            document["_etag"] = "\"native-publication-" + ++version + "\"";
            var rid = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id))[..16];
            rid[4] |= 128;
            rid[15] &= 15;
            document["_rid"] = Convert.ToBase64String(rid).Replace('/', '-');
            Documents[id] = document.ToJsonString();
            response = Response(document.ToJsonString(), request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);
        }
        AfterWrite?.Invoke();
        if (LoseWriteResponse)
        {
            LoseWriteResponse = false;
            response.Dispose();
            throw new HttpRequestException("test-only-publication-provider-secret");
        }
        return response;
    }
    private HttpResponseMessage Response(string json, HttpStatusCode status)
    {
        var body = JsonNode.Parse(json)!;
        var etag = (string?)body["_etag"];
        if (ResponseCorruption == "missing-etag") body.AsObject().Remove("_etag");
        if (ResponseCorruption == "wrong-etag") etag = "\"wrong-version\"";
        if (ResponseCorruption == "changed-echo") body["state"] = "Unknown";
        var response = new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }
}
