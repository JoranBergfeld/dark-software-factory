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

        if (!text.Contains("code=", StringComparison.Ordinal)
            && !Uri.TryCreate(text, UriKind.Absolute, out _)
            && !text.StartsWith('?'))
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
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
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

            var code = context.Request.QueryString["code"];
            if (string.IsNullOrWhiteSpace(code))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteResponseAsync(
                    context,
                    "No GitHub App manifest code received. Return to GitHub to finish App creation.",
                    "text/plain; charset=utf-8",
                    cancellationToken);
                continue;
            }

            await WriteResponseAsync(
                context,
                "GitHub App creation callback received. Return to the terminal for the installation link. "
                    + "Bootstrap is not complete until the App is installed and credentials are stored.",
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

internal sealed record GitHubInstallationDiscovery(string Id, string Selection);

internal sealed class GitHubAppBootstrapClient(
    HttpClient httpClient,
    Func<OwnerBootstrapRequest, CancellationToken, Task<string>> captureCode,
    Func<OwnerGitHubCredentials, CancellationToken, Task<GitHubInstallationDiscovery>> discoverInstallation,
    IGitHubAppRecoveryStore? recoveryStore = null,
    ICliTerminal? terminal = null)
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
            (credentials, cancellationToken) => DiscoverInstallationDetailsAsync(httpClient, credentials, cancellationToken),
            terminal: terminal);
    }

    public async Task<OwnerGitHubCredentials> GetOrCreateAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        var uninstalled = await recoveryStore.LoadAsync(request.AppName, cancellationToken);
        if (uninstalled is null)
        {
            var code = GitHubAppManifest.ParseCode(await captureCode(request, cancellationToken));
            terminal?.WriteLine("[dsf] Exchanging GitHub App creation callback for credentials...");
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

            var slug = payload.RootElement.GetProperty("slug").GetString();
            if (string.IsNullOrWhiteSpace(slug))
            {
                throw new InvalidOperationException("GitHub App manifest conversion returned no App slug.");
            }

            uninstalled = new OwnerGitHubCredentials(appId, string.Empty, privateKey, Slug: slug);
            await recoveryStore.SaveAsync(request.AppName, uninstalled, cancellationToken);
            terminal?.WriteLine($"[dsf] GitHub App created (ID {appId}); recovery credentials saved locally until Key Vault storage succeeds.");
        }
        else
        {
            terminal?.WriteLine($"[dsf] Resuming GitHub App {uninstalled.AppId} from saved recovery credentials; no new App will be created.");
        }

        var installationUrl = string.IsNullOrWhiteSpace(uninstalled.Slug)
            ? "https://github.com/settings/apps"
            : $"https://github.com/apps/{Uri.EscapeDataString(uninstalled.Slug)}/installations/new";
        terminal?.WriteLine("[dsf] App creation and installation are separate steps. Open this URL and install the App for selected repositories:");
        terminal?.WriteLine(installationUrl);
        terminal?.WriteLine("[dsf] Waiting for GitHub App installation (up to 5 minutes; checking every 5 seconds)...");
        var installation = await discoverInstallation(uninstalled, cancellationToken);
        if (string.IsNullOrWhiteSpace(installation.Id))
        {
            throw new InvalidOperationException("GitHub App installation discovery returned no installation id.");
        }

        terminal?.WriteLine($"[dsf] GitHub App installation found (ID {installation.Id}, repositories: {installation.Selection}).");
        return uninstalled with
        {
            InstallationId = installation.Id,
            InstallationSelection = installation.Selection,
        };
    }

    public Task CompleteAsync(OwnerBootstrapRequest request, CancellationToken cancellationToken) =>
        recoveryStore.DeleteAsync(request.AppName, cancellationToken);

    internal static async Task<string> DiscoverInstallationAsync(
        HttpClient httpClient,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken) =>
        (await DiscoverInstallationDetailsAsync(httpClient, credentials, cancellationToken)).Id;

    internal static async Task<GitHubInstallationDiscovery> DiscoverInstallationDetailsAsync(
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
                    && selection.GetString() is "selected" or "all")
                .Select(installation => new GitHubInstallationDiscovery(
                    installation.GetProperty("id").GetInt64().ToString(),
                    installation.GetProperty("repository_selection").GetString()!))
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

    internal static async Task<string> CaptureCodeAsync(
        ICliTerminal terminal,
        string appName,
        CancellationToken cancellationToken,
        Uri? callbackUri = null,
        Func<string, CancellationToken, Task>? openBrowser = null)
    {
        using var listener = new GitHubAppLoopbackListener(callbackUri ?? CallbackUri);
        listener.Start();
        var manifest = GitHubAppManifest.Create(appName, listener.CallbackUri) with
        {
            Url = listener.ManifestUri.AbsoluteUri,
        };
        terminal.WriteLine("[dsf] Open this GitHub App manifest URL in a browser:");
        terminal.WriteLine(listener.ManifestUri.AbsoluteUri);
        terminal.WriteLine(
            "[dsf] Create the GitHub App, then return here. Installation follows after the creation callback.");
        terminal.WriteLine(
            $"[dsf] Remote/WSL browser cannot reach localhost? Forward port {listener.CallbackUri.Port}, "
            + "or paste the redirected callback URL/code below.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var callback = listener.WaitForCodeAsync(manifest, timeout.Token);
        Task<string?>? pasted = null;
        try
        {
            if (GitHubAppBrowserOpener.ShouldLaunch(terminal.Capabilities))
            {
                await (openBrowser ?? ((url, token) => TryOpenBrowserAsync(terminal, url, token)))(
                    listener.ManifestUri.AbsoluteUri, timeout.Token);
            }

            terminal.WriteLine("[dsf] Waiting for GitHub App creation callback (up to 15 minutes)...");
            string raw;
            if (terminal.Capabilities.IsInteractive && !callback.IsCompleted)
            {
                pasted = terminal.PromptSecretAsync(
                    "[dsf] Optional: paste callback URL/code, then Enter (input hidden): ", timeout.Token);
                var completed = await Task.WhenAny((Task)callback, pasted);
                raw = completed == callback ? await callback : (await pasted ?? string.Empty);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    raw = await callback;
                }
            }
            else
            {
                raw = await callback;
            }

            var code = GitHubAppManifest.ParseCode(raw);
            await timeout.CancelAsync();
            await ObserveCancellationAsync(pasted);
            terminal.WriteLine("[dsf] GitHub App creation callback received.");
            return code;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Timed out waiting for the GitHub App creation callback after 15 minutes. "
                + "Check localhost connectivity or rerun bootstrap with --github-callback '<callback-url-or-code>'.");
        }
        finally
        {
            await timeout.CancelAsync();
            await ObserveCancellationAsync(callback);
            await ObserveCancellationAsync(pasted);
        }
    }

    private static async Task ObserveCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // The losing callback/input operation is cancelled and joined before continuing.
        }
    }

    internal static async Task TryOpenBrowserAsync(
        ICliTerminal terminal,
        string url,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, IManagedProcess>? processFactory = null,
        GitHubAppBrowserLaunch? launch = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            launch ??= GitHubAppBrowserOpener.Resolve();
            if (launch is null)
            {
                terminal.WriteErrorLine("[dsf] Browser opener unavailable. Open the printed URL manually.");
                return;
            }

            var startInfo = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (launch.Subcommand is not null)
            {
                startInfo.ArgumentList.Add(launch.Subcommand);
            }

            startInfo.ArgumentList.Add(url);
            using var process = (processFactory ?? (info => new RealManagedProcess(new Process { StartInfo = info })))(startInfo);
            process.Start();
            var stdout = process.ReadStandardOutputAsync(timeout.Token);
            var stderr = process.ReadStandardErrorAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                await ObserveCancellationAsync(stdout);
                await ObserveCancellationAsync(stderr);
            }

            if (process.ExitCode != 0)
            {
                terminal.WriteErrorLine(
                    $"[dsf] Browser could not be opened automatically ({launch.FileName} exited {process.ExitCode}). "
                    + "Open the printed URL manually; bootstrap is still waiting for the callback.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            terminal.WriteLine(
                "[dsf] Browser opener is still running; continuing to wait for the callback. "
                + "If no browser opened, open the printed URL manually.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            terminal.WriteErrorLine("[dsf] Browser could not be opened automatically. Open the printed URL manually.");
        }
    }
}
