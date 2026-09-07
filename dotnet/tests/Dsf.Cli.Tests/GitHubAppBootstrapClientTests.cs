using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class GitHubAppBootstrapClientTests
{
    [Fact]
    public async Task Get_or_create_exchanges_manifest_code_for_owner_credentials()
    {
        var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent("""
                    {"id":7,"pem":"-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----"}
                    """),
            });
        var client = new GitHubAppBootstrapClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            _ => "temporary-code",
            (_, _) => Task.FromResult("42"));

        var credentials = await client.GetOrCreateAsync(
            new OwnerBootstrapRequest(
                "dsf-sbx-20260907", "rg-dsf-app", "kvdsfsbx20260907",
                "appcsdsfsbx20260907", "swedencentral"),
            CancellationToken.None);

        Assert.Equal("7", credentials.AppId);
        Assert.Equal("42", credentials.InstallationId);
        Assert.Contains("/app-manifests/temporary-code/conversions", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
    }

    [Fact]
    public void Manifest_has_only_required_permissions_and_no_webhook()
    {
        var manifest = GitHubAppManifest.Create(
            "dsf-sbx-20260907",
            new Uri("http://127.0.0.1:8765/callback"));

        Assert.Equal("write", manifest.DefaultPermissions["issues"]);
        Assert.Equal("write", manifest.DefaultPermissions["pull_requests"]);
        Assert.Equal("write", manifest.DefaultPermissions["contents"]);
        Assert.Equal("write", manifest.DefaultPermissions["administration"]);
        Assert.Empty(manifest.DefaultEvents);
        Assert.False(manifest.Public);
    }

    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("?code=abc123", "abc123")]
    [InlineData("http://127.0.0.1:8765/callback?code=abc123&state=x", "abc123")]
    public void Callback_code_parser_accepts_interactive_and_headless_forms(string raw, string expected)
    {
        Assert.Equal(expected, GitHubAppManifest.ParseCode(raw));
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }
}
