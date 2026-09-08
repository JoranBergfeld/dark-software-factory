using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dsf.Cli;

internal sealed record GitHubAppManifest(
    string Name,
    string Url,
    string RedirectUrl,
    bool Public,
    IReadOnlyDictionary<string, string> DefaultPermissions,
    IReadOnlyList<string> DefaultEvents)
{
    public static GitHubAppManifest Create(string name, Uri callbackUri) => new(
        name,
        callbackUri.AbsoluteUri,
        callbackUri.AbsoluteUri,
        false,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issues"] = "write",
            ["pull_requests"] = "write",
            ["contents"] = "write",
            ["administration"] = "write",
        },
        []);

    public string CreateHtml()
    {
        var manifest = JsonSerializer.Serialize(new
        {
            name = Name,
            url = Url,
            redirect_url = RedirectUrl,
            @public = Public,
            default_permissions = DefaultPermissions,
        });
        return $"<form action=\"https://github.com/settings/apps/new\" method=\"post\">"
            + $"<input type=\"hidden\" name=\"manifest\" value=\"{WebUtility.HtmlEncode(manifest)}\"></form>"
            + "<script>document.forms[0].submit()</script>";
    }

    public static string ParseCode(string raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new InvalidOperationException("GitHub App manifest callback code is required.");
        }

        if (!text.Contains("code=", StringComparison.Ordinal))
        {
            return text;
        }

        var query = Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri.Query
            : text.TrimStart('?');
        var code = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "code")?[1];
        return string.IsNullOrWhiteSpace(code)
            ? throw new InvalidOperationException("GitHub App manifest callback contained no code.")
            : Uri.UnescapeDataString(code);
    }
}

internal interface IGitHubAppRecoveryStore
{
    Task<OwnerGitHubCredentials?> LoadAsync(string appName, CancellationToken cancellationToken);

    Task SaveAsync(
        string appName,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken);

    Task DeleteAsync(string appName, CancellationToken cancellationToken);
}

internal sealed class NoopGitHubAppRecoveryStore : IGitHubAppRecoveryStore
{
    public static NoopGitHubAppRecoveryStore Instance { get; } = new();

    public Task<OwnerGitHubCredentials?> LoadAsync(string appName, CancellationToken cancellationToken) =>
        Task.FromResult<OwnerGitHubCredentials?>(null);

