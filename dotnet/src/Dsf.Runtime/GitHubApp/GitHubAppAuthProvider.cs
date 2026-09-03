using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dsf.Runtime.GitHubApp;

/// <summary>
/// Mints GitHub App installation access tokens: fetches the app's private key
/// from <see cref="IPrivateKeySecretReader"/>, signs a short-lived App JWT with
/// it, exchanges that JWT for an installation access token via the GitHub REST
/// API, and caches the result until shortly before it expires. This is the
/// production auth mechanism -- the same <c>GITHUB_APP_ID</c>,
/// <c>GITHUB_INSTALLATION_ID</c>, <c>GITHUB_APP_PRIVATE_KEY_SECRET</c> and
/// <c>AZURE_KEYVAULT_URI</c> settings the Python runtime resolves, not a
/// personal access token.
/// </summary>
public sealed class GitHubAppAuthProvider : IGitHubAuthProvider
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan JwtBackdate = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(9);

    private readonly string appId;
    private readonly string installationId;
    private readonly Uri keyVaultUri;
    private readonly string privateKeySecretName;
    private readonly IPrivateKeySecretReader secretReader;
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    private string? cachedToken;
    private DateTimeOffset cachedExpiresAt = DateTimeOffset.MinValue;

    public GitHubAppAuthProvider(
        string appId,
        string installationId,
        Uri keyVaultUri,
        string privateKeySecretName,
        IPrivateKeySecretReader secretReader,
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        this.appId = appId.Trim();
        this.installationId = installationId.Trim();
        this.keyVaultUri = keyVaultUri;
        this.privateKeySecretName = privateKeySecretName.Trim();
        this.secretReader = secretReader;
        this.httpClient = httpClient;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (cachedToken is not null && cachedExpiresAt - RefreshBuffer > now)
        {
            return cachedToken;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (cachedToken is not null && cachedExpiresAt - RefreshBuffer > now)
            {
                return cachedToken;
            }

            var privateKeyPem = await secretReader.GetSecretAsync(keyVaultUri, privateKeySecretName, cancellationToken);
            var jwt = BuildAppJwt(appId, privateKeyPem, now);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("dsf-runtime");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"GitHub refused to mint an installation access token for app '{appId}' installation "
                    + $"'{installationId}' ({(int)response.StatusCode}): {body}");
            }

            using var document = JsonDocument.Parse(body);
            var token = document.RootElement.TryGetProperty("token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    $"GitHub accepted the app JWT for '{appId}' but answered no installation access token: {body}");
            }

            var expiresAt = document.RootElement.TryGetProperty("expires_at", out var expiresAtElement)
                && DateTimeOffset.TryParse(expiresAtElement.GetString(), out var parsed)
                    ? parsed
                    : now + JwtLifetime;

            cachedToken = token;
            cachedExpiresAt = expiresAt;
            return token;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>
    /// Builds and signs the short-lived RS256 App JWT GitHub requires to mint an
    /// installation access token. Built from .NET's own RSA/JSON primitives
    /// rather than an external JWT library, since the shape needed here is three
    /// base64url segments and an RSA-SHA256 signature.
    /// </summary>
    private static string BuildAppJwt(string appId, string privateKeyPem, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = (now - JwtBackdate).ToUnixTimeSeconds(),
            exp = (now + JwtLifetime).ToUnixTimeSeconds(),
            iss = appId,
        });

        var unsigned = $"{Base64UrlEncode(header)}.{Base64UrlEncode(payload)}";
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
