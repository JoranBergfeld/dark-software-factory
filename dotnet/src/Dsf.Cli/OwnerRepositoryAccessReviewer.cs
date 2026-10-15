using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed record OwnerRepositoryAccessReview(
    ReviewedGitHubApp? App,
    ReviewedGitHubInstallation? Installation,
    IReadOnlyList<RepositoryReviewFinding> Findings);

internal sealed class OwnerRepositoryAccessReviewer(IOwnerCredentialReader owner, HttpClient http)
{
    public async Task<OwnerRepositoryAccessReview> ReviewAsync(
        RepositoryAttachmentReviewRequest request,
        ReviewedGitHubRepository repository,
        CancellationToken cancellationToken)
    {
        var findings = new List<RepositoryReviewFinding>();
        ReviewedGitHubApp? app = null;
        ReviewedGitHubInstallation? installation = null;
        var stage = "owner-prerequisites";
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerAppConfigEndpoint)
                || string.IsNullOrWhiteSpace(request.OwnerKeyVaultUri))
            {
                findings.Add(new(stage, RepositoryReviewState.Unknown,
                    "DSF_OWNER_APPCONFIG_ENDPOINT and DSF_OWNER_KEYVAULT_URI are required; no owner resources were created.",
                    "owner administrator", "Supply the prepared owner endpoints; do not substitute factory credentials."));
                return new(app, installation, findings);
            }

            var identity = await owner.ReadIdentityFromStatusAsync(request.OwnerAppConfigEndpoint, cancellationToken);
            if (identity is null)
            {
                findings.Add(new(stage, RepositoryReviewState.Pending,
                    "No single completed owner bootstrap identity is available.",
                    "owner administrator", "Finish or reconcile owner bootstrap, App registration and installation consent in the existing owner control plane."));
                return new(app, installation, findings);
            }

            if (!SameEndpoint(request.OwnerKeyVaultUri, identity.KeyVaultUri)
                || !SameEndpoint(request.OwnerAppConfigEndpoint, identity.AppConfigEndpoint))
            {
                findings.Add(new(stage, RepositoryReviewState.Blocked,
                    "Supplied owner endpoints do not match the completed bootstrap authority (or its endpoint metadata is missing).",
                    "owner administrator", "Reconcile the existing owner status and supply its exact App Configuration / Key Vault endpoints before reading any signing key."));
                return new(app, installation, findings);
            }

            var credentials = await owner.ReadAsync(request.OwnerKeyVaultUri, includePrivateKey: true, cancellationToken);
            if (identity.AppId != credentials.AppId || identity.InstallationId != credentials.InstallationId)
            {
                findings.Add(new(stage, RepositoryReviewState.Blocked, "Owner status and Key Vault GitHub identifiers disagree.",
                    "owner administrator", "Reconcile the existing owner authority; do not copy or replace credentials during review."));
                return new(app, installation, findings);
            }

            findings.Add(new(stage, RepositoryReviewState.Confirmed,
                "Completed owner status and existing owner Key Vault credentials agree. No factory key copy is required."));
            var jwt = GitHubAppBootstrapClient.CreateAppJwt(credentials);
            stage = "app-registration";
            var registration = await GetAsync("app", jwt, cancellationToken);
            app = new(registration.GetProperty("id").GetInt64(), registration.GetProperty("slug").GetString()!,
                Permissions(registration.GetProperty("permissions")));
            if (app.Id.ToString(CultureInfo.InvariantCulture) != identity.AppId)
            {
                throw new InvalidOperationException("App registration identity mismatch.");
            }

            findings.Add(new(stage, RepositoryReviewState.Confirmed, $"Owner signing key authenticates registered DSF App {app.Id}."));
            stage = "installation";
            var value = await GetAsync($"app/installations/{Uri.EscapeDataString(identity.InstallationId)}", jwt, cancellationToken);
            var account = value.GetProperty("account");
            installation = new(
                value.GetProperty("id").GetInt64(), value.GetProperty("app_id").GetInt64(),
                account.GetProperty("id").GetInt64(), account.GetProperty("login").GetString()!,
                account.GetProperty("type").GetString()!, value.GetProperty("repository_selection").GetString()!,
                value.GetProperty("suspended_at").ValueKind is JsonValueKind.Null
                    ? null : value.GetProperty("suspended_at").GetDateTimeOffset(),
                Permissions(value.GetProperty("permissions")));
            if (installation.Id.ToString(CultureInfo.InvariantCulture) != identity.InstallationId
                || installation.AppId != app.Id || installation.AccountId != repository.OwnerId)
            {
                findings.Add(new(stage, RepositoryReviewState.Blocked, "Installation identity/account does not match the intended owner App and repository owner.",
                    "repository installation administrator", "Review the intended App installation and owner identifiers; do not replace or expand it here."));
                return new(app, installation, findings);
            }

            findings.Add(new(stage, RepositoryReviewState.Confirmed,
                $"Installation {installation.Id} belongs to repository owner account {installation.AccountId} ({installation.AccountLogin})."));
            var installationSettings = installation.AccountType == "Organization"
                ? $"https://github.com/organizations/{installation.AccountLogin}/settings/installations/{installation.Id}"
                : $"https://github.com/settings/installations/{installation.Id}";
            findings.Add(new("installation-selection",
                installation.RepositorySelection == "selected" ? RepositoryReviewState.Confirmed : RepositoryReviewState.Blocked,
                $"Observed repository selection: {installation.RepositorySelection}; Decide requires selected repositories.",
                installation.RepositorySelection == "selected" ? null : "repository installation administrator",
                installation.RepositorySelection == "selected" ? null : $"Review installation scope at {installationSettings}. DSF does not expand or change it."));
            findings.Add(new("installation-suspension",
                installation.SuspendedAt is null ? RepositoryReviewState.Confirmed : RepositoryReviewState.Blocked,
                installation.SuspendedAt is null ? "Installation is not suspended." : $"Installation is suspended since {installation.SuspendedAt:O}.",
                installation.SuspendedAt is null ? null : "GitHub App owner / organization administrator",
                installation.SuspendedAt is null ? null : $"Resolve suspension outside DSF at {installationSettings}; no automatic unsuspension."));
            var required = new Dictionary<string, string>
            {
                ["issues"] = "write",
                ["contents"] = "read",
                ["metadata"] = "read",
            };
            var missing = required.Where(permission => !Granted(installation.GrantedPermissions, permission.Key, permission.Value))
                .Select(permission => $"{permission.Key}:{permission.Value}").ToArray();
            findings.Add(new("installation-permissions",
                missing.Length == 0 ? RepositoryReviewState.Confirmed : RepositoryReviewState.Pending,
                missing.Length == 0
                    ? "Required issue-write, contents-read and metadata-read grants are present. This is not factory-runtime authority verification."
                    : $"Required grants not established: {string.Join(", ", missing)}.",
                missing.Length == 0 ? null : "GitHub App owner / installation administrator",
                missing.Length == 0 ? null : $"Review App registration and consent at {installationSettings}; DSF cannot upgrade permissions or request a broader human token."));
            var unapproved = app.RequestedPermissions
                .Where(permission => !Granted(installation.GrantedPermissions, permission.Key, permission.Value))
                .Select(permission => permission.Key).ToArray();
            findings.Add(new("administrator-approval",
                unapproved.Length == 0 ? RepositoryReviewState.Confirmed : RepositoryReviewState.Pending,
                unapproved.Length == 0 ? "Current registration permissions are granted to this installation."
                    : $"Registration permissions await installation approval: {string.Join(", ", unapproved)}.",
                unapproved.Length == 0 ? null : "installation account administrator",
                unapproved.Length == 0 ? null : $"Review the pending permission request and organization policy at {installationSettings}; review does not grant consent."));
            stage = "repository-membership";
            var membership = await GetAsync($"repos/{repository.Owner}/{repository.Name}/installation", jwt, cancellationToken);
            if (membership.GetProperty("id").GetInt64() != installation.Id)
            {
                throw new InvalidOperationException("Repository installation mismatch.");
            }

            findings.Add(new(stage, RepositoryReviewState.Confirmed, "GitHub confirms this repository belongs to the intended App installation."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            findings.Add(new(stage, RepositoryReviewState.Unknown,
                "Owner/App inspection timed out; no authority is inferred and no remote changes were requested.",
                "owner / installation administrator",
                "Check the authorized owner/GitHub network path and service availability, then repeat read-only review."));
        }
        catch (HttpRequestException exception) when (exception.StatusCode is not null)
        {
            var state = exception.StatusCode switch
            {
                HttpStatusCode.NotFound when stage == "repository-membership" => RepositoryReviewState.Pending,
                HttpStatusCode.NotFound when stage == "installation" => RepositoryReviewState.Denied,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RepositoryReviewState.Denied,
                _ => RepositoryReviewState.Unknown,
            };
            findings.Add(new(stage, state, $"GitHub inspection returned HTTP {(int)exception.StatusCode.Value}. "
                + (stage == "repository-membership" ? "Repository membership/consent is not established."
                    : "Stored installation or App authority may be revoked, inaccessible or restricted; no access is inferred."),
                stage == "app-registration" ? "GitHub App owner" : "installation account / organization administrator",
                "Review the intended App under GitHub Settings > Applications > Installed GitHub Apps, including selected repositories, "
                + "pending approval and organization SSO/policy restrictions; do not broaden tokens or automatically repair the installation."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or KeyNotFoundException
            or FormatException or CryptographicException or ArgumentException or HttpRequestException)
        {
            findings.Add(new(stage, RepositoryReviewState.Unknown, "Inspection could not establish this prerequisite; provider response and credential material are withheld.",
                "owner / repository installation administrator",
                "Verify existing owner data-plane access, signing key, App installation consent and organization SSO/policy eligibility; do not broaden human tokens."));
        }

        return new(app, installation, findings);
    }

    private async Task<JsonElement> GetAsync(string path, string jwt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.UserAgent.ParseAdd("dsf-cli/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, string> Permissions(JsonElement value) =>
        value.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!, StringComparer.Ordinal);

    private static bool Granted(IReadOnlyDictionary<string, string> permissions, string name, string required) =>
        permissions.TryGetValue(name, out var granted) && (granted == required || required == "read" && granted == "write");

    private static bool SameEndpoint(string requested, string? recorded) =>
        Uri.TryCreate(requested, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == "/" && uri.IsDefaultPort
        && Uri.TryCreate(recorded, UriKind.Absolute, out var other) && uri == other;
}
