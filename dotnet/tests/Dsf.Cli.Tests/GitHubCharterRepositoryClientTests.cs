using System.Net;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class GitHubCharterRepositoryClientTests
{
    [Fact]
    public async Task OpenFilePullRequest_keeps_the_pull_request_when_auto_merge_is_rejected()
    {
        var handler = new StubHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"default_branch":"main"}"""),
            Response(HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}"""),
            Response(HttpStatusCode.Created, """{"ref":"refs/heads/charter/constitution"}"""),
            Response(HttpStatusCode.Created, """{"content":{"sha":"blob-sha"}}"""),
            Response(HttpStatusCode.Created, """{"html_url":"https://github.test/acme/demo/pull/7"}"""));
        var gh = new StubGhCliRunner(
            new GhInvocationResult(1, string.Empty, "Auto-merge is not allowed for this repository"));
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token", gh);

        var url = await client.OpenFilePullRequestAsync(
            "acme/demo",
            ".specify/memory/constitution.md",
            "constitution",
            "charter/constitution",
            "title",
            "body",
            "message",
            enableAutoMerge: true,
            existingSha: null,
            CancellationToken.None);

        Assert.Equal("https://github.test/acme/demo/pull/7", url);
        Assert.Single(gh.Invocations);
        Assert.Contains("--auto", gh.Invocations[0]);
    }

    [Fact]
    public async Task AssignCopilotWithApp_returns_false_when_copilot_is_not_a_suggested_actor()
    {
        var handler = new StubHttpMessageHandler(
            Response(
                HttpStatusCode.OK,
                """{"data":{"repository":{"suggestedActors":{"nodes":[{"login":"octocat","__typename":"User"}]}}}}"""));
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token");

        var assigned = await client.AssignCopilotWithAppAsync("acme/demo", "ISSUE_node", CancellationToken.None);

        Assert.False(assigned);
    }

    [Fact]
    public async Task AssignCopilotWithApp_returns_false_on_graphql_errors()
    {
        var handler = new StubHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"errors":[{"message":"Resource not accessible by integration"}]}"""));
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token");

        var assigned = await client.AssignCopilotWithAppAsync("acme/demo", "ISSUE_node", CancellationToken.None);

        Assert.False(assigned);
    }

    [Fact]
    public async Task HasCopilotReviewRequest_skips_reviewers_without_a_login()
    {
        var gh = new StubGhCliRunner(
            new GhInvocationResult(
                0,
                """
                {"data":{"repository":{"pullRequest":{"reviewRequests":{"nodes":[
                  {"requestedReviewer":{"__typename":"Team"}},
                  {"requestedReviewer":null},
                  {"requestedReviewer":{"__typename":"Bot","login":"copilot-pull-request-reviewer"}}
                ]}}}}}
                """,
                string.Empty));
        var client = new GitHubCharterRepositoryClient(ApiClient(new StubHttpMessageHandler()), "test-token", gh);

        var requested = await client.HasCopilotReviewRequestAsync("acme/demo", 12, CancellationToken.None);

        Assert.True(requested);
    }

    [Fact]
    public async Task HasCopilotReviewRequest_is_false_when_only_teams_are_requested()
    {
        var gh = new StubGhCliRunner(
            new GhInvocationResult(
                0,
                """{"data":{"repository":{"pullRequest":{"reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"Team"}}]}}}}}""",
                string.Empty));
        var client = new GitHubCharterRepositoryClient(ApiClient(new StubHttpMessageHandler()), "test-token", gh);

        Assert.False(await client.HasCopilotReviewRequestAsync("acme/demo", 12, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_gh_invocations_surface_as_retryable_gh_command_errors()
    {
        var gh = new StubGhCliRunner(new GhInvocationResult(1, string.Empty, "gh: server error"));
        var client = new GitHubCharterRepositoryClient(ApiClient(new StubHttpMessageHandler()), "test-token", gh);

        await Assert.ThrowsAsync<GhCommandException>(
            () => client.HasCopilotReviewRequestAsync("acme/demo", 12, CancellationToken.None));
    }

    private static HttpClient ApiClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.github.test/") };

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) =>
        new(status)
        {
            Content = new StringContent(json ?? "{}", System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("No response configured."));
    }

    private sealed class StubGhCliRunner(params GhInvocationResult[] results) : IGhCliRunner
    {
        private readonly Queue<GhInvocationResult> results = new(results);

        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public GhInvocationResult Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Invocations.Add(arguments);
            return results.Count > 0
                ? results.Dequeue()
                : throw new InvalidOperationException("No gh result configured.");
        }
    }
}
