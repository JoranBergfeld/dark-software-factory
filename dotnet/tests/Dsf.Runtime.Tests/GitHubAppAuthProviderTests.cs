using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dsf.Runtime.GitHubApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// <see cref="GitHubAppAuthProvider"/> is the production auth mechanism behind
/// the filing station: it fetches the app's private key from Key Vault (through
/// a deterministic test double here, never a live secret), signs a GitHub App
/// JWT with it, and exchanges that JWT for an installation access token. No
/// personal access token is ever required.
/// </summary>
public sealed class GitHubAppAuthProviderTests
{
    private sealed record RecordedExchange(string Path, string? AuthorizationHeader);

    private sealed class StubPrivateKeySecretReader(string pem) : IPrivateKeySecretReader
    {
        public int CallCount { get; private set; }

        public Uri? RequestedVaultUri { get; private set; }

        public string? RequestedSecretName { get; private set; }

        public Task<string> GetSecretAsync(Uri vaultUri, string secretName, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedVaultUri = vaultUri;
            RequestedSecretName = secretName;
            return Task.FromResult(pem);
        }
    }

    private static string GeneratePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static string BaseAddress(WebApplication app) =>
        app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);

    private static async Task<WebApplication> StartGitHubAsync(
        List<RecordedExchange> recorded, Func<string> tokenResponse)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/app/installations/{installationId}/access_tokens", (HttpRequest request, string installationId) =>
        {
            recorded.Add(new RecordedExchange(
                $"/app/installations/{installationId}/access_tokens",
                request.Headers.Authorization.ToString()));
            return Results.Text(tokenResponse(), "application/json");
        });
        await app.StartAsync();
        return app;
    }

    private static (string Header, string Payload) DecodeJwt(string authorizationHeader)
    {
        var jwt = AuthenticationHeaderValue.Parse(authorizationHeader).Parameter!;
        var parts = jwt.Split('.');
        static string Decode(string segment)
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }

        return (Decode(parts[0]), Decode(parts[1]));
    }

    [Fact]
    public async Task Mints_an_installation_token_by_signing_an_app_jwt_with_the_key_vault_secret()
    {
        var recorded = new List<RecordedExchange>();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var github = await StartGitHubAsync(
            recorded,
            () => JsonSerializer.Serialize(new { token = "ghs_installation_token", expires_at = expiresAt }));
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var secretReader = new StubPrivateKeySecretReader(GeneratePrivateKeyPem());
            var vaultUri = new Uri("https://acme-kv.vault.azure.net/");
            var provider = new GitHubAppAuthProvider(
                appId: "12345",
                installationId: "67890",
                keyVaultUri: vaultUri,
                privateKeySecretName: "gh-app-private-key",
                secretReader: secretReader,
                httpClient: http);

            var token = await provider.GetTokenAsync(CancellationToken.None);

            Assert.Equal("ghs_installation_token", token);
            var exchange = Assert.Single(recorded);
            Assert.Equal("/app/installations/67890/access_tokens", exchange.Path);
            Assert.Equal(vaultUri, secretReader.RequestedVaultUri);
            Assert.Equal("gh-app-private-key", secretReader.RequestedSecretName);

            var (header, payload) = DecodeJwt(exchange.AuthorizationHeader!);
            using var headerDoc = JsonDocument.Parse(header);
            using var payloadDoc = JsonDocument.Parse(payload);
            Assert.Equal("RS256", headerDoc.RootElement.GetProperty("alg").GetString());
            Assert.Equal("12345", payloadDoc.RootElement.GetProperty("iss").GetString());
            Assert.True(payloadDoc.RootElement.TryGetProperty("iat", out _));
            Assert.True(payloadDoc.RootElement.TryGetProperty("exp", out _));
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task Caches_the_installation_token_until_shortly_before_it_expires()
    {
        var recorded = new List<RecordedExchange>();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var github = await StartGitHubAsync(
            recorded,
            () => JsonSerializer.Serialize(new { token = "ghs_installation_token", expires_at = expiresAt }));
        await using var host = github;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(github)}/") };
            var secretReader = new StubPrivateKeySecretReader(GeneratePrivateKeyPem());
            var provider = new GitHubAppAuthProvider(
                "12345",
                "67890",
                new Uri("https://acme-kv.vault.azure.net/"),
                "gh-app-private-key",
                secretReader,
                http);

            var first = await provider.GetTokenAsync(CancellationToken.None);
            var second = await provider.GetTokenAsync(CancellationToken.None);

            Assert.Equal(first, second);
            Assert.Equal(1, secretReader.CallCount);
            Assert.Single(recorded);
        }
        finally
        {
            await github.StopAsync();
        }
    }

    [Fact]
    public async Task A_refused_token_exchange_fails_loudly_rather_than_returning_no_token()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/app/installations/{installationId}/access_tokens", () =>
            Results.Json(new { message = "Bad credentials" }, statusCode: 401));
        await app.StartAsync();
        await using var host = app;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{BaseAddress(app)}/") };
            var secretReader = new StubPrivateKeySecretReader(GeneratePrivateKeyPem());
            var provider = new GitHubAppAuthProvider(
                "12345",
                "67890",
                new Uri("https://acme-kv.vault.azure.net/"),
                "gh-app-private-key",
                secretReader,
                http);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.GetTokenAsync(CancellationToken.None));

            Assert.Contains("12345", exception.Message);
            Assert.Contains("Bad credentials", exception.Message);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
