using Dsf.Cli;
using System.Net;
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
            (_, _) => Task.FromResult("temporary-code"),
            (_, _) => Task.FromResult("42"),
            NoopGitHubAppRecoveryStore.Instance);

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
    public async Task Get_or_create_recovers_converted_app_when_installation_discovery_failed()
    {
        var recovery = new RecordingGitHubAppRecoveryStore();
        var converted = new OwnerGitHubCredentials("7", string.Empty, SamplePem);
        await recovery.SaveAsync("dsf-sbx-20260907", converted, CancellationToken.None);
        var client = new GitHubAppBootstrapClient(
            new HttpClient(new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            {
                BaseAddress = new Uri("https://api.github.com/"),
            },
            (_, _) => throw new InvalidOperationException("manifest should not be captured"),
            (_, _) => Task.FromResult("42"),
            recovery);

        var credentials = await client.GetOrCreateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal("7", credentials.AppId);
        Assert.Equal("42", credentials.InstallationId);
        Assert.True(recovery.Deleted);
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

    [Fact]
    public async Task Loopback_listener_returns_the_callback_code()
    {
        using var listener = new GitHubAppLoopbackListener(new Uri("http://127.0.0.1:0/callback"));
        listener.Start();

        using var client = new HttpClient();
        var code = listener.WaitForCodeAsync(CancellationToken.None);
        var response = await client.GetAsync($"{listener.CallbackUri}?code=manifest-code");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("manifest-code", await code);
    }

    [Fact]
    public async Task Loopback_listener_preserves_cancellation_when_no_callback_arrives()
    {
        using var listener = new GitHubAppLoopbackListener(new Uri("http://127.0.0.1:0/callback"));
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => listener.WaitForCodeAsync(cancellation.Token));
    }

    [Fact]
    public void Linux_browser_opener_falls_back_to_gio()
    {
        var opener = GitHubAppBrowserOpener.ResolveLinux(path => path == "/usr/bin/gio");

        Assert.Equal(new GitHubAppBrowserLaunch("gio", "open"), opener);
    }

    [Fact]
    public void Headless_browser_opening_is_disabled()
    {
        Assert.False(GitHubAppBrowserOpener.ShouldLaunch(new TerminalCapabilities(
            IsInteractive: false,
            SupportsAnsi: false,
            SupportsEmoji: false)));
    }

    [Fact]
    public async Task Loopback_listener_serves_manifest_form_before_receiving_callback()
    {
        var manifest = GitHubAppManifest.Create(
            "dsf-sbx-20260907",
            new Uri("http://127.0.0.1:0/callback"));
        using var listener = new GitHubAppLoopbackListener(new Uri(manifest.RedirectUrl));
        listener.Start();
        manifest = manifest with { Url = listener.ManifestUri.AbsoluteUri, RedirectUrl = listener.CallbackUri.AbsoluteUri };

        var callback = listener.WaitForCodeAsync(manifest, CancellationToken.None);
        using var client = new HttpClient();
        var manifestPage = await client.GetStringAsync(listener.ManifestUri);

        Assert.Contains("https://github.com/settings/apps/new", manifestPage);
        Assert.Contains("name=\"manifest\"", manifestPage);
        Assert.Contains("\"name\":\"dsf-sbx-20260907\"", WebUtility.HtmlDecode(manifestPage));

        await client.GetAsync($"{listener.CallbackUri}?code=manifest-code");
        Assert.Equal("manifest-code", await callback);
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

    private static OwnerBootstrapRequest SampleRequest() =>
        new(
            "dsf-sbx-20260907",
            "rg-dsf-app",
            "kvdsfsbx20260907",
            "appcsdsfsbx20260907",
            "swedencentral");

    private const string SamplePem = """
        -----BEGIN RSA PRIVATE KEY-----
        MIIBOgIBAAJBALs+zZz5p+8QYq+R2I+uZgqD7Njfom7UFYkB+z5eN8nJ5T5P
        1qZ3Q2s3V3TQDjM71L24f0Rt7zG1u7u8dxMCAwEAAQJAYwE3v+L4i8Ue9dQH
        1eV7xZx2Zp1ZJ1Uu4bM/7n7RGh9U/FXxVC4wpT8OyXHJ3VhHxdpxtn8iMwxm
        bY/1AQIhAPrJx0jhDaQRvqE1W4/eXrS+VJC8zRzZ5jzqvx6AVTqZAiEAv8Q9
        yTGLtd1h3osnWBUnuO4JrV+uXl3c0O8qZn54sUCIQDHVLjWlCnmfBzFaAX3m
        pO9dUhjOdR0i8v0epNQlzsyAQIgT++05zzYjAzYsS1NQxkrfCE07INcJY2f
        FYU4AiYXAAECIQCkkWJwksUf/7bqtbYVLDl2d8dMjdgux0hHV50qR0Q3Wg==
        -----END RSA PRIVATE KEY-----
        """;

    internal sealed class RecordingGitHubAppRecoveryStore : IGitHubAppRecoveryStore
    {
        private OwnerGitHubCredentials? credentials;

        public bool Deleted { get; private set; }

        public Task<OwnerGitHubCredentials?> LoadAsync(string appName, CancellationToken cancellationToken) =>
            Task.FromResult(credentials);

        public Task SaveAsync(
            string appName,
            OwnerGitHubCredentials credentials,
            CancellationToken cancellationToken)
        {
            this.credentials = credentials;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string appName, CancellationToken cancellationToken)
        {
            Deleted = true;
            credentials = null;
            return Task.CompletedTask;
        }
    }
}
