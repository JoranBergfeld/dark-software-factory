using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The filing seam files real GitHub issues and is idempotent: a proposal carries
/// a durable intent key, the filer stamps it into the issue body, and a re-filed
/// intent resolves to the issue that already exists instead of duplicating it.
/// Exercised against a real HTTP server speaking the GitHub REST shapes.
/// </summary>
public sealed class GitHubIssueFilerTests
{
    private sealed record Recorded(string Path, string Body);

    private static Proposal ProposalWithIntent(string intentKey)
    {
        var proposal = new Proposal("run-1-sentry", "[sentry] checkout 500s spiked", ["sentry"], ["SENTRY-1"])
        {
            Verdict = ProposalVerdict.Proceed,
            Confidence = 0.9,
            IntentKey = intentKey,
        };
        proposal.Labels.Add("ready-for-agent");
        return proposal;
    }

    private static string BaseAddress(WebApplication app) =>
        app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);

    private static async Task<WebApplication> StartGitHubAsync(
        List<Recorded> recorded, Func<string, string> searchResponse)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/search/issues", (HttpRequest request) =>
        {
            var query = request.Query["q"].ToString();
            recorded.Add(new Recorded("/search/issues", query));
            return Results.Text(searchResponse(query), "application/json");
        });
        app.MapPost("/repos/{owner}/{repo}/issues", async (HttpRequest request, string owner, string repo) =>
        {
            using var reader = new StreamReader(request.Body);
            recorded.Add(new Recorded($"/repos/{owner}/{repo}/issues", await reader.ReadToEndAsync()));
            return Results.Json(new { html_url = "https://github.com/acme/acme/issues/7", number = 7 });
        });
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Files_an_issue_carrying_the_intent_key_labels_and_evidence()
    {
        var recorded = new List<Recorded>();
        var github = await StartGitHubAsync(recorded, _ => """{"total_count": 0, "items": []}""");
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var filer = new GitHubIssueFiler(http, "ghp_test", "acme/acme");

            var url = await filer.FileAsync(ProposalWithIntent("fingerprint-1:sentry"), CancellationToken.None);

            Assert.Equal("https://github.com/acme/acme/issues/7", url);
            var created = Assert.Single(recorded, entry => entry.Path == "/repos/acme/acme/issues");
            using var body = JsonDocument.Parse(created.Body);
            Assert.Equal("[sentry] checkout 500s spiked", body.RootElement.GetProperty("title").GetString());
            Assert.Contains("fingerprint-1:sentry", body.RootElement.GetProperty("body").GetString());
            Assert.Contains("SENTRY-1", body.RootElement.GetProperty("body").GetString());
            Assert.Contains(
                "ready-for-agent",
                body.RootElement.GetProperty("labels").EnumerateArray().Select(label => label.GetString()!));
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task An_already_filed_intent_resolves_to_the_existing_issue_without_filing_again()
    {
        var recorded = new List<Recorded>();
        var github = await StartGitHubAsync(
            recorded,
            _ => """
            {"total_count": 1, "items": [{"html_url": "https://github.com/acme/acme/issues/3",
             "body": "<!-- dsf-intent: fingerprint-1:sentry -->"}]}
            """);
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var filer = new GitHubIssueFiler(http, "ghp_test", "acme/acme");

            var url = await filer.FileAsync(ProposalWithIntent("fingerprint-1:sentry"), CancellationToken.None);

            Assert.Equal("https://github.com/acme/acme/issues/3", url);
            Assert.DoesNotContain(recorded, entry => entry.Path == "/repos/acme/acme/issues");
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task A_proposal_without_an_intent_key_is_refused_rather_than_filed_unguarded()
    {
        var recorded = new List<Recorded>();
        var github = await StartGitHubAsync(recorded, _ => """{"total_count": 0, "items": []}""");
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var filer = new GitHubIssueFiler(http, "ghp_test", "acme/acme");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => filer.FileAsync(ProposalWithIntent(string.Empty), CancellationToken.None));

            Assert.Empty(recorded);
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task Assigns_the_cloud_agent_to_a_newly_filed_issue_when_enabled()
    {
        var recorded = new List<Recorded>();
        var github = await StartGitHubWithAssignmentAsync(recorded);
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var filer = new GitHubIssueFiler(
                http, new Dsf.Runtime.GitHubApp.StaticGitHubAuthProvider("ghp_test"), "acme/acme", assignCloudAgent: true);

            var url = await filer.FileAsync(ProposalWithIntent("fingerprint-1:sentry"), CancellationToken.None);

            Assert.Equal("https://github.com/acme/acme/issues/7", url);
            var mutation = Assert.Single(recorded, entry => entry.Body.Contains("replaceActorsForAssignable", StringComparison.Ordinal));
            Assert.Contains("\"cloud-agent-actor-id\"", mutation.Body);
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task Does_not_query_graphql_when_cloud_agent_assignment_is_disabled()
    {
        var recorded = new List<Recorded>();
        var github = await StartGitHubWithAssignmentAsync(recorded);
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var filer = new GitHubIssueFiler(http, "ghp_test", "acme/acme");

            await filer.FileAsync(ProposalWithIntent("fingerprint-1:sentry"), CancellationToken.None);

            Assert.DoesNotContain(recorded, entry => entry.Path == "/graphql");
        }
        finally
        {
            await github.StopAsync();
        }
    }

    private static async Task<WebApplication> StartGitHubWithAssignmentAsync(List<Recorded> recorded)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/search/issues", (HttpRequest request) =>
        {
            recorded.Add(new Recorded("/search/issues", request.Query["q"].ToString()));
            return Results.Text("""{"total_count": 0, "items": []}""", "application/json");
        });
        app.MapPost("/repos/{owner}/{repo}/issues", async (HttpRequest request, string owner, string repo) =>
        {
            using var reader = new StreamReader(request.Body);
            recorded.Add(new Recorded($"/repos/{owner}/{repo}/issues", await reader.ReadToEndAsync()));
            return Results.Json(new { html_url = "https://github.com/acme/acme/issues/7", number = 7, node_id = "issue-node-id" });
        });
        app.MapPost("/graphql", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync();
            recorded.Add(new Recorded("/graphql", payload));
            if (payload.Contains("suggestedActors", StringComparison.Ordinal))
            {
                return Results.Text(
                    """
                    {"data": {"node": {"suggestedActors": {"nodes": [
                        {"login": "copilot-swe-agent", "id": "cloud-agent-actor-id"}
                    ]}}}}
                    """,
                    "application/json");
            }

            return Results.Text("""{"data": {"replaceActorsForAssignable": {"clientMutationId": null}}}""", "application/json");
        });
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Evolving_observations_of_the_same_problem_create_only_one_issue()
    {
        var recorded = new List<Recorded>();
        var github = await StartGitHubAsync(recorded, _ => JsonSerializer.Serialize(new
        {
            items = recorded.Where(entry => entry.Path == "/repos/acme/acme/issues").Select(entry =>
            {
                using var document = JsonDocument.Parse(entry.Body);
                return new { html_url = "https://github.com/acme/acme/issues/7", body = document.RootElement.GetProperty("body").GetString() };
            }).ToArray(),
        }));
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var filer = new GitHubIssueFiler(http, "ghp_test", "acme/acme");
            var services = new ConveyorServices(
                "acme", [], filer, new RecordingRunStore(), new RecordingModelClient(), new RecordingTracer(),
                new FixedConfidenceThresholdReader(0.6),
                ProblemIdentityResolver: new CosmosProblemIdentityResolver(
                    "https://cosmos.example", "dsf", "learning", "acme", new ProblemIdentityTestGateway()));
            foreach (var summary in new[] { "checkout errors: 17", "checkout errors: 18" })
            {
                var run = new ConveyorRun { Fingerprint = "same-scope" };
                run.Evidence.Add(new EvidenceItem("azuremonitor", "checkout-alert", summary));
                await new Dsf.FeatureCouncil.Conveyor.Stations.S3Synthesis().RunAsync(run, services, CancellationToken.None);
                await filer.FileAsync(Assert.Single(run.Proposals), CancellationToken.None);
            }

            Assert.Single(recorded, entry => entry.Path == "/repos/acme/acme/issues");
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task Synthesis_gives_every_proposal_a_durable_intent_key()
    {
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"] };
        run.Evidence.Add(new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        run.Fingerprint = "abc123";

        var services = new ConveyorServices(
            "acme", [], null, new RecordingRunStore(), new RecordingModelClient(), new RecordingTracer(),
            new FixedConfidenceThresholdReader(0.6),
            ProblemIdentityResolver: new CosmosProblemIdentityResolver(
                "https://cosmos.example", "dsf", "learning", "acme", new ProblemIdentityTestGateway()));
        var synthesis = new Dsf.FeatureCouncil.Conveyor.Stations.S3Synthesis();
        await synthesis.RunAsync(run, services, CancellationToken.None);

        var proposal = Assert.Single(run.Proposals);
        Assert.Matches("^abc123:[0-9a-f]{32}$", proposal.IntentKey);
        var laterRun = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"], Fingerprint = "abc123" };
        laterRun.Evidence.Add(new EvidenceItem("sentry", "SENTRY-2", "checkout 500s spiked"));
        await synthesis.RunAsync(laterRun, services, CancellationToken.None);
        Assert.Equal(proposal.IntentKey, Assert.Single(laterRun.Proposals).IntentKey);
    }
}
