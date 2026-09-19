using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed partial class PreviewPreparationTransport
{
    internal string? PublisherWorkflowEvent { get; set; }
    internal bool PublisherPermissionDrift { get; set; }
    internal List<JsonNode> PublisherTokenRequests { get; } = [];
    internal List<string> PublisherKeyPrincipals { get; } = [];
    internal const string PublisherToken = "native-test-publisher-token-must-not-leave-auth";
    internal List<JsonNode> PublishedIssues { get; } = [];
    internal string? PublicationFailure { get; set; }
    internal int IssuePosts { get; private set; }
    internal Func<CancellationToken, Task>? BeforeIssueResponse { get; set; }
    internal void ResetPublications() { IssuePosts = 0; PublishedIssues.Clear(); PublicationFailure = null; BeforeIssueResponse = null; }

    private async Task<HttpResponseMessage?> PublisherGitHubAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        if (uri.Host != "api.github.com" && !uri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal)) return null;
        if (!Values().ContainsKey(FactoryPublisher.RootKey)
            || request.Headers.Authorization?.Parameter == Dsf.Runtime.Tests.SourceInventoryTransport.InstallationToken) return null;
        var authority = JsonSerializer.Deserialize<FactoryGitHubAuthority>(Values()[ProductConfigurationKeys.FactoryGitHubAuthority], FactoryGitHubJson.Options)!;
        if (uri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal))
        {
            if (uri.GetLeftPart(UriPartial.Path) != authority.DestinationSecretUri) return null;
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    "Bearer authorization=\"https://login.microsoftonline.com/" + Manifest.Identity.TenantId + "\", resource=\"https://vault.azure.net\"");
                return challenge;
            }
            var claims = Decode(request.Headers.Authorization.Parameter!.Split('.')[1]);
            Assert.Equal(authority.AuthPrincipalId, (string?)claims["oid"]);
            Assert.Equal(authority.AuthClientId, (string?)claims["appid"]);
            PublisherKeyPrincipals.Add((string)claims["oid"]!);
            return Reply(new { id = authority.DestinationSecretUri, value = RepositoryReviewFixture.OriginalAppPrivateKey, attributes = new { enabled = true } });
        }
        if (request.Headers.Authorization?.Parameter != PublisherToken)
        {
            var jwt = request.Headers.Authorization?.Parameter?.Split('.') ?? [];
            Assert.Equal(3, jwt.Length);
            using var key = RSA.Create();
            key.ImportFromPem(RepositoryReviewFixture.OriginalAppPrivateKey);
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Bytes(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            Assert.Equal(authority.AppId.ToString(), (string?)Decode(jwt[1])["iss"]);
        }
        JsonNode Initial(string endpoint) => JsonNode.Parse(scenario.Machine.Boundary.Repository.AppResponses[endpoint].Json)!;
        if (path == "/app")
        {
            var app = Initial("/app");
            if (PublisherPermissionDrift) app["permissions"]!["actions"] = "write";
            return Reply(app);
        }
        if (path is "/app/installations/42" or "/repos/acme/renamed/installation") return Reply(Initial("/app/installations/42"));
        if (path == "/app/installations/42/access_tokens")
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(913, (int)body["repository_ids"]![0]!);
            Assert.Single(body["repository_ids"]!.AsArray());
            Assert.Equal(new[] { "actions", "contents", "issues", "metadata", "repository_hooks" }, body["permissions"]!.AsObject().Select(p => p.Key).Order());
            Assert.Equal("write", (string?)body["permissions"]!["issues"]);
            Assert.All(body["permissions"]!.AsObject().Where(p => p.Key != "issues"), p => Assert.Equal("read", (string?)p.Value));
            PublisherTokenRequests.Add(body.DeepClone());
            var minted = Reply(new { token = PublisherToken, expires_at = scenario.Source.Source!.Clock.Now.AddHours(1),
                repository_selection = "selected", permissions = body["permissions"], repositories = new[] { new { id = 913 } } });
            minted.StatusCode = HttpStatusCode.Created;
            return minted;
        }
        Assert.Equal(PublisherToken, request.Headers.Authorization?.Parameter);
        if (path == "/repos/acme/renamed/issues" && request.Method == HttpMethod.Post)
        {
            IssuePosts++;
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(new[] { "body", "labels", "title" }, payload.AsObject().Select(p => p.Key).Order());
            Assert.Equal(new[] { "dsf:proposal" }, payload["labels"]!.AsArray().Select(l => (string)l!));
            if (PublicationFailure == "rejected")
            {
                var rejected = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("{}") };
                rejected.Headers.Add("X-GitHub-Request-Id", "native-rejection");
                return rejected;
            }
            var number = 71 + PublishedIssues.Count;
            var issue = JsonSerializer.SerializeToNode(new
            {
                id = 97000 + number, node_id = "I_NATIVE_" + number, number,
                title = (string)payload["title"]!, body = (string)payload["body"]!,
                state = "open", assignees = Array.Empty<object>(), milestone = (object?)null,
                labels = new[] { new { name = "dsf:proposal" } }, created_at = scenario.Source.Source!.Clock.Now,
                url = "https://api.github.com/repos/acme/renamed/issues/" + number,
                html_url = "https://github.com/acme/renamed/issues/" + number,
                repository_url = "https://api.github.com/repos/acme/renamed"
            })!;
            PublishedIssues.Add(issue.DeepClone());
            if (BeforeIssueResponse is not null) await BeforeIssueResponse(token);
            if (PublicationFailure == "lost") throw new HttpRequestException("Lost native create response.");
            if (PublicationFailure == "canceled") throw new OperationCanceledException(token);
            var created = Reply(issue);
            created.StatusCode = HttpStatusCode.Created;
            created.Headers.Add("X-GitHub-Request-Id", "native-create-" + number);
            return created;
        }
        Assert.Equal(HttpMethod.Get, request.Method);
        if (path == "/installation/repositories") return Reply(new { total_count = 1, repositories = new[] { new { id = 913 } } });
        if (path == "/repositories/913") return Reply(new { id = 913, owner = new { id = 81 }, full_name = "acme/renamed",
            default_branch = "release/2026", archived = false, disabled = false, has_issues = true });
        if (path.StartsWith("/repos/acme/renamed/git/ref/heads/", StringComparison.Ordinal))
            return Reply(new { @ref = "refs/heads/release/2026", @object = new { type = "commit", sha = new string('a', 40) } });
        if (path.Contains("/labels/", StringComparison.Ordinal))
            return Reply(new { id = 10, name = "dsf:proposal", description = DecideRepositoryLabels.Meanings["dsf:proposal"] });
        if (path.EndsWith("/actions/workflows", StringComparison.Ordinal))
            return PublisherWorkflowEvent is null ? Reply(new { total_count = 0, workflows = Array.Empty<object>() })
                : Reply(new { total_count = 1, workflows = new[] { new { id = 11, state = "active", path = ".github/workflows/existing.yml" } } });
        if (path.EndsWith("/contents/.github/workflows/existing.yml", StringComparison.Ordinal))
        {
            var content = $"on: {PublisherWorkflowEvent}\njobs:\n  ci:\n    steps:\n      - run: dotnet test\n";
            return Reply(new { type = "file", encoding = "base64", sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content))),
                size = Encoding.UTF8.GetByteCount(content), content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) });
        }
        if (path.EndsWith("/hooks", StringComparison.Ordinal)) return Reply(Array.Empty<object>());
        throw new InvalidOperationException("Unexpected native publisher provider operation: " + request.Method + " " + path);

        static byte[] Bytes(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
        static JsonNode Decode(string value) => JsonNode.Parse(Bytes(value))!;
    }
}
