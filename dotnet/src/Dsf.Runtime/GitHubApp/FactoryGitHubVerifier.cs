using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime.GitHubApp;

internal sealed class FactoryGitHubVerifier(HttpClient http, TokenCredential credential, TimeProvider clock)
{
    private string? verifiedReadToken;
    private static readonly IReadOnlyDictionary<string, string> ReadPermissions =
        new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "read" };

    public async Task<GitHubAuthorityProof> VerifyAsync(FactoryGitHubAuthority authority, CancellationToken token)
    {
        verifiedReadToken = null;
        var operation = "GET versioned factory signing key";
        var admin = "factory Key Vault / network administrator";
        try
        {
            var secretUri = new Uri(authority.DestinationSecretUri ?? throw new JsonException());
            var parts = secretUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (secretUri.Scheme != "https" || !secretUri.IsDefaultPort || secretUri.Query.Length != 0
                || secretUri.Fragment.Length != 0 || secretUri.UserInfo.Length != 0
                || parts is not ["secrets", "github-app-private-key", { Length: 32 }]
                || parts[2].Any(c => !char.IsAsciiHexDigit(c))
                || secretUri.Host != authority.DestinationVaultId.Split('/').Last() + ".vault.azure.net")
                throw new JsonException();
            var options = new SecretClientOptions { Retry = { MaxRetries = 0 }, Transport = new HttpClientTransport(http) };
            var secret = (await new SecretClient(new Uri(secretUri.GetLeftPart(UriPartial.Authority)), credential, options)
                .GetSecretAsync(parts[1], parts[2], token)).Value;
            if (secret.Properties.Enabled == false) throw new AuthorityDeniedException("Approved factory key version is disabled; vault administrator must reconcile without replacing or rotating it.");
            if (secret.Id != secretUri || secret.Properties.Enabled != true) throw new JsonException();
            var now = clock.GetUtcNow();
            var jwt = GitHubAppAuthProvider.BuildAppJwt(authority.AppId.ToString(CultureInfo.InvariantCulture), secret.Value, now);
            admin = "GitHub App / installation administrator";
            operation = "GET /app";
            var app = await RequestAsync(operation, jwt, null, token);
            if (app.GetProperty("id").GetInt64() != authority.AppId
                || !SamePermissions(Permissions(app), authority.ExpectedAppPermissions))
                throw new AuthorityDeniedException("Authenticated App identity or requested permissions differ from the approved App.");
            operation = $"GET /app/installations/{authority.InstallationId}";
            var installation = await RequestAsync(operation, jwt, null, token);
            ValidateInstallation(installation, authority);
            var selection = installation.GetProperty("repository_selection").GetString();
            operation = $"POST /app/installations/{authority.InstallationId}/access_tokens";
            var minted = await RequestAsync(operation, jwt,
                new { repository_ids = new[] { authority.RepositoryId }, permissions = ReadPermissions }, token);
            var access = minted.GetProperty("token").GetString();
            var expiry = minted.GetProperty("expires_at").GetDateTimeOffset();
            if (string.IsNullOrWhiteSpace(access) || expiry <= now.AddMinutes(2) || expiry > now.AddHours(1).AddSeconds(30)
                || minted.GetProperty("repository_selection").GetString() != "selected"
                || !SamePermissions(Permissions(minted), ReadPermissions))
                throw new JsonException();
            var repositories = minted.GetProperty("repositories");
            if (repositories.GetArrayLength() != 1 || repositories[0].GetProperty("id").GetInt64() != authority.RepositoryId)
                throw new AuthorityDeniedException("Token response did not restrict reach to the approved durable repository.");
            operation = "GET /installation/repositories?per_page=100";
            var effective = await RequestAsync(operation, access, null, token);
            if (effective.GetProperty("total_count").GetInt32() != 1
                || effective.GetProperty("repositories").GetArrayLength() != 1
                || effective.GetProperty("repositories")[0].GetProperty("id").GetInt64() != authority.RepositoryId)
                throw new AuthorityDeniedException("Effective token repository reach differs from the approved durable repository.");
            operation = $"GET /repositories/{authority.RepositoryId}";
            var repository = await RequestAsync(operation, access, null, token);
            var owner = repository.GetProperty("owner");
            var name = repository.GetProperty("full_name").GetString();
            var branch = repository.GetProperty("default_branch").GetString();
            if (repository.GetProperty("id").GetInt64() != authority.RepositoryId
                || owner.GetProperty("id").GetInt64() != authority.AccountId
                || string.IsNullOrWhiteSpace(name) || name.Split('/') is not [{ Length: > 0 }, { Length: > 0 }]
                || string.IsNullOrWhiteSpace(branch))
                throw new AuthorityDeniedException("Repository identity or account differs from the intended installation.");
            if (repository.GetProperty("archived").GetBoolean() || repository.GetProperty("disabled").GetBoolean()
                || !repository.GetProperty("has_issues").GetBoolean())
                throw new AuthorityDeniedException("Repository is archived, disabled or has issues disabled; repository administrator must resolve outside DSF.");
            operation = $"GET /repos/{name}/git/ref/heads/{Uri.EscapeDataString(branch)}";
            var reference = await RequestAsync(operation, access, null, token);
            var sha = reference.GetProperty("object").GetProperty("sha").GetString();
            if (reference.GetProperty("ref").GetString() != "refs/heads/" + branch
                || reference.GetProperty("object").GetProperty("type").GetString() != "commit"
                || sha is not { Length: 40 or 64 } || sha.Any(c => !char.IsAsciiHexDigit(c)))
                throw new JsonException();
            operation = $"GET /repos/{name}/installation";
            var membership = await RequestAsync(operation, jwt, null, token);
            ValidateInstallation(membership, authority);
            verifiedReadToken = access;
            return new(GitHubAuthorityState.Verified, "deployed-github-authentication", "none",
                "Observed versioned factory key access and actual GitHub identity, installation membership and restricted read token. "
                + "Issue-write grant is coarser than DSF policy; no publisher is eligible while inactive. "
                + "Key holder can mint broader App-authorized tokens; downscoping is discipline, not signing-key containment.")
            {
                FactoryId = authority.FactoryId, OperationId = authority.OperationId, ObservedAt = now,
                AppId = authority.AppId, InstallationId = authority.InstallationId, AccountId = authority.AccountId,
                RepositoryId = authority.RepositoryId, Repository = name, DefaultBranch = branch,
                InstallationRepositorySelection = selection, GrantedPermissions = Permissions(installation),
                AppPermissions = Permissions(app),
                EffectivePermissions = ReadPermissions, TokenExpiresAt = expiry,
                KeyVersionUri = secretUri.AbsoluteUri, AuthPrincipalId = authority.AuthPrincipalId,
            };
        }

        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Failure(GitHubAuthorityState.Canceled, "Verification canceled; no repository mutation dispatched."); }
        catch (AuthorityDeniedException e)
        { return Failure(GitHubAuthorityState.Blocked, e.Message); }
        catch (RequestFailedException e) when (e.Status is 401 or 403 or 404)
        { return Failure(GitHubAuthorityState.Blocked, $"Provider denied the exact operation (HTTP {e.Status}); no access repair attempted."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException
            or CryptographicException or ArgumentException or HttpRequestException or RequestFailedException or OperationCanceledException
            or Azure.Identity.AuthenticationFailedException)
        { return Failure(GitHubAuthorityState.Unknown, "Provider evidence unavailable, malformed or unverifiable; details withheld."); }

        GitHubAuthorityProof Failure(GitHubAuthorityState state, string detail) => new(state, operation, admin, detail)
        { FactoryId = authority.FactoryId, OperationId = authority.OperationId, ObservedAt = clock.GetUtcNow() };
    }

    public async Task<(GitHubAuthorityProof Proof, string? Token)> AcquireReadTokenAsync(
        FactoryGitHubAuthority authority, CancellationToken token)
    {
        var proof = await VerifyAsync(authority, token);
        return (proof, proof.State == GitHubAuthorityState.Verified ? verifiedReadToken : null);
    }

    private async Task<JsonElement> RequestAsync(string operation, string authorization, object? body, CancellationToken token)
    {
        var parts = operation.Split(' ', 2);
        using var request = new HttpRequestMessage(new HttpMethod(parts[0]), "https://api.github.com" + parts[1]);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("dsf-factory-github-auth");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.UnprocessableEntity)
            throw new AuthorityDeniedException($"GitHub denied the exact operation (HTTP {(int)response.StatusCode}); installation administrator must resolve outside DSF.");
        if (response.StatusCode != (body is null ? HttpStatusCode.OK : HttpStatusCode.Created))
            throw new JsonException();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(token), FactoryGitHubJson.Options);
    }

    private static void ValidateInstallation(JsonElement installation, FactoryGitHubAuthority authority)
    {
        if (installation.GetProperty("id").GetInt64() != authority.InstallationId
            || installation.GetProperty("app_id").GetInt64() != authority.AppId
            || installation.GetProperty("account").GetProperty("id").GetInt64() != authority.AccountId
            || installation.GetProperty("account").GetProperty("type").GetString() is not ("User" or "Organization")
            || installation.GetProperty("repository_selection").GetString() is not ("all" or "selected")
            || installation.GetProperty("repository_selection").GetString() != authority.ExpectedInstallationRepositorySelection
            || installation.GetProperty("suspended_at").ValueKind != JsonValueKind.Null
            || !SamePermissions(Permissions(installation), authority.ExpectedPermissions))
            throw new AuthorityDeniedException("Installation identity, suspension, account or granted permissions differ from the approved authority.");
    }

    private static Dictionary<string, string> Permissions(JsonElement value) =>
        value.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? throw new JsonException());
    private static bool SamePermissions(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected) =>
        actual.Count == expected.Count && actual.All(p => expected.TryGetValue(p.Key, out var value) && value == p.Value);
    private sealed class AuthorityDeniedException(string message) : Exception(message);
}
