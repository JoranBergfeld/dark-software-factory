using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class FactoryActivityStoreTests
{
    [Fact]
    public async Task Native_discovery_rolls_over_without_a_factory_lifetime_invocation_limit()
    {
        var now = DateTimeOffset.Parse("2026-09-15T23:00:00Z");
        var manifest = Manifest(now);
        var identity = manifest.Identity;
        var promotion = new FactoryPromotionApproval(1, identity.FactoryId, Guid.NewGuid(), identity.AzureActorId, now,
            new string('e', 64), new string('f', 64), new string('1', 64), new string('2', 64), []);
        using var handler = new ActivityIndexTransport();
        using var http = new HttpClient(handler);
        var credential = new ActivityCredential();
        var seed = await FactoryActivityStore.InitializeAsync(manifest, new(1, promotion, now), false,
            credential, http, TimeProvider.System, CancellationToken.None);
        var activity = new FactoryActivityStore(manifest, promotion, false, seed.Identity, credential, http, TimeProvider.System);
        var authority = new AuditRecordIdentity(identity.FactoryId, AuditStoreKind.Governance,
            "governance", "1", new string('3', 64));

        for (var index = 0; index <= FactoryActivityIndex.MaximumEntries; index++)
        {
            var invocation = Guid.NewGuid();
            var scope = new FactoryPublicationDecisionScope(invocation, authority, authority);
            Assert.True(await activity.RegisterAsync(FactoryPromotion.DecisionId(identity.FactoryId, invocation),
                FactoryModelsValidation.Hash(scope), _ => Task.CompletedTask, CancellationToken.None, intendedDecision: scope));
        }

        var retained = await activity.ReadAsync(CancellationToken.None);
        Assert.Equal(FactoryActivityIndex.MaximumEntries + 1, retained.Document.Entries.Count);
        Assert.Contains(handler.Documents.Keys,
            key => key.EndsWith(FactoryActivityIndex.Name(identity.FactoryId, false) + "-page-1", StringComparison.Ordinal));

        await activity.ConfirmAsync(retained.Document.Entries[^1].Id, "-wAAAIAAAAABAAAAAAAAAA==", CancellationToken.None);
        var confirmed = await activity.ReadAsync(CancellationToken.None);
        Assert.Equal(retained.Identity, confirmed.Identity);
        Assert.NotEqual(retained.SnapshotRevision, confirmed.SnapshotRevision);
        Assert.Equal("-wAAAIAAAAABAAAAAAAAAA==", confirmed.Document.Entries[^1].NativeResourceId);

        var pagePath = handler.Documents.Keys.Single(key => key.EndsWith("-page-1", StringComparison.Ordinal));
        var page = handler.Documents[pagePath];
        handler.Documents.Remove(pagePath);
        await Assert.ThrowsAsync<PreviewCapabilityException>(() => activity.ReadAsync(CancellationToken.None));
        handler.Documents[pagePath] = page;
        page["_rid"] = "-wAAAIAAAAACAAAAAAAAAA==";
        await Assert.ThrowsAsync<PreviewCapabilityException>(() => activity.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Scheduled_journal_uses_native_product_partition_and_reuses_unsettled_invocation()
    {
        var manifest = Manifest(DateTimeOffset.UtcNow);
        using var handler = new ActivityIndexTransport();
        using var http = new HttpClient(handler);
        var settings = new RuntimeSettings("widget", "https://product.azconfig.io", "", "", "", "", "", "", "", "", "", "");
        var dependencies = RuntimeDependencies.Production() with { PreviewCredential = new ActivityCredential(), PreviewHttp = http };

        var first = await RuntimeWorkflow.ResolveScheduledInvocationAsync(settings, dependencies, manifest, true, CancellationToken.None);
        var repeated = await RuntimeWorkflow.ResolveScheduledInvocationAsync(settings, dependencies, manifest, false, CancellationToken.None);
        Assert.Equal(first, repeated);
        Assert.True(repeated.DryRun);
        Assert.Equal("widget", (string?)handler.Documents.Single().Value["product"]);

        await RuntimeWorkflow.SetScheduledInvocationSettledAsync(settings, dependencies, manifest, first, CancellationToken.None);
        var next = await RuntimeWorkflow.ResolveScheduledInvocationAsync(settings, dependencies, manifest, false, CancellationToken.None);
        Assert.NotEqual(first.InvocationId, next.InvocationId);
        Assert.False(next.DryRun);
    }

    private static FactoryManifest Manifest(DateTimeOffset now)
    {
        var identity = new FactoryReservationIdentity(Guid.NewGuid(), Guid.NewGuid(), "https://owner.azconfig.io", "widget", 913,
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "prod", new string('a', 64), new string('b', 64),
            Guid.NewGuid().ToString(), 42, now);
        return new(1, "decide-factory-manifest", "product-app-configuration", "Inactive",
            identity, new string('c', 64), 1, 1, new string('d', 64));
    }

    private sealed class ActivityCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext request, CancellationToken token) =>
            new("activity-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken token) =>
            ValueTask.FromResult(GetToken(request, token));
    }

    private sealed class ActivityIndexTransport : HttpMessageHandler
    {
        internal Dictionary<string, JsonNode> Documents { get; } = new(StringComparer.Ordinal);
        private int revision;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get)
                return Documents.TryGetValue(path, out var current)
                    ? Reply(HttpStatusCode.OK, current)
                    : new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            var partition = JsonSerializer.Deserialize<string[]>(request.Headers.GetValues("x-ms-documentdb-partitionkey").Single())!.Single();
            if ((string?)body["product"] != partition) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            var id = request.Method == HttpMethod.Post ? (string)body["id"]! : path[(path.LastIndexOf('/') + 1)..];
            var documentPath = request.Method == HttpMethod.Post ? path + "/" + id : path;
            if (request.Method == HttpMethod.Post && Documents.ContainsKey(documentPath))
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            if (request.Method == HttpMethod.Put
                && (!Documents.TryGetValue(documentPath, out var existing)
                    || request.Headers.GetValues("If-Match").Single() != (string)existing["_etag"]!))
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            body["_rid"] = "-wAAAIAAAAABAAAAAAAAAA==";
            body["_etag"] = $"\"{++revision}\"";
            Documents[documentPath] = body;
            return Reply(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, body);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, JsonNode body)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body.ToJsonString())
            };
            response.Headers.TryAddWithoutValidation("ETag", (string)body["_etag"]!);
            return response;
        }
    }
}
