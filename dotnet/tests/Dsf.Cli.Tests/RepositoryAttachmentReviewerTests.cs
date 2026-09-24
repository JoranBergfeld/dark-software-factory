using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Dsf.Cli;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class RepositoryAttachmentReviewerTests
{
    [Fact]
    public async Task Missing_token_fails_loudly_before_any_github_read()
    {
        var handler = new RecordingHandler();
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), token: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reviewer.ReviewAsync(
                new RepositoryAttachmentReviewRequest("acme", "demo", null, null, null),
                CancellationToken.None));

        Assert.Contains("GH_TOKEN", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_repository_is_denied_and_blocks_without_repository_identity()
    {
        var handler = new RecordingHandler(Response(HttpStatusCode.NotFound));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token");

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "missing", null, null, null),
            CancellationToken.None);

        Assert.Null(review.Repository);
        Assert.Equal(RepositoryAttachmentEligibility.Blocked, review.Eligibility);
        Assert.Contains(
            review.Prerequisites,
            prerequisite => prerequisite.Name == "repository-access" && prerequisite.State == PrerequisiteState.Denied);
        Assert.Contains(review.BlockingReasons, reason => reason.Contains("not found", StringComparison.OrdinalIgnoreCase));
        AssertOnlyReads(handler);
    }

    [Fact]
    public async Task Readable_repository_reports_observed_owner_name_and_default_branch_after_rename()
    {
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":42,"name":"renamed-repo","owner":{"login":"acme"},"default_branch":"trunk","archived":false,"has_issues":true,"private":true}"""),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.NotFound));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token");

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "old-name", null, null, null),
            CancellationToken.None);

        Assert.NotNull(review.Repository);
        Assert.Equal("42", review.Repository!.RepositoryId);
        Assert.Equal("acme", review.Repository.Owner);
        Assert.Equal("renamed-repo", review.Repository.Name);
        Assert.Equal("trunk", review.Repository.DefaultBranch);
        Assert.False(review.Repository.Archived);
        Assert.True(review.Repository.IssuesEnabled);
        AssertOnlyReads(handler);
    }

    [Fact]
    public async Task Archived_and_issues_disabled_repository_is_blocked_without_repair()
    {
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":1,"name":"demo","owner":{"login":"acme"},"default_branch":"main","archived":true,"has_issues":false,"private":true}"""),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.NotFound));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token");

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "demo", null, null, null),
            CancellationToken.None);

        Assert.Equal(RepositoryAttachmentEligibility.Blocked, review.Eligibility);
        Assert.Contains(review.BlockingReasons, reason => reason.Contains("archived", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.BlockingReasons, reason => reason.Contains("issues disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("unarchive", string.Join(' ', handler.Requests.Select(r => r.Path)), StringComparison.OrdinalIgnoreCase);
        AssertOnlyReads(handler);
    }

    [Fact]
    public async Task Existing_label_with_unrelated_description_is_flagged_as_conflicting_and_never_altered()
    {
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":1,"name":"demo","owner":{"login":"acme"},"default_branch":"main","archived":false,"has_issues":true,"private":true}"""),
            Response(HttpStatusCode.OK, """{"name":"dsf:proposal","color":"ff0000","description":"unrelated team label"}"""),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.NotFound));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token");

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "demo", null, null, null),
            CancellationToken.None);

        var proposalLabel = Assert.Single(review.Labels, label => label.Name == "dsf:proposal");
        Assert.True(proposalLabel.Exists);
        Assert.True(proposalLabel.ConflictsWithExpectedMeaning);
        AssertOnlyReads(handler);
    }

    [Fact]
    public async Task Workflow_triggered_on_issues_blocks_the_mutation_class_as_visible_technical_proof()
    {
        var workflowYaml = "on:\n  issues:\n    types: [opened]\njobs:\n  x:\n    runs-on: ubuntu-latest\n";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(workflowYaml));
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":1,"name":"demo","owner":{"login":"acme"},"default_branch":"main","archived":false,"has_issues":true,"private":true}"""),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.OK, """[{"type":"file","name":"auto.yml","path":".github/workflows/auto.yml"}]"""),
            Response(HttpStatusCode.OK, $$"""{"content":"{{encoded}}"}"""),
            Response(HttpStatusCode.NotFound));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token");

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "demo", null, null, "no external bots"),
            CancellationToken.None);

        var finding = Assert.Single(review.Automation, f => f.Evidence == AutomationEvidence.VisibleWorkflow);
        Assert.True(finding.Blocks);
        Assert.Contains(review.Automation, f => f.Evidence == AutomationEvidence.OwnerDeclared && !f.Blocks);
        Assert.Equal(RepositoryAttachmentEligibility.Blocked, review.Eligibility);
        Assert.Contains(review.BlockingReasons, reason => reason.Contains("automation", StringComparison.OrdinalIgnoreCase));
        AssertOnlyReads(handler);
    }

    [Fact]
    public async Task Missing_owner_control_plane_reports_unknown_installation_prerequisite()
    {
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":1,"name":"demo","owner":{"login":"acme"},"default_branch":"main","archived":false,"has_issues":true,"private":true}"""),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.NotFound));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token");

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "demo", null, null, null),
            CancellationToken.None);

        var registration = Assert.Single(review.Prerequisites, p => p.Name == "app-registration");
        Assert.Equal(PrerequisiteState.Unknown, registration.State);
        Assert.Equal(RepositoryAttachmentEligibility.Unknown, review.Eligibility);
    }

    [Fact]
    public async Task Suspended_installation_is_denied_and_blocks()
    {
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":1,"name":"demo","owner":{"login":"acme"},"default_branch":"main","archived":false,"has_issues":true,"private":true}"""),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.NotFound),
            Response(
                HttpStatusCode.OK,
                """{"total_count":1,"installations":[{"id":9,"account":{"login":"acme"},"repository_selection":"all","suspended_at":"2020-01-01T00:00:00Z","permissions":{"issues":"write","contents":"read","metadata":"read"}}]}"""));
        var reader = new FakeOwnerCredentialReader(new OwnerGitHubIdentity("app-1", "9"));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token", reader);

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "demo", "https://kv.example/", null, null),
            CancellationToken.None);

        var suspension = Assert.Single(review.Prerequisites, p => p.Name == "installation-suspension");
        Assert.Equal(PrerequisiteState.Denied, suspension.State);
        Assert.Equal(RepositoryAttachmentEligibility.Blocked, review.Eligibility);
        AssertOnlyReads(handler);
    }

    [Fact]
    public async Task Repository_not_selected_for_installation_is_pending_with_administrator_handoff()
    {
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":7,"name":"demo","owner":{"login":"acme"},"default_branch":"main","archived":false,"has_issues":true,"private":true}"""),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.NotFound),
            Response(
                HttpStatusCode.OK,
                """{"total_count":1,"installations":[{"id":9,"account":{"login":"acme"},"repository_selection":"selected","permissions":{"issues":"write","contents":"read","metadata":"read"}}]}"""),
            Response(HttpStatusCode.OK, """{"total_count":0,"repositories":[]}"""));
        var reader = new FakeOwnerCredentialReader(new OwnerGitHubIdentity("app-1", "9"));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token", reader);

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "demo", "https://kv.example/", null, null),
            CancellationToken.None);

        var membership = Assert.Single(review.Prerequisites, p => p.Name == "selected-repository-membership");
        Assert.Equal(PrerequisiteState.Pending, membership.State);
        Assert.NotNull(membership.ResponsibleActor);
        Assert.Equal(RepositoryAttachmentEligibility.Unknown, review.Eligibility);
        AssertOnlyReads(handler);
    }

    [Fact]
    public async Task Organization_sso_requirement_is_reported_as_pending()
    {
        var handler = new RecordingHandler(
            Response(
                HttpStatusCode.OK,
                """{"id":1,"name":"demo","owner":{"login":"acme"},"default_branch":"main","archived":false,"has_issues":true,"private":true}""",
                ("X-GitHub-SSO", "required; url=https://github.com/orgs/acme/sso?authorization_request=abc")),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            NotFoundLabel(),
            Response(HttpStatusCode.NotFound),
            Response(HttpStatusCode.NotFound));
        var reviewer = new GitHubRepositoryAttachmentReviewer(new HttpClient(handler), "token");

        var review = await reviewer.ReviewAsync(
            new RepositoryAttachmentReviewRequest("acme", "demo", null, null, null),
            CancellationToken.None);

        var sso = Assert.Single(review.Prerequisites, p => p.Name == "organization-sso");
        Assert.Equal(PrerequisiteState.Pending, sso.State);
    }

    private static void AssertOnlyReads(RecordingHandler handler) =>
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));

    private static HttpResponseMessage NotFoundLabel() => Response(HttpStatusCode.NotFound);

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode, string? body = null, (string Name, string Value)? header = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (header is { } value)
        {
            response.Headers.TryAddWithoutValidation(value.Name, value.Value);
        }

        return response;
    }

    private sealed class FakeOwnerCredentialReader(OwnerGitHubIdentity identity) : IOwnerCredentialReader
    {
        public Task<OwnerGitHubIdentity?> ReadIdentityFromStatusAsync(
            string ownerAppConfigEndpoint, CancellationToken cancellationToken) =>
            Task.FromResult<OwnerGitHubIdentity?>(null);

        public Task<OwnerGitHubCredentials> ReadAsync(
            string keyVaultUri, bool includePrivateKey, CancellationToken cancellationToken) =>
            Task.FromResult(new OwnerGitHubCredentials(identity.AppId, identity.InstallationId, string.Empty));
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException($"No response configured for {request.Method} {request.RequestUri}.");
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path);
}
