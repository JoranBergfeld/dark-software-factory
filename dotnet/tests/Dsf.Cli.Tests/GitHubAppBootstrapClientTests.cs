using Dsf.Cli;
using System.Net;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class GitHubAppBootstrapClientTests
{
    [Fact]
    public async Task Automatic_callback_stops_pending_terminal_input()
    {
        var terminal = new CallbackTerminal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var manifestUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = GitHubAppBootstrapClient.CaptureCodeAsync(
            terminal, "dsf-test", cancellation.Token,
            new Uri("http://127.0.0.1:0/callback"),
            (url, _) =>
            {
                manifestUrl.SetResult(url);
                return Task.CompletedTask;
            });
        try
        {
            var url = await manifestUrl.Task.WaitAsync(cancellation.Token);
            await terminal.PromptStarted.Task.WaitAsync(cancellation.Token);
            using var http = new HttpClient();
            using var response = await http.GetAsync($"{url}callback?code=test-code", cancellation.Token);

            Assert.Equal("test-code", await capture.WaitAsync(cancellation.Token));
            Assert.False(terminal.InputPending);
            Assert.Contains("callback received", terminal.Output);
        }
        finally
        {
            terminal.Input.TrySetResult(null);
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task Pasted_callback_is_acknowledged_without_echoing_the_code()
    {
        var terminal = new CallbackTerminal();
        terminal.Input.SetResult("test-secret-code");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var code = await GitHubAppBootstrapClient.CaptureCodeAsync(
            terminal, "dsf-test", cancellation.Token,
            new Uri("http://127.0.0.1:0/callback"), (_, _) => Task.CompletedTask);

        Assert.Equal("test-secret-code", code);
        Assert.Contains("callback received", terminal.Output);
        Assert.DoesNotContain("test-secret-code", terminal.Output);
    }

    [Fact]
    public async Task Cancelling_callback_capture_stops_pending_terminal_input()
    {
        var terminal = new CallbackTerminal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var capture = GitHubAppBootstrapClient.CaptureCodeAsync(
            terminal, "dsf-test", cancellation.Token,
            new Uri("http://127.0.0.1:0/callback"), (_, _) => Task.CompletedTask);
        await terminal.PromptStarted.Task.WaitAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.False(terminal.InputPending);
        Assert.DoesNotContain("callback received", terminal.Output);
    }

    [Fact]
    public async Task Empty_paste_keeps_waiting_for_browser_callback()
    {
        var terminal = new CallbackTerminal();
        terminal.Input.SetResult("");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? manifestUrl = null;
        var capture = GitHubAppBootstrapClient.CaptureCodeAsync(
            terminal, "dsf-test", cancellation.Token,
            new Uri("http://127.0.0.1:0/callback"),
            (url, _) =>
            {
                manifestUrl = url;
                return Task.CompletedTask;
            });
        await terminal.PromptStarted.Task.WaitAsync(cancellation.Token);
        Assert.False(capture.IsCompleted);

        using var http = new HttpClient();
        using var response = await http.GetAsync($"{manifestUrl}callback?code=test-code", cancellation.Token);

        Assert.Equal("test-code", await capture);
    }

    [Fact]
    public async Task Failed_browser_opener_is_reported_without_leaking_child_output()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(true, false, false), []);
        var process = new FailedBrowserProcess();
        System.Diagnostics.ProcessStartInfo? startInfo = null;

        await GitHubAppBootstrapClient.TryOpenBrowserAsync(
            terminal, "http://127.0.0.1:8765/", CancellationToken.None,
            info =>
            {
                startInfo = info;
                return process;
            },
            new GitHubAppBrowserLaunch("test-browser"));

        Assert.NotNull(startInfo);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(process.Waited);
        Assert.Contains("exited 1", terminal.Error);
        Assert.Contains("Open the printed URL manually", terminal.Error);
        Assert.DoesNotContain("gio:", terminal.Error);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Cancelling_browser_wait_does_not_kill_the_operators_browser()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(true, false, false), []);
        var process = new FakeManagedProcess();
        using var cancellation = new CancellationTokenSource();
        var opening = GitHubAppBootstrapClient.TryOpenBrowserAsync(
            terminal, "http://127.0.0.1:8765/", cancellation.Token, _ => process,
            new GitHubAppBrowserLaunch("test-browser"));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(0, process.KillCallCount);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Long_running_browser_opener_does_not_kill_the_operators_browser()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(true, false, false), []);
        var process = new FakeManagedProcess();

        await GitHubAppBootstrapClient.TryOpenBrowserAsync(
            terminal, "http://127.0.0.1:8765/", CancellationToken.None, _ => process,
            new GitHubAppBrowserLaunch("test-browser"));

        Assert.Equal(0, process.KillCallCount);
        Assert.Contains("Browser opener is still running", terminal.Output);
        Assert.Empty(terminal.Error);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Manifest_conversion_reports_separate_installation_step()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var recovery = new RecordingGitHubAppRecoveryStore();
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":7,"pem":"test-private-key","slug":"dsf-test"}"""),
        });
        var client = new GitHubAppBootstrapClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            (_, _) => Task.FromResult("test-secret-code"),
            (_, _) =>
            {
                Assert.Contains("https://github.com/apps/dsf-test/installations/new", terminal.Output);
                Assert.Contains("Waiting", terminal.Output);
                return Task.FromResult(new GitHubInstallationDiscovery("42", "selected"));
            },
            recovery,
            terminal);

        await client.GetOrCreateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal("dsf-test", (await recovery.LoadAsync(SampleRequest().AppName, CancellationToken.None))?.Slug);
        Assert.Contains("Exchanging", terminal.Output);
        Assert.Contains("installation found", terminal.Output);
        Assert.DoesNotContain("test-private-key", terminal.Output);
        Assert.DoesNotContain("test-secret-code", terminal.Output);
    }

    [Fact]
    public async Task Get_or_create_exchanges_manifest_code_for_owner_credentials()
    {
        var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent("""
                    {"id":7,"pem":"-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----","slug":"dsf-test"}
                    """),
            });
        var client = new GitHubAppBootstrapClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            (_, _) => Task.FromResult("temporary-code"),
            (_, _) => Task.FromResult(new GitHubInstallationDiscovery("42", "selected")),
            NoopGitHubAppRecoveryStore.Instance);

        var credentials = await client.GetOrCreateAsync(
            new OwnerBootstrapRequest(
                "dsf-sbx-20260907", "rg-dsf-app", "kvdsfsbx20260907",
                "appcsdsfsbx20260907", "swedencentral"),
            CancellationToken.None);

        Assert.Equal("7", credentials.AppId);
        Assert.Equal("42", credentials.InstallationId);
        Assert.Equal("selected", credentials.InstallationSelection);
        Assert.Contains("/app-manifests/temporary-code/conversions", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
    }

    [Fact]
    public async Task Get_or_create_recovers_converted_app_when_installation_discovery_failed()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var recovery = new RecordingGitHubAppRecoveryStore();
        var converted = new OwnerGitHubCredentials("7", string.Empty, CreatePrivateKeyPem());
        await recovery.SaveAsync("dsf-sbx-20260907", converted, CancellationToken.None);
        var client = new GitHubAppBootstrapClient(
            new HttpClient(new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            {
                BaseAddress = new Uri("https://api.github.com/"),
            },
            (_, _) => throw new InvalidOperationException("manifest should not be captured"),
            (_, _) => Task.FromResult(new GitHubInstallationDiscovery("42", "all")),
            recovery,
            terminal);

        var credentials = await client.GetOrCreateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal("7", credentials.AppId);
        Assert.Equal("42", credentials.InstallationId);
        Assert.Equal("all", credentials.InstallationSelection);
        Assert.False(recovery.Deleted);
        Assert.Contains("Resuming GitHub App 7", terminal.Output);
        Assert.Contains("https://github.com/settings/apps", terminal.Output);
        Assert.DoesNotContain("Exchanging", terminal.Output);
    }

    [Fact]
    public async Task Installation_discovery_accepts_a_single_all_repositories_installation()
    {
        var handler = new StubHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"id":42,"repository_selection":"all"}]"""),
        });

        var installationId = await GitHubAppBootstrapClient.DiscoverInstallationAsync(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            new OwnerGitHubCredentials("7", string.Empty, CreatePrivateKeyPem()),
            CancellationToken.None);

        Assert.Equal("42", installationId);
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

    [Theory]
    [InlineData("http://127.0.0.1:8765/callback")]
    [InlineData("?state=abc123")]
    [InlineData("http://127.0.0.1:8765/callback?code=")]
    public void Callback_code_parser_rejects_urls_without_a_code(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => GitHubAppManifest.ParseCode(raw));
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
    public async Task Loopback_callback_explains_that_installation_is_still_pending()
    {
        using var listener = new GitHubAppLoopbackListener(new Uri("http://127.0.0.1:0/callback"));
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new HttpClient();
        var code = listener.WaitForCodeAsync(cancellation.Token);

        var page = await client.GetStringAsync($"{listener.CallbackUri}?code=manifest-code", cancellation.Token);

        Assert.Equal("manifest-code", await code);
        Assert.Contains("Return to the terminal", page);
        Assert.Contains("install", page);
    }

    [Fact]
    public async Task Loopback_listener_rejects_missing_code_and_keeps_waiting()
    {
        using var listener = new GitHubAppLoopbackListener(new Uri("http://127.0.0.1:0/callback"));
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new HttpClient();
        var code = listener.WaitForCodeAsync(cancellation.Token);

        using var invalid = await client.GetAsync(listener.CallbackUri, cancellation.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.False(code.IsCompleted);

        using var valid = await client.GetAsync($"{listener.CallbackUri}?code=manifest-code", cancellation.Token);
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

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> responseFactory;

        public StubHttpMessageHandler(HttpResponseMessage response)
            : this(() => response)
        {
        }

        public StubHttpMessageHandler(Func<HttpResponseMessage> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(responseFactory());
        }
    }

    private static OwnerBootstrapRequest SampleRequest() =>
        new(
            "dsf-sbx-20260907",
            "rg-dsf-app",
            "kvdsfsbx20260907",
            "appcsdsfsbx20260907",
            "swedencentral");

    private static string CreatePrivateKeyPem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

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

    private sealed class FailedBrowserProcess : IManagedProcess
    {
        public int ExitCode => 1;
        public bool Waited { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() { }
        public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) => Task.FromResult("");
        public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
            Task.FromResult("gio: http://127.0.0.1:8765/: Operation not supported");
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Waited = true;
            return Task.CompletedTask;
        }
        public void Kill(bool entireProcessTree) => throw new InvalidOperationException("Already exited.");
        public void Dispose() => Disposed = true;
    }

    private sealed class CallbackTerminal : ICliTerminal
    {
        private readonly System.Text.StringBuilder output = new();
        public TerminalCapabilities Capabilities => new(true, false, false);
        public TaskCompletionSource<string?> Input { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InputPending { get; private set; }
        public string Output => output.ToString();
        public void WriteLine(string value) => output.AppendLine(value);
        public void WriteErrorLine(string value) => output.AppendLine(value);

        public string? Prompt(string message)
        {
            InputPending = true;
            PromptStarted.TrySetResult();
            try
            {
                return Input.Task.GetAwaiter().GetResult();
            }
            finally
            {
                InputPending = false;
            }

        }

        public async Task<string?> PromptSecretAsync(string message, CancellationToken cancellationToken)
        {
            InputPending = true;
            PromptStarted.TrySetResult();
            try
            {
                return await Input.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                InputPending = false;
            }
        }
    }
}
