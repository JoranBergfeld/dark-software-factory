using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The served source agent must actually gather. Its <c>/gather</c> endpoint reads
/// the kind's configured integration endpoint and returns what that integration
/// answered; when the integration is not configured it reports which setting is
/// unset. It must never answer a permanent "not implemented".
/// </summary>
public sealed class SourceAgentGatherTests
{
    private static readonly RuntimeSettings Settings = new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: "",
        AppInsightsConnectionString: "",
        CosmosEndpoint: "https://cosmos.example",
        OpenAiEndpoint: "https://openai.example",
        OpenAiDeployment: "gpt-deploy",
        OpenAiEmbeddingDeployment: "embed-deploy",
        GitHubAppId: "",
        GitHubInstallationId: "",
        GitHubAppPrivateKeySecret: "",
        GitHubRepository: "acme/acme");

    private static string BaseAddress(WebApplication app) =>
        app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);

    /// <summary>Serves a real HTTP endpoint standing in for an upstream source system.</summary>
    private static async Task<WebApplication> StartUpstreamAsync(string json, List<string>? authHeaders = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/issues", (HttpRequest request) =>
        {
            authHeaders?.Add(request.Headers.Authorization.ToString());
            return Results.Text(json, "application/json");
        });
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Gather_without_integration_configuration_names_the_unset_setting()
    {
        var dependencies = RuntimeDependencies.Production(new Dictionary<string, string?>());
        var app = RuntimeVerbs.BuildSourceAgentHost(Settings, "sentry", dependencies, "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var response = await client.PostAsync(
                "/gather", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.NotEqual(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("DSF_SOURCE_SENTRY_ENDPOINT", body);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Gather_returns_evidence_read_from_the_configured_integration_endpoint()
    {
        var authHeaders = new List<string>();
        var upstream = await StartUpstreamAsync(
            """[{"id": "SENTRY-1", "title": "checkout 500s spiked"}, {"id": "SENTRY-2", "title": "same trace"}]""",
            authHeaders);
        await using var upstreamHost = upstream;
        var env = new Dictionary<string, string?>
        {
            ["DSF_SOURCE_SENTRY_ENDPOINT"] = $"{BaseAddress(upstream)}/issues",
            ["DSF_SOURCE_SENTRY_TOKEN"] = "sentry-token",
        };
        var app = RuntimeVerbs.BuildSourceAgentHost(
            Settings, "sentry", RuntimeDependencies.Production(env), "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var response = await client.PostAsync(
                "/gather", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var evidence = payload.GetProperty("evidence").EnumerateArray().ToList();
            Assert.Equal(2, evidence.Count);
            Assert.Equal("SENTRY-1", evidence[0].GetProperty("reference").GetString());
            Assert.Equal("checkout 500s spiked", evidence[0].GetProperty("summary").GetString());
            Assert.Contains("Bearer sentry-token", authHeaders);
        }
        finally
        {
            await app.StopAsync();
            await upstream.StopAsync();
        }
    }

    [Fact]
    public async Task Gather_reports_an_unreachable_integration_instead_of_empty_evidence()
    {
        var env = new Dictionary<string, string?>
        {
            // A port nothing is listening on: the agent must report the failure.
            ["DSF_SOURCE_GRAFANA_ENDPOINT"] = "http://127.0.0.1:1/api/search",
        };
        var app = RuntimeVerbs.BuildSourceAgentHost(
            Settings, "grafana", RuntimeDependencies.Production(env), "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var response = await client.PostAsync(
                "/gather", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task The_orchestrator_side_gatherer_reads_evidence_from_a_served_source_agent()
    {
        var agent = RuntimeVerbs.BuildSourceAgentHost(
            Settings,
            "sentry",
            TestDependencies.Build(sourceIntegration: new ScriptedSourceIntegration(
                new EvidenceItem("sentry", "SENTRY-9", "queue backed up"))),
            "127.0.0.1",
            0);
        await using var agentHost = agent;
        await agent.StartAsync();
        try
        {
            var gatherer = new SourceAgentEvidenceGatherer("sentry", new Uri(BaseAddress(agent)), new HttpClient());

            var evidence = await gatherer.GatherAsync(
                new ConveyorRun { SourceKinds = ["sentry"], ProductHints = ["acme"] }, CancellationToken.None);

            var item = Assert.Single(evidence);
            Assert.Equal("SENTRY-9", item.Reference);
            Assert.Equal("queue backed up", item.Summary);
            Assert.Equal("sentry", item.SourceKind);
        }
        finally
        {
            await agent.StopAsync();
        }
    }

    [Fact]
    public async Task The_orchestrator_side_gatherer_reports_an_unreachable_source_agent()
    {
        var gatherer = new SourceAgentEvidenceGatherer(
            "sentry", new Uri("http://127.0.0.1:1"), new HttpClient());

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => gatherer.GatherAsync(new ConveyorRun { SourceKinds = ["sentry"] }, CancellationToken.None));

        Assert.Contains("sentry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