    public Task SaveAsync(
        string appName,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAsync(string appName, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FileGitHubAppRecoveryStore : IGitHubAppRecoveryStore
{
    public static FileGitHubAppRecoveryStore Instance { get; } = new();

    public async Task<OwnerGitHubCredentials?> LoadAsync(
        string appName,
        CancellationToken cancellationToken)
    {
        var path = RecoveryPath(appName);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<OwnerGitHubCredentials>(
            stream,
            cancellationToken: cancellationToken);
    }

    public async Task SaveAsync(
        string appName,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken)
    {
        var path = RecoveryPath(appName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await JsonSerializer.SerializeAsync(stream, credentials, cancellationToken: cancellationToken);
    }

    public Task DeleteAsync(string appName, CancellationToken cancellationToken)
    {
        File.Delete(RecoveryPath(appName));
        return Task.CompletedTask;
    }

    private static string RecoveryPath(string appName)
    {
        var safeName = new string(appName.Select(character =>
            char.IsLetterOrDigit(character) || character == '-' ? character : '-').ToArray());
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsf",
            $"bootstrap-{safeName}.recovery.json");
    }
}

internal sealed class GitHubAppLoopbackListener(Uri callbackUri) : IDisposable
{
    private readonly HttpListener listener = new();

    public Uri CallbackUri { get; private set; } = callbackUri;

    public Uri ManifestUri => new(CallbackUri.GetLeftPart(UriPartial.Authority) + "/");

    public void Start()
    {
        if (CallbackUri.Port == 0)
        {
            using var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            port.Start();
            CallbackUri = new UriBuilder(CallbackUri) { Port = ((IPEndPoint)port.LocalEndpoint).Port }.Uri;
        }

        listener.Prefixes.Add(CallbackUri.GetLeftPart(UriPartial.Authority) + "/");
        listener.Start();
    }

    public Task<string> WaitForCodeAsync(CancellationToken cancellationToken) =>
        WaitForCodeAsync(null, cancellationToken);

    public async Task<string> WaitForCodeAsync(
        GitHubAppManifest? manifest,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(listener.Abort);
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (manifest is not null
                && string.Equals(context.Request.Url?.AbsolutePath, ManifestUri.AbsolutePath, StringComparison.Ordinal))
            {
                await WriteResponseAsync(context, manifest.CreateHtml(), "text/html; charset=utf-8", cancellationToken);
                continue;
            }

            if (!string.Equals(context.Request.Url?.AbsolutePath, CallbackUri.AbsolutePath, StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                continue;
            }

            var code = GitHubAppManifest.ParseCode(context.Request.Url?.ToString() ?? string.Empty);
            await WriteResponseAsync(
                context,
                "GitHub App setup received. You may close this window.",
                "text/plain; charset=utf-8",
                cancellationToken);
            return code;
        }
    }

    private static async Task WriteResponseAsync(
        HttpListenerContext context,
        string content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
    }

    public void Dispose()
    {
        listener.Close();
    }
}

internal sealed record GitHubAppBrowserLaunch(string FileName, string? Subcommand = null);

internal static class GitHubAppBrowserOpener
{
    public static bool ShouldLaunch(TerminalCapabilities capabilities) => capabilities.IsInteractive;

    public static GitHubAppBrowserLaunch? ResolveLinux(Func<string, bool> fileExists) =>
        fileExists("/usr/bin/xdg-open") ? new("xdg-open")
        : fileExists("/usr/bin/gio") ? new("gio", "open")
        : null;

    public static GitHubAppBrowserLaunch? Resolve() =>
        OperatingSystem.IsMacOS() ? new("open")
        : OperatingSystem.IsWindows() ? new("cmd", "/c start \"\"")
        : ResolveLinux(File.Exists);
}

internal sealed class GitHubAppBootstrapClient(
    HttpClient httpClient,
    Func<OwnerBootstrapRequest, CancellationToken, Task<string>> captureCode,
    Func<OwnerGitHubCredentials, CancellationToken, Task<string>> discoverInstallation,
    IGitHubAppRecoveryStore? recoveryStore = null)
    : IGitHubAppBootstrapper
{
    private static readonly Uri CallbackUri = new("http://127.0.0.1:8765/callback");
    private readonly IGitHubAppRecoveryStore recoveryStore = recoveryStore ?? FileGitHubAppRecoveryStore.Instance;

    public static GitHubAppBootstrapClient Create(ICliTerminal terminal, string? callbackCode = null)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dsf-cli");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return new GitHubAppBootstrapClient(
            httpClient,
            (request, cancellationToken) => string.IsNullOrWhiteSpace(callbackCode)
                ? CaptureCodeAsync(terminal, request.AppName, cancellationToken)
                : Task.FromResult(callbackCode),
            (credentials, cancellationToken) => DiscoverInstallationAsync(httpClient, credentials, cancellationToken));
    }

    public async Task<OwnerGitHubCredentials> GetOrCreateAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        var uninstalled = await recoveryStore.LoadAsync(request.AppName, cancellationToken);
        if (uninstalled is null)
        {
            var code = GitHubAppManifest.ParseCode(await captureCode(request, cancellationToken));
            using var response = await httpClient.PostAsync(
                $"app-manifests/{Uri.EscapeDataString(code)}/conversions",
                content: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"GitHub App manifest conversion failed with {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var appId = payload.RootElement.GetProperty("id").GetInt64().ToString();
            var privateKey = payload.RootElement.GetProperty("pem").GetString();
            if (string.IsNullOrWhiteSpace(privateKey))
            {
                throw new InvalidOperationException("GitHub App manifest conversion returned no private key.");
            }

            uninstalled = new OwnerGitHubCredentials(appId, string.Empty, privateKey);
            await recoveryStore.SaveAsync(request.AppName, uninstalled, cancellationToken);
        }

        var installationId = await discoverInstallation(uninstalled, cancellationToken);
        if (string.IsNullOrWhiteSpace(installationId))
        {
            throw new InvalidOperationException("GitHub App installation discovery returned no installation id.");
        }

        var installed = uninstalled with { InstallationId = installationId };
        await recoveryStore.DeleteAsync(request.AppName, cancellationToken);
        return installed;
    }

    private static async Task<string> DiscoverInstallationAsync(
        HttpClient httpClient,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 60; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "app/installations");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                CreateAppJwt(credentials));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"GitHub App installation discovery failed with {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var installations = payload.RootElement.EnumerateArray()
                .Where(installation =>
                    installation.TryGetProperty("repository_selection", out var selection)
                    && selection.GetString() == "selected")
                .Select(installation => installation.GetProperty("id").GetInt64().ToString())
                .ToArray();
            if (installations.Length == 1)
            {
                return installations[0];
            }

            if (installations.Length > 1)
            {
                throw new InvalidOperationException(
                    "GitHub App installation discovery found more than one selected-repositories installation.");
            }

            if (attempt < 60)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "No selected-repositories GitHub App installation appeared; install the App and rerun dsf bootstrap.");
    }

    private static string CreateAppJwt(OwnerGitHubCredentials credentials)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(credentials.PrivateKey);
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = credentials.AppId,
        }));
        var data = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> CaptureCodeAsync(
        ICliTerminal terminal,
        string appName,
        CancellationToken cancellationToken)
    {
        using var listener = new GitHubAppLoopbackListener(CallbackUri);
        listener.Start();
        var manifest = GitHubAppManifest.Create(appName, listener.CallbackUri) with
        {
            Url = listener.ManifestUri.AbsoluteUri,
        };
        if (GitHubAppBrowserOpener.ShouldLaunch(terminal.Capabilities))
        {
            TryOpenBrowser(listener.ManifestUri.AbsoluteUri);
        }
        terminal.WriteLine("[dsf] Open this GitHub App manifest URL in a browser:");
        terminal.WriteLine(listener.ManifestUri.AbsoluteUri);
        terminal.WriteLine(
            "[dsf] Complete GitHub App creation and selected-repositories installation. "
            + "The browser callback completes automatically; paste it here if needed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var callback = listener.WaitForCodeAsync(manifest, timeout.Token);
        if (!terminal.Capabilities.IsInteractive)
        {
            return await callback;
        }

        var pasted = Task.Run<string?>(
            () => terminal.Prompt("[dsf] GitHub callback: "),
            cancellationToken);
        var completed = await Task.WhenAny((Task)callback, pasted);
        if (completed == callback)
        {
            return await callback;
        }

        var raw = await pasted;
        return string.IsNullOrWhiteSpace(raw) ? await callback : raw;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            var launch = GitHubAppBrowserOpener.Resolve();
            if (launch is null)
            {
                return;
            }

            var startInfo = new ProcessStartInfo(launch.FileName) { UseShellExecute = false };
            if (launch.Subcommand is not null)
            {
                startInfo.ArgumentList.Add(launch.Subcommand);
            }

            startInfo.ArgumentList.Add(url);
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The operator can still paste the callback code without a local browser opener.
        }
    }
}
