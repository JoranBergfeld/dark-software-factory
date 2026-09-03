using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The outcome-polling seam reads real GitHub search results: it finds issues
/// carrying both the filing station's intent-key marker and a canonical outcome
/// label, and reports the intent key and verdict for each -- so the learning loop
/// can record what disposition a filed proposal actually received.
/// </summary>
public sealed class GitHubOutcomePollerTests
{
    private static string BaseAddress(WebApplication app) =>
        app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);

    private static async Task<WebApplication> StartGitHubAsync(Action<string> onQuery, Func<string> searchResponse)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/search/issues", (HttpRequest request) =>
        {
            onQuery(request.Query["q"].ToString());
            return Results.Text(searchResponse(), "application/json");
        });
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Polling_ORs_every_canonical_outcome_label_into_one_search_query()
    {
        string? query = null;
        var github = await StartGitHubAsync(q => query = q, () => """{"total_count": 0, "items": []}""");
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var poller = new GitHubOutcomePoller(http, new StaticAuthProvider("ghp_test"), "acme/acme");

            await poller.PollAsync(CancellationToken.None);

            Assert.NotNull(query);
            var decoded = Uri.UnescapeDataString(query!);
            Assert.Contains("repo:acme/acme", decoded);
            Assert.Contains("dsf-outcome:approved", decoded);
            Assert.Contains("dsf-outcome:rejected", decoded);
            Assert.Contains("dsf-outcome:changes-requested", decoded);
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task An_outcome_labelled_issue_carrying_an_intent_marker_is_reported_with_its_verdict()
    {
        var github = await StartGitHubAsync(
            _ => { },
            () => """
            {"total_count": 1, "items": [{
                "html_url": "https://github.com/acme/acme/issues/9",
                "title": "[sentry] checkout 500s spiked",
                "body": "<!-- dsf-intent: fingerprint-1:sentry -->\n\nSome body.",
                "labels": [{"name": "ready-for-agent"}, {"name": "dsf-outcome:approved"}]
            }]}
            """);
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var poller = new GitHubOutcomePoller(http, new StaticAuthProvider("ghp_test"), "acme/acme");

            var signals = await poller.PollAsync(CancellationToken.None);

            var signal = Assert.Single(signals);
            Assert.Equal("fingerprint-1:sentry", signal.IntentKey);
            Assert.Equal(OutcomeLabels.Approved, signal.Verdict);
            Assert.Equal("https://github.com/acme/acme/issues/9", signal.IssueUrl);
            Assert.Equal("[sentry] checkout 500s spiked", signal.Title);
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task An_outcome_labelled_issue_with_no_intent_marker_is_skipped()
    {
        var github = await StartGitHubAsync(
            _ => { },
            () => """
            {"total_count": 1, "items": [{
                "html_url": "https://github.com/acme/acme/issues/10",
                "title": "manually filed, no marker",
                "body": "no marker here",
                "labels": [{"name": "dsf-outcome:rejected"}]
            }]}
            """);
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var poller = new GitHubOutcomePoller(http, new StaticAuthProvider("ghp_test"), "acme/acme");

            var signals = await poller.PollAsync(CancellationToken.None);

            Assert.Empty(signals);
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task A_refused_search_is_reported_with_the_repository_it_polled()
    {
        var failingBuilder = WebApplication.CreateSlimBuilder();
        failingBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        var failing = failingBuilder.Build();
        failing.MapGet("/search/issues", () => Results.Json(new { message = "rate limited" }, statusCode: 403));
        await failing.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(failing)}/") };
            var poller = new GitHubOutcomePoller(http, new StaticAuthProvider("ghp_test"), "acme/acme");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => poller.PollAsync(CancellationToken.None));

            Assert.Contains("acme/acme", exception.Message);
            Assert.Contains("403", exception.Message);
        }
        finally
        {
            await failing.StopAsync();
        }
    }

    private sealed class StaticAuthProvider(string token) : GitHubApp.IGitHubAuthProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }
}
