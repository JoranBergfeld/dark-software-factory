using System.Net;
using System.Text.Json;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class GitHubRestProvisioningClientTests
{
    [Fact]
    public async Task Missing_token_fails_loudly_before_github_mutation()
    {
        var handler = new RecordingHttpMessageHandler();
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), token: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnsureRepositoryAsync(
                new EnsureRepositoryRequest("acme", "demo", "private", "main"),
                CancellationToken.None));

        Assert.Contains("GH_TOKEN", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnsureRepository_with_omitted_owner_resolves_authenticated_user_and_creates_user_repo()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"login":"octocat"}"""),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.Created, """{"id":456,"default_branch":"main","owner":{"login":"octocat"}}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureRepositoryAsync(
            new EnsureRepositoryRequest("", "demo", "private", "main"),
            CancellationToken.None);

        Assert.Equal(456, result.RepositoryId);
        Assert.Equal("main", result.DefaultBranch);
        Assert.Equal("octocat", result.Owner);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/user", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/repos/octocat/demo", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/user/repos", request.Path);
                using var payload = JsonDocument.Parse(request.Body!);
                Assert.Equal("demo", payload.RootElement.GetProperty("name").GetString());
                Assert.Equal("private", payload.RootElement.GetProperty("visibility").GetString());
            });
    }

    [Fact]
    public async Task Missing_seed_repo_workflow_is_created_via_contents_api()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.Created, """{"content":{"sha":"def456"}}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        await client.EnsureSeedRepoAsync(
            new EnsureSeedRepoRequest("acme/demo", "main"),
            CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/repos/acme/demo/contents/.github/workflows/ci.yml", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/repos/acme/demo/contents/.github/workflows/ci.yml", request.Path);
                using var payload = JsonDocument.Parse(request.Body!);
                Assert.Equal("chore: seed baseline ci workflow", payload.RootElement.GetProperty("message").GetString());
                Assert.Equal("main", payload.RootElement.GetProperty("branch").GetString());
                var contentBase64 = payload.RootElement.GetProperty("content").GetString();
                Assert.NotNull(contentBase64);
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(contentBase64));
                Assert.Contains("name: ci", decoded, StringComparison.Ordinal);
                Assert.Contains("runs-on: ubuntu-latest", decoded, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Existing_seed_repo_workflow_skips_creation_mutation()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"sha":"abc123"}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        await client.EnsureSeedRepoAsync(
            new EnsureSeedRepoRequest("acme/demo", "main"),
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/repos/acme/demo/contents/.github/workflows/ci.yml", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Missing_organization_repository_is_created_once()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.OK, """{"login":"operator"}"""),
            Response(HttpStatusCode.Created, """{"id":123,"default_branch":"main"}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureRepositoryAsync(
            new EnsureRepositoryRequest("acme", "demo", "private", "main"),
            CancellationToken.None);

        Assert.Equal(123, result.RepositoryId);
        Assert.Equal("main", result.DefaultBranch);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/repos/acme/demo", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/user", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/orgs/acme/repos", request.Path);
                using var payload = JsonDocument.Parse(request.Body!);
                Assert.Equal("demo", payload.RootElement.GetProperty("name").GetString());
                Assert.Equal("private", payload.RootElement.GetProperty("visibility").GetString());
            });
    }

    [Fact]
    public async Task Labels_create_only_missing_definitions()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """[{"name":"feature"}]"""),
            Response(HttpStatusCode.Created, """{"name":"incident"}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        await client.EnsureLabelsAsync(
            new EnsureLabelsRequest(
                "acme/demo",
                [
                    new GitHubLabelDefinition("feature"),
                    new GitHubLabelDefinition("incident", "b60205", "SRE-filed incident"),
                ]),
            CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/repos/acme/demo/labels?per_page=100", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/repos/acme/demo/labels", request.Path);
                using var payload = JsonDocument.Parse(request.Body!);
                Assert.Equal("incident", payload.RootElement.GetProperty("name").GetString());
                Assert.Equal("b60205", payload.RootElement.GetProperty("color").GetString());
                Assert.Equal(
                    "SRE-filed incident",
                    payload.RootElement.GetProperty("description").GetString());
            });
    }

    [Fact]
    public async Task Labels_follow_pagination_before_deciding_what_is_missing()
    {
        var firstPage = Response(HttpStatusCode.OK, """[{"name":"feature"}]""");
        firstPage.Headers.Add(
            "Link",
            "<https://api.github.com/repos/acme/demo/labels?per_page=100&page=2>; rel=\"next\"");
        var handler = new RecordingHttpMessageHandler(
            firstPage,
            Response(HttpStatusCode.OK, """[{"name":"incident"}]"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        await client.EnsureLabelsAsync(
            new EnsureLabelsRequest(
                "acme/demo",
                [
                    new GitHubLabelDefinition("feature"),
                    new GitHubLabelDefinition("incident"),
                ]),
            CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            request => Assert.Equal(
                "/repos/acme/demo/labels?per_page=100",
                request.Path),
            request => Assert.Equal(
                "/repos/acme/demo/labels?per_page=100&page=2",
                request.Path));
    }

    [Fact]
    public async Task App_binding_adds_repository_when_selected_installation_does_not_cover_it()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"id":123,"default_branch":"main"}"""),
            Response(
                HttpStatusCode.OK,
                """{"total_count":0,"repository_selection":"selected","repositories":[]}"""),
            Response(HttpStatusCode.NoContent));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureAppBindingAsync(
            new EnsureAppBindingRequest("acme/demo", "7", "42"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("7", result.AppId);
        Assert.Equal("42", result.InstallationId);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/repos/acme/demo", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    "/user/installations/42/repositories?per_page=100",
                    request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/user/installations/42/repositories/123", request.Path);
            });
    }

    [Fact]
    public async Task App_binding_skips_mutation_when_selected_installation_already_covers_repository()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"id":123,"default_branch":"main"}"""),
            Response(
                HttpStatusCode.OK,
                """{"total_count":1,"repository_selection":"selected","repositories":[{"id":123}]}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureAppBindingAsync(
            new EnsureAppBindingRequest("acme/demo", "7", "42"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("7", result.AppId);
        Assert.Equal("42", result.InstallationId);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/repos/acme/demo", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    "/user/installations/42/repositories?per_page=100",
                    request.Path);
            });
    }

    [Fact]
    public async Task App_binding_skips_mutation_for_all_repositories_installation()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"id":123,"default_branch":"main"}"""),
            Response(
                HttpStatusCode.OK,
                """{"total_count":0,"repository_selection":"all","repositories":[]}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureAppBindingAsync(
            new EnsureAppBindingRequest("acme/demo", "7", "42"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("7", result.AppId);
        Assert.Equal("42", result.InstallationId);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/repos/acme/demo", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    "/user/installations/42/repositories?per_page=100",
                    request.Path);
            });
    }

    [Fact]
    public async Task App_binding_follows_pagination_before_deciding_repository_is_missing()
    {
        var firstPage = Response(
            HttpStatusCode.OK,
            """{"total_count":1,"repository_selection":"selected","repositories":[{"id":999}]}""");
        firstPage.Headers.Add(
            "Link",
            "<https://api.github.com/user/installations/42/repositories"
            + "?per_page=100&page=2>; rel=\"next\"");
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"id":123,"default_branch":"main"}"""),
            firstPage,
            Response(
                HttpStatusCode.OK,
                """{"total_count":1,"repository_selection":"selected","repositories":[{"id":123}]}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureAppBindingAsync(
            new EnsureAppBindingRequest("acme/demo", "7", "42"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/repos/acme/demo", request.Path),
            request => Assert.Equal(
                "/user/installations/42/repositories?per_page=100",
                request.Path),
            request => Assert.Equal(
                "/user/installations/42/repositories?per_page=100&page=2",
                request.Path));
    }

    [Fact]
    public async Task Existing_ruleset_is_updated_with_frozen_payload_and_auto_merge()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"id":456}"""),
            Response(HttpStatusCode.OK, """{"id":123}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureBranchProtectionRulesetAsync(
            new EnsureBranchProtectionRulesetRequest(
                "acme/demo",
                "main",
                ["ci"],
                RequiredApprovingReviewCount: 0,
                ExistingRulesetId: 456,
                AllowAutoMerge: true),
            CancellationToken.None);

        Assert.Equal(456, result.RulesetId);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/repos/acme/demo/rulesets/456", request.Path);
                using var payload = JsonDocument.Parse(request.Body!);
                var root = payload.RootElement;
                Assert.Equal("dsf-creation", root.GetProperty("name").GetString());
                Assert.Equal("branch", root.GetProperty("target").GetString());
                Assert.Equal("active", root.GetProperty("enforcement").GetString());
                Assert.Equal(
                    "~DEFAULT_BRANCH",
                    root.GetProperty("conditions")
                        .GetProperty("ref_name")
                        .GetProperty("include")[0]
                        .GetString());
                var rules = root.GetProperty("rules");
                Assert.Equal("pull_request", rules[0].GetProperty("type").GetString());
                Assert.Equal(
                    0,
                    rules[0]
                        .GetProperty("parameters")
                        .GetProperty("required_approving_review_count")
                        .GetInt32());
                Assert.True(
                    rules[0]
                        .GetProperty("parameters")
                        .GetProperty("dismiss_stale_reviews_on_push")
                        .GetBoolean());
                Assert.Equal("required_status_checks", rules[1].GetProperty("type").GetString());
                Assert.True(
                    rules[1]
                        .GetProperty("parameters")
                        .GetProperty("strict_required_status_checks_policy")
                        .GetBoolean());
                Assert.True(
                    rules[1]
                        .GetProperty("parameters")
                        .GetProperty("do_not_enforce_on_create")
                        .GetBoolean());
                Assert.Equal(
                    "ci",
                    rules[1]
                        .GetProperty("parameters")
                        .GetProperty("required_status_checks")[0]
                        .GetProperty("context")
                        .GetString());
            },
            request =>
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.Equal("/repos/acme/demo", request.Path);
                using var payload = JsonDocument.Parse(request.Body!);
                Assert.True(payload.RootElement.GetProperty("allow_auto_merge").GetBoolean());
            });
    }

    [Fact]
    public async Task Unsupported_private_repository_ruleset_is_skipped_without_auto_merge_mutation()
    {
        var handler = new RecordingHttpMessageHandler(
            Response(
                HttpStatusCode.Forbidden,
                """{"message":"Upgrade to GitHub Pro or make this repository public to enable this feature."}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureBranchProtectionRulesetAsync(
            new EnsureBranchProtectionRulesetRequest(
                "acme/demo",
                "main",
                ["ci"],
                RequiredApprovingReviewCount: 1,
                ExistingRulesetId: null),
            CancellationToken.None);

        Assert.Null(result.RulesetId);
        Assert.Single(handler.Requests);
        Assert.Equal(
            "/repos/acme/demo/rulesets?includes_parents=false",
            handler.Requests[0].Path);
    }

    [Fact]
    public async Task Ruleset_lookup_follows_pagination_before_updating()
    {
        var firstPage = Response(
            HttpStatusCode.OK,
            """[{"id":111,"name":"unrelated"}]""");
        firstPage.Headers.Add(
            "Link",
            "<https://api.github.com/repos/acme/demo/rulesets?includes_parents=false&page=2>; rel=\"next\"");
        var handler = new RecordingHttpMessageHandler(
            firstPage,
            Response(HttpStatusCode.OK, """[{"id":456,"name":"dsf-creation"}]"""),
            Response(HttpStatusCode.OK, """{"id":456}"""),
            Response(HttpStatusCode.OK, """{"id":123}"""));
        var client = new GitHubRestProvisioningClient(new HttpClient(handler), "test-token");

        var result = await client.EnsureBranchProtectionRulesetAsync(
            new EnsureBranchProtectionRulesetRequest(
                "acme/demo",
                "main",
                ["ci"],
                RequiredApprovingReviewCount: 1,
                ExistingRulesetId: null),
            CancellationToken.None);

        Assert.Equal(456, result.RulesetId);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);
        Assert.Equal("/repos/acme/demo/rulesets/456", handler.Requests[2].Path);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) =>
        new(status)
        {
            Content = json is null ? null : new StringContent(json),
        };

    private sealed class RecordingHttpMessageHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<RecordedHttpRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(
                new RecordedHttpRequest(
                    request.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Content is null
                        ? null
                        : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("No response configured.");
        }
    }

    private sealed record RecordedHttpRequest(HttpMethod Method, string Path, string? Body);
}
