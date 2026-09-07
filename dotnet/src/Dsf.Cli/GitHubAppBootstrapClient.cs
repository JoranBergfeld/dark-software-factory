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

internal sealed class GitHubAppBootstrapClient(
    HttpClient httpClient,
    Func<OwnerBootstrapRequest, string> captureCode,
    Func<OwnerGitHubCredentials, CancellationToken, Task<string>> discoverInstallation)
    : IGitHubAppBootstrapper
{
    private static readonly Uri CallbackUri = new("http://127.0.0.1:8765/callback");

    public static GitHubAppBootstrapClient Create(ICliTerminal terminal)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dsf-cli");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return new GitHubAppBootstrapClient(
            httpClient,
            request => CaptureCode(terminal, request.AppName),
            (credentials, cancellationToken) => DiscoverInstallationAsync(httpClient, credentials, cancellationToken));
    }

    public async Task<OwnerGitHubCredentials> GetOrCreateAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        var code = GitHubAppManifest.ParseCode(captureCode(request));
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

        var uninstalled = new OwnerGitHubCredentials(appId, string.Empty, privateKey);
        var installationId = await discoverInstallation(uninstalled, cancellationToken);
        if (string.IsNullOrWhiteSpace(installationId))
        {
            throw new InvalidOperationException("GitHub App installation discovery returned no installation id.");
        }

        return uninstalled with { InstallationId = installationId };
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

    private static string CaptureCode(ICliTerminal terminal, string appName)
    {
        var manifest = GitHubAppManifest.Create(appName, CallbackUri);
        var path = Path.Combine(Path.GetTempPath(), $"dsf-app-manifest-{Guid.NewGuid():N}.html");
        try
        {
            var encodedManifest = WebUtility.HtmlEncode(JsonSerializer.Serialize(new
            {
                name = manifest.Name,
                url = manifest.Url,
                redirect_url = manifest.RedirectUrl,
                @public = manifest.Public,
                default_permissions = manifest.DefaultPermissions,
            }));
            File.WriteAllText(
                path,
                $"<form action=\"https://github.com/settings/apps/new\" method=\"post\"><input type=\"hidden\" name=\"manifest\" value=\"{encodedManifest}\"></form><script>document.forms[0].submit()</script>");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            TryOpenBrowser(path);
            terminal.WriteLine("[dsf] Complete GitHub App creation, install it for selected repositories, then paste the callback URL or code.");
            return terminal.Prompt("[dsf] GitHub callback: ")
                ?? throw new InvalidOperationException("GitHub App manifest callback code is required.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void TryOpenBrowser(string path)
    {
        try
        {
            var opener = OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "cmd" : "xdg-open";
            var arguments = OperatingSystem.IsWindows() ? $"/c start \"\" \"{path}\"" : path;
            Process.Start(new ProcessStartInfo(opener, arguments) { UseShellExecute = false });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The operator can still paste the callback code without a local browser opener.
        }
    }
}
