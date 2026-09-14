using System.Net;
using System.Text.Json;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class GitHubCharterRepositoryClientTests
{
    [Fact]
    public async Task OpenFilePullRequest_reports_auto_merge_failure_with_the_created_pr_url()
    {
        var handler = new StubHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"default_branch":"main"}"""),
            Response(HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}"""),
            Response(HttpStatusCode.Created, """{"ref":"refs/heads/charter/constitution"}"""),
            Response(HttpStatusCode.Created, """{"content":{"sha":"blob-sha"}}"""),
            Response(HttpStatusCode.Created, """{"html_url":"https://github.test/acme/demo/pull/7"}"""),
            Response(HttpStatusCode.OK, """{"allow_auto_merge":true}"""));
        var gh = new StubGhCliRunner(
            new GhInvocationResult(1, string.Empty, "Auto-merge is not allowed for this repository"));
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token", gh);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenFilePullRequestAsync(
            "acme/demo",
            ".specify/memory/constitution.md",
            "constitution",
            "charter/constitution",
            "title",
            "body",
            "message",
            enableAutoMerge: true,
            existingSha: null,
            CancellationToken.None));

        Assert.Contains("https://github.test/acme/demo/pull/7", error.Message);
        Assert.Contains("Auto-merge is not allowed", error.Message);
        Assert.Single(gh.Invocations);
        Assert.Contains("--auto", gh.Invocations[0]);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Initial_charter_requests_auto_merge_and_enables_it_on_existing_repositories()
    {
        var handler = new StubHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"default_branch":"main"}"""),
            Response(HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}"""),
            Response(HttpStatusCode.Created, "{}"),
            Response(HttpStatusCode.Created, "{}"),
            Response(HttpStatusCode.Created, """{"html_url":"https://github.test/acme/demo/pull/7"}"""),
            Response(HttpStatusCode.OK, """{"allow_auto_merge":false}"""),
            Response(HttpStatusCode.OK, """{"allow_auto_merge":true}"""));
        var gh = new StubGhCliRunner(new GhInvocationResult(0, "", ""));
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token", gh);

        await client.OpenInitialPullRequestAsync("acme/demo", "demo", "charter", CancellationToken.None);

        Assert.Equal(
            ["pr", "merge", "https://github.test/acme/demo/pull/7", "--repo", "acme/demo", "--auto", "--squash"],
            Assert.Single(gh.Invocations));
        var update = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
        Assert.Equal("/repos/acme/demo", update.Path);
        using var payload = JsonDocument.Parse(update.Body!);
        Assert.Single(payload.RootElement.EnumerateObject());
        Assert.True(payload.RootElement.GetProperty("allow_auto_merge").GetBoolean());
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("rulesets"));
    }

    [Fact]
    public async Task Auto_merge_does_not_patch_an_already_enabled_repository_or_bypass_gates()
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.OK, """{"allow_auto_merge":true}"""));
        var gh = new StubGhCliRunner(new GhInvocationResult(0, "", ""));
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token", gh);

        await client.EnsurePullRequestAutoMergeAsync("acme/demo", "https://github.test/acme/demo/pull/7", CancellationToken.None);

        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
        Assert.Equal(
            ["pr", "merge", "https://github.test/acme/demo/pull/7", "--repo", "acme/demo", "--auto", "--squash"],
            Assert.Single(gh.Invocations));
    }

    [Fact]
    public async Task Repository_permission_failure_includes_pr_and_stops_before_merge()
    {
        var handler = new StubHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"allow_auto_merge":false}"""),
            Response(HttpStatusCode.Forbidden, """{"message":"Resource not accessible"}"""));
        var gh = new StubGhCliRunner();
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token", gh);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EnsurePullRequestAutoMergeAsync("acme/demo", "https://github.test/acme/demo/pull/7", CancellationToken.None));

        Assert.Contains("https://github.test/acme/demo/pull/7", error.Message);
        Assert.Contains("403", error.Message);
        Assert.Contains("PR is preserved", error.Message);
        Assert.Empty(gh.Invocations);
    }

    [Fact]
    public async Task Missing_gh_reports_the_preserved_pr_instead_of_success()
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.OK, """{"allow_auto_merge":true}"""));
        var gh = new StubGhCliRunner { Error = new GhUnavailableException("gh is missing", new IOException()) };
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token", gh);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EnsurePullRequestAutoMergeAsync("acme/demo", "https://github.test/acme/demo/pull/7", CancellationToken.None));

        Assert.Contains("gh is missing", error.Message);
        Assert.Contains("https://github.test/acme/demo/pull/7", error.Message);
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_an_auto_merge_failure()
    {
        var handler = new StubHttpMessageHandler(Response(HttpStatusCode.OK, """{"allow_auto_merge":true}"""));
        var gh = new StubGhCliRunner { Error = new OperationCanceledException() };
        var client = new GitHubCharterRepositoryClient(ApiClient(handler), "test-token", gh);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.EnsurePullRequestAutoMergeAsync("acme/demo", "https://github.test/acme/demo/pull/7", CancellationToken.None));
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
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("No response configured.");
        }
    }

    private sealed class StubGhCliRunner(params GhInvocationResult[] results) : IGhCliRunner
    {
        private readonly Queue<GhInvocationResult> results = new(results);

        public List<IReadOnlyList<string>> Invocations { get; } = [];
        public Exception? Error { get; init; }

        public GhInvocationResult Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Invocations.Add(arguments);
            if (Error is not null)
            {
                throw Error;
            }
            return results.Count > 0
                ? results.Dequeue()
                : throw new InvalidOperationException("No gh result configured.");
        }
    }
}
