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
        var proposal = new Proposal("run-1-sentry", "[sentry] checkout 500s spiked", "sentry", ["SENTRY-1"])
        {
            Accepted = true,
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
    public async Task Synthesis_gives_every_proposal_a_durable_intent_key()
    {
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"] };
        run.Evidence.Add(new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        run.Fingerprint = "abc123";

        await new Dsf.FeatureCouncil.Conveyor.Stations.S3Synthesis()
            .RunAsync(run, new ConveyorServices("acme", [], null, new RecordingRunStore()), CancellationToken.None);

        var proposal = Assert.Single(run.Proposals);
        // Stable across runs of the same scope: the fingerprint, not the run id.
        Assert.Equal("abc123:sentry", proposal.IntentKey);
    }
}
