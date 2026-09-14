using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class ProblemIdentityTests
{
    [Theory]
    [InlineData("alert-1", "checkout errors: 18")]
    [InlineData("alert-2", "checkout errors: 17")]
    [InlineData("alert-2", "checkout errors: 18")]
    public async Task Independent_processes_resolve_evolving_observations_to_the_persisted_problem(
        string reference, string summary)
    {
        var gateway = new ProblemIdentityTestGateway();
        var first = await Resolver(gateway).ResolveAsync("scope", Cluster("alert-1", "checkout errors: 17"), CancellationToken.None);

        var recurring = await Resolver(gateway).ResolveAsync("scope", Cluster(reference, summary), CancellationToken.None);

        Assert.Equal(first, recurring);
    }

    [Fact]
    public async Task Recognized_observations_extend_the_profile_for_later_evidence()
    {
        var gateway = new ProblemIdentityTestGateway();
        var first = await Resolver(gateway).ResolveAsync("scope", Cluster("alert", "checkout errors: 17"), CancellationToken.None);
        var second = await Resolver(gateway).ResolveAsync("scope", Cluster("alert", "checkout errors: 18"), CancellationToken.None);
        var third = await Resolver(gateway).ResolveAsync("scope", Cluster("alert", "checkout failures: 18"), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public async Task Distinct_problems_and_scopes_keep_distinct_persistent_keys()
    {
        var gateway = new ProblemIdentityTestGateway();
        var first = await Resolver(gateway).ResolveAsync("scope", Cluster("a", "checkout timeout"), CancellationToken.None);
        var second = await Resolver(gateway).ResolveAsync("scope", Cluster("b", "inventory warehouse shortage"), CancellationToken.None);
        var otherScope = await Resolver(gateway).ResolveAsync("other", Cluster("a", "checkout timeout"), CancellationToken.None);
        var otherProduct = await Resolver(gateway, "other-product").ResolveAsync("scope", Cluster("a", "checkout timeout"), CancellationToken.None);

        Assert.Equal(4, new[] { first, second, otherScope, otherProduct }.Distinct().Count());
        Assert.Equal(first, await Resolver(gateway).ResolveAsync("scope", Cluster("a", "checkout timeout"), CancellationToken.None));
        Assert.Equal(second, await Resolver(gateway).ResolveAsync("scope", Cluster("b", "inventory warehouse shortage"), CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_creators_converge_on_one_persisted_identity()
    {
        var gateway = new ProblemIdentityTestGateway();

        var keys = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            Resolver(gateway).ResolveAsync("scope", Cluster($"alert-{index}", "checkout errors: 17"), CancellationToken.None)));

        Assert.Single(keys.Distinct());
    }

    [Fact]
    public async Task Concurrent_distinct_insertions_do_not_lose_either_problem()
    {
        var gateway = new ProblemIdentityTestGateway();
        var clusters = new[] { Cluster("a", "checkout timeout"), Cluster("b", "inventory warehouse shortage") };

        var keys = await Task.WhenAll(clusters.Select(cluster =>
            Resolver(gateway).ResolveAsync("scope", cluster, CancellationToken.None)));

        Assert.NotEqual(keys[0], keys[1]);
        for (var index = 0; index < clusters.Length; index++)
        {
            Assert.Equal(keys[index], await Resolver(gateway).ResolveAsync("scope", clusters[index], CancellationToken.None));
        }
    }

    [Fact]
    public async Task Concurrent_profile_updates_retain_both_recognition_paths()
    {
        var gateway = new ProblemIdentityTestGateway();
        var first = await Resolver(gateway).ResolveAsync("scope", Cluster("a", "checkout errors: 17"), CancellationToken.None);
        var additions = new[] { Cluster("b", "checkout errors: 18"), Cluster("c", "checkout failures: 17") };

        var keys = await Task.WhenAll(additions.Select(cluster =>
            Resolver(gateway).ResolveAsync("scope", cluster, CancellationToken.None)));

        Assert.All(keys, key => Assert.Equal(first, key));
        Assert.Equal(first, await Resolver(gateway).ResolveAsync(
            "scope", Cluster("d", "invoice errors: 18"), CancellationToken.None));
        Assert.Equal(first, await Resolver(gateway).ResolveAsync(
            "scope", Cluster("e", "checkout failures urgent"), CancellationToken.None));
    }

    [Fact]
    public async Task Evidence_bridging_two_known_problems_requires_human_consolidation()
    {
        var gateway = new ProblemIdentityTestGateway();
        await Resolver(gateway).ResolveAsync("scope", Cluster("a", "checkout timeout"), CancellationToken.None);
        await Resolver(gateway).ResolveAsync("scope", Cluster("b", "warehouse inventory"), CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Resolver(gateway).ResolveAsync("scope", Cluster("c", "checkout timeout warehouse inventory"), CancellationToken.None));

        Assert.Contains("ambiguous", error.Message);
    }

    [Fact]
    public async Task An_unwritable_index_fails_instead_of_returning_an_unpersisted_identity()
    {
        var gateway = new ProblemIdentityTestGateway { RejectWrites = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Resolver(gateway).ResolveAsync("scope", Cluster("a", "checkout timeout"), CancellationToken.None));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("evidence")]
    public async Task Incomplete_persisted_profiles_cannot_authorize_an_identity(string missingField)
    {
        var gateway = new ProblemIdentityTestGateway
        {
            TransformRead = json =>
            {
                if (json is null)
                {
                    return null;
                }
                var document = JsonNode.Parse(json)!;
                document["problems"]![0]!.AsObject().Remove(missingField);
                return document.ToJsonString();
            },
        };
        await Resolver(gateway).ResolveAsync("scope", Cluster("a", "checkout timeout"), CancellationToken.None);

        await Assert.ThrowsAsync<JsonException>(() =>
            Resolver(gateway).ResolveAsync("scope", Cluster("a", "checkout timeout"), CancellationToken.None));
    }

    [Fact]
    public async Task Cosmos_replacement_errors_are_not_reported_as_conflicts_or_success()
    {
        using var handler = new ConditionalWriteHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var gateway = new AzureCosmosDocumentGateway(new Credential(), client);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.ReplaceIfMatchAsync(
            "https://cosmos.example", "dsf", "learning", "acme", "index", "{}", "\"version-1\"", CancellationToken.None));

        Assert.Contains("503", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.PreconditionFailed, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Cosmos_replacement_requires_the_observed_version(HttpStatusCode status, bool replaced)
    {
        using var handler = new ConditionalWriteHandler(status);
        using var client = new HttpClient(handler);
        var gateway = new AzureCosmosDocumentGateway(new Credential(), client);

        Assert.Equal(replaced, await gateway.ReplaceIfMatchAsync(
            "https://cosmos.example", "dsf", "learning", "acme", "index", "{}", "\"version-1\"", CancellationToken.None));
    }

    private static CosmosProblemIdentityResolver Resolver(ICosmosDocumentGateway gateway, string product = "acme") =>
        new("https://cosmos.example", "dsf", "learning", product, gateway);

    private static EvidenceCluster Cluster(string reference, string summary) =>
        new([new EvidenceItem("azuremonitor", reference, summary)]);

    private sealed class ConditionalWriteHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/dbs/dsf/colls/learning/docs/index", request.RequestUri!.AbsolutePath);
            Assert.Equal("\"version-1\"", request.Headers.GetValues("If-Match").Single());
            Assert.Equal("[\"acme\"]", request.Headers.GetValues("x-ms-documentdb-partitionkey").Single());
            Assert.False(request.Headers.Contains("x-ms-documentdb-is-upsert"));
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
