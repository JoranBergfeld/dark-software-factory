using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

/// <summary>Selects the repository (and optional owner control-plane lookup) to review.</summary>
internal sealed record RepositoryAttachmentReviewRequest(
    string Owner,
    string Repository,
    string? OwnerKeyVaultUri,
    string? OwnerAppConfigEndpoint,
    string? AutomationOwnerDeclaration);

/// <summary>
/// Produces a real, read-only <see cref="RepositoryAttachmentReview"/>. No implementation may
/// create/alter labels, repair repository settings, or persist an operator credential.
/// </summary>
internal interface IRepositoryAttachmentReviewer
{
    Task<RepositoryAttachmentReview> ReviewAsync(
        RepositoryAttachmentReviewRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reviews an existing GitHub repository for Decide attachment using the operator's effective
/// GitHub CLI authentication (<c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>, e.g. from
/// <c>export GH_TOKEN="$(gh auth token)"</c>) for discovery, and the existing owner control plane
/// (<see cref="IOwnerCredentialReader"/>) for the DSF App's registered identity. It never persists
/// the operator's token and never mutates the repository.
/// </summary>
internal sealed class GitHubRepositoryAttachmentReviewer : IRepositoryAttachmentReviewer
{
    private const string DefaultApiUrl = "https://api.github.com/";

    private static readonly Regex AutomationTriggerPattern = new(
        @"(?im)^\s*(""?issues""?|""?issue_comment""?|""?pull_request""?|""?pull_request_target""?)\s*:",
        RegexOptions.Compiled);

    private static readonly Regex OnKeyPattern = new(@"(?im)^on\s*:.*$", RegexOptions.Compiled);

    private static readonly Regex Base64Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> RequiredInstallationPermissions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issues"] = "write",
            ["contents"] = "read",
            ["metadata"] = "read",
        };

    private readonly HttpClient httpClient;
    private readonly string? token;
    private readonly IOwnerCredentialReader? ownerCredentialReader;

    internal GitHubRepositoryAttachmentReviewer(
        HttpClient httpClient,
        string? token,
        IOwnerCredentialReader? ownerCredentialReader = null)
    {
        this.httpClient = httpClient;
        this.token = token;
        this.ownerCredentialReader = ownerCredentialReader;
        this.httpClient.BaseAddress ??= new Uri(DefaultApiUrl);
    }

    internal static GitHubRepositoryAttachmentReviewer FromEnvironment(
        IOwnerCredentialReader? ownerCredentialReader = null)
    {
        var apiUrl = Environment.GetEnvironmentVariable("DSF_GITHUB_API_URL");
        var client = new HttpClient
        {
            BaseAddress = new Uri(string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : EnsureTrailingSlash(apiUrl)),
        };
        var token = Environment.GetEnvironmentVariable("GH_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        return new GitHubRepositoryAttachmentReviewer(client, token, ownerCredentialReader);
    }

    public async Task<RepositoryAttachmentReview> ReviewAsync(
        RepositoryAttachmentReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Repository attachment review requires effective GitHub CLI authentication: set "
                + "GH_TOKEN or GITHUB_TOKEN (for example: export GH_TOKEN=\"$(gh auth token)\").");
        }

        var prerequisites = new List<RepositoryAttachmentPrerequisite>();
        var automation = new List<RepositoryAutomationFinding>();
        var blockingReasons = new List<string>();

        var (repository, repositoryPrerequisite, ssoPrerequisite) = await ReadRepositoryAsync(
            request.Owner, request.Repository, cancellationToken);
        prerequisites.Add(repositoryPrerequisite);
        if (ssoPrerequisite is not null)
        {
            prerequisites.Add(ssoPrerequisite);
        }

        IReadOnlyList<RepositoryLabelReview> labels = [];
        if (repository is not null)
        {
            if (repository.Archived)
            {
                blockingReasons.Add(
                    $"Repository '{repository.Owner}/{repository.Name}' is archived; onboarding does not "
                    + "unarchive it. The repository owner must unarchive it before attachment.");
            }

            if (!repository.IssuesEnabled)
            {
                blockingReasons.Add(
                    $"Repository '{repository.Owner}/{repository.Name}' has issues disabled; onboarding does "
                    + "not enable them. The repository owner must enable issues before attachment.");
            }

            labels = await ReviewLabelsAsync(repository.Owner, repository.Name, cancellationToken);
            automation.AddRange(await ReviewAutomationAsync(repository.Owner, repository.Name, cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(request.AutomationOwnerDeclaration))
        {
            automation.Add(
                new RepositoryAutomationFinding(
                    MutationClass: "owner-declaration",
                    Blocks: false,
                    Evidence: AutomationEvidence.OwnerDeclared,
                    Detail: $"Owner declared: {request.AutomationOwnerDeclaration.Trim()} "
                        + "(a declaration, not technical proof of every bot/webhook)."));
        }

        foreach (var finding in automation.Where(finding => finding.Blocks))
        {
            blockingReasons.Add(
                $"Known automation on mutation class '{finding.MutationClass}' blocks it: {finding.Detail} "
                + "The repository owner/administrator must confirm or disable this automation externally; "
                + "onboarding does not override it or repair the repository.");
        }

        prerequisites.AddRange(
            await ReviewInstallationAsync(request, repository, cancellationToken));

        foreach (var prerequisite in prerequisites.Where(prerequisite => prerequisite.State == PrerequisiteState.Denied))
        {
            blockingReasons.Add(
                $"Prerequisite '{prerequisite.Name}' is denied: {prerequisite.Detail} "
                + $"Responsible actor: {prerequisite.ResponsibleActor ?? "unknown"}.");
        }

        var eligibility = DetermineEligibility(repository, prerequisites, blockingReasons);
        return new RepositoryAttachmentReview(
            repository,
            prerequisites,
            labels,
            automation,
            eligibility,
            blockingReasons);
    }

    private static RepositoryAttachmentEligibility DetermineEligibility(
        RepositoryIdentity? repository,
        IReadOnlyList<RepositoryAttachmentPrerequisite> prerequisites,
        IReadOnlyList<string> blockingReasons)
    {
        if (blockingReasons.Count > 0)
        {
            return RepositoryAttachmentEligibility.Blocked;
        }

        if (repository is null)
        {
            return RepositoryAttachmentEligibility.Unknown;
        }

        return prerequisites.Any(prerequisite => prerequisite.State is PrerequisiteState.Unknown or PrerequisiteState.Pending)
            ? RepositoryAttachmentEligibility.Unknown
            : RepositoryAttachmentEligibility.Eligible;
    }

    private async Task<(RepositoryIdentity? Repository, RepositoryAttachmentPrerequisite RepositoryPrerequisite, RepositoryAttachmentPrerequisite? Sso)>
        ReadRepositoryAsync(string owner, string repositoryName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"repos/{owner}/{repositoryName}", cancellationToken);
        var sso = ReadSsoPrerequisite(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (
                null,
                new RepositoryAttachmentPrerequisite(
                    "repository-access",
                    PrerequisiteState.Denied,
                    $"Repository '{owner}/{repositoryName}' was not found or is not visible to the "
                    + "authenticated GitHub CLI identity.",
                    "repository owner or administrator (grant read access, or confirm the owner/name)"),
                sso);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return (
                null,
                new RepositoryAttachmentPrerequisite(
                    "repository-access",
                    PrerequisiteState.Denied,
                    $"Access to repository '{owner}/{repositoryName}' was refused (policy, SSO, or "
                    + "suspension). No broader human token is requested automatically.",
                    "repository owner or organization administrator"),
                sso);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (
                null,
                new RepositoryAttachmentPrerequisite(
                    "repository-access",
                    PrerequisiteState.Unknown,
                    $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} reading "
                    + $"'{owner}/{repositoryName}'.",
                    "operator (retry) or repository administrator"),
                sso);
        }

        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var identity = new RepositoryIdentity(
            RepositoryId: root.GetProperty("id").GetInt64().ToString(),
            Owner: root.GetProperty("owner").GetProperty("login").GetString() ?? owner,
            Name: root.GetProperty("name").GetString() ?? repositoryName,
            DefaultBranch: root.GetProperty("default_branch").GetString()
                ?? throw new InvalidOperationException(
                    $"Repository '{owner}/{repositoryName}' has no default branch."),
            Archived: root.TryGetProperty("archived", out var archived) && archived.GetBoolean(),
            IssuesEnabled: !root.TryGetProperty("has_issues", out var hasIssues) || hasIssues.GetBoolean(),
            Visibility: root.TryGetProperty("visibility", out var visibility)
                ? visibility.GetString() ?? "unknown"
                : (root.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean() ? "private" : "public"));
        return (
            identity,
            new RepositoryAttachmentPrerequisite(
                "repository-access",
                PrerequisiteState.Confirmed,
                $"Repository '{identity.Owner}/{identity.Name}' (id {identity.RepositoryId}) is readable; "
                + $"default branch '{identity.DefaultBranch}'.",
                null),
            sso);
    }

    private static RepositoryAttachmentPrerequisite? ReadSsoPrerequisite(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-GitHub-SSO", out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault() ?? string.Empty;
        if (!value.Contains("required", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new RepositoryAttachmentPrerequisite(
            "organization-sso",
            PrerequisiteState.Pending,
            $"The organization requires SSO authorization for this token: {value}",
            "operator (authorize the token for organization SSO)");
    }

    private async Task<IReadOnlyList<RepositoryLabelReview>> ReviewLabelsAsync(
        string owner,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        var reviews = new List<RepositoryLabelReview>();
        foreach (var name in RepositoryAttachmentReview.PermittedLabelNames)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                $"repos/{owner}/{repositoryName}/labels/{Uri.EscapeDataString(name)}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                reviews.Add(new RepositoryLabelReview(name, Exists: false, null, null, ConflictsWithExpectedMeaning: false));
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                reviews.Add(new RepositoryLabelReview(name, Exists: false, null, null, ConflictsWithExpectedMeaning: false));
                continue;
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            var color = document.RootElement.TryGetProperty("color", out var colorElement)
                ? colorElement.GetString()
                : null;
            var description = document.RootElement.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;
            // An empty/missing description is unknown provenance, not proven conflicting; only a
            // description that positively omits "dsf" is treated as a semantic conflict.
            var conflicts = !string.IsNullOrWhiteSpace(description)
                && !description.Contains("dsf", StringComparison.OrdinalIgnoreCase);
            reviews.Add(new RepositoryLabelReview(name, Exists: true, color, description, conflicts));
        }

        return reviews;
    }

    private async Task<IReadOnlyList<RepositoryAutomationFinding>> ReviewAutomationAsync(
        string owner,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        var findings = new List<RepositoryAutomationFinding>();

        using (var listing = await SendAsync(
            HttpMethod.Get, $"repos/{owner}/{repositoryName}/contents/.github/workflows", cancellationToken))
        {
            if (listing.IsSuccessStatusCode)
            {
                using var document = await ReadJsonAsync(listing, cancellationToken);
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.GetProperty("type").GetString() != "file")
                    {
                        continue;
                    }

                    var path = entry.GetProperty("path").GetString();
                    if (path is null || !(path.EndsWith(".yml") || path.EndsWith(".yaml")))
                    {
                        continue;
                    }

                    using var contentResponse = await SendAsync(
                        HttpMethod.Get, $"repos/{owner}/{repositoryName}/contents/{path}", cancellationToken);
                    if (!contentResponse.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    using var contentDocument = await ReadJsonAsync(contentResponse, cancellationToken);
                    var encoded = contentDocument.RootElement.GetProperty("content").GetString() ?? string.Empty;
                    var text = Encoding.UTF8.GetString(
                        Convert.FromBase64String(Base64Whitespace.Replace(encoded, string.Empty)));
                    if (AutomationTriggerPattern.IsMatch(ExtractOnSection(text)))
                    {
                        findings.Add(
                            new RepositoryAutomationFinding(
                                MutationClass: "issue-or-pull-request-triggered-workflow",
                                Blocks: true,
                                Evidence: AutomationEvidence.VisibleWorkflow,
                                Detail: $"Workflow '{path}' triggers on issue/pull-request/label activity."));
                    }
                }
            }
            else if (listing.StatusCode != HttpStatusCode.NotFound)
            {
                findings.Add(
                    new RepositoryAutomationFinding(
                        MutationClass: "issue-or-pull-request-triggered-workflow",
                        Blocks: false,
                        Evidence: AutomationEvidence.Unknown,
                        Detail: $"Could not list .github/workflows ({(int)listing.StatusCode} {listing.ReasonPhrase})."));
            }
        }

        using var hooks = await SendAsync(HttpMethod.Get, $"repos/{owner}/{repositoryName}/hooks", cancellationToken);
        if (hooks.IsSuccessStatusCode)
        {
            using var document = await ReadJsonAsync(hooks, cancellationToken);
            var activeHooks = document.RootElement.EnumerateArray()
                .Count(hook => !hook.TryGetProperty("active", out var active) || active.GetBoolean());
            if (activeHooks > 0)
            {
                findings.Add(
                    new RepositoryAutomationFinding(
                        MutationClass: "webhook-triggered-delivery",
                        Blocks: true,
                        Evidence: AutomationEvidence.VisibleWebhook,
                        Detail: $"{activeHooks} active repository webhook(s) are configured."));
            }
        }
        else if (hooks.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            findings.Add(
                new RepositoryAutomationFinding(
                    MutationClass: "webhook-triggered-delivery",
                    Blocks: false,
                    Evidence: AutomationEvidence.Unknown,
                    Detail: "The authenticated identity lacks administrator access to list repository webhooks."));
        }

        return findings;
    }

    /// <summary>
    /// Returns only the text of the workflow's top-level <c>on:</c> trigger block (its own line
    /// plus any more-indented continuation lines), so trigger-key detection cannot false-positive
    /// on an unrelated key such as a job/step/env value that happens to be named "issues".
    /// </summary>
    private static string ExtractOnSection(string workflowYaml)
    {
        var match = OnKeyPattern.Match(workflowYaml);
        if (!match.Success)
        {
            return string.Empty;
        }

        var lines = workflowYaml[match.Index..].Split('\n');
        var section = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                section.Append('\n').Append(line);
                continue;
            }

            break;
        }

        return section.ToString();
    }

    private async Task<IReadOnlyList<RepositoryAttachmentPrerequisite>> ReviewInstallationAsync(
        RepositoryAttachmentReviewRequest request,
        RepositoryIdentity? repository,
        CancellationToken cancellationToken)
    {
        if (ownerCredentialReader is null || string.IsNullOrWhiteSpace(request.OwnerKeyVaultUri))
        {
            return
            [
                new RepositoryAttachmentPrerequisite(
                    "app-registration",
                    PrerequisiteState.Unknown,
                    "No owner control plane was supplied (--owner-keyvault-uri); the DSF App's registered "
                    + "identity could not be checked.",
                    "operator (supply --owner-keyvault-uri/--owner-appconfig-endpoint)"),
            ];
        }

        var resolver = new OwnerCredentialResolver(ownerCredentialReader);
        OwnerGitHubIdentity identity;
        try
        {
            identity = await resolver.ResolveIdentityAsync(
                request.OwnerKeyVaultUri, request.OwnerAppConfigEndpoint, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            return
            [
                new RepositoryAttachmentPrerequisite(
                    "app-registration",
                    PrerequisiteState.Unknown,
                    $"Could not read the owner control plane: {exception.Message}",
                    "operator (complete `dsf bootstrap`) or owner administrator"),
            ];
        }

        var results = new List<RepositoryAttachmentPrerequisite>
        {
            new(
                "app-registration",
                PrerequisiteState.Confirmed,
                $"DSF App id '{identity.AppId}' and installation id '{identity.InstallationId}' are "
                + "registered in the owner control plane.",
                null),
        };

        if (repository is null)
        {
            return results;
        }

        JsonElement? installation = null;
        var page = 1;
        while (installation is null)
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"user/installations?per_page=100&page={page}", cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                results.AddRange(
                [
                    new RepositoryAttachmentPrerequisite(
                        "installation-account",
                        PrerequisiteState.Unknown,
                        "The operator's GitHub CLI identity could not list App installations "
                        + $"({(int)response.StatusCode} {response.ReasonPhrase}).",
                        "operator (an account with installation-management access) or organization administrator"),
                    new RepositoryAttachmentPrerequisite(
                        "installation-permissions",
                        PrerequisiteState.Unknown,
                        "Granted permissions could not be verified because installations could not be listed.",
                        "operator or organization administrator"),
                ]);
                return results;
            }

            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            var matches = document.RootElement.GetProperty("installations").EnumerateArray()
                .Where(entry => entry.GetProperty("id").GetInt64().ToString() == identity.InstallationId)
                .ToArray();
            if (matches.Length > 0)
            {
                installation = matches[0].Clone();
                break;
            }

            var hasMore = document.RootElement.GetProperty("installations").GetArrayLength() == 100;
            if (!hasMore)
            {
                break;
            }

            page++;
        }

        if (installation is null)
        {
            results.Add(
                new RepositoryAttachmentPrerequisite(
                    "installation-account",
                    PrerequisiteState.Unknown,
                    $"Installation id '{identity.InstallationId}' was not visible to the operator's GitHub CLI "
                    + "identity; it may not have installation-management access, or the installation was removed.",
                    "operator (installation-management access) or organization administrator"));
            return results;
        }

        var installationValue = installation.Value;
        var suspendedAt = installationValue.TryGetProperty("suspended_at", out var suspendedElement)
            && suspendedElement.ValueKind != JsonValueKind.Null
                ? suspendedElement.GetString()
                : null;
        var account = installationValue.TryGetProperty("account", out var accountElement)
            && accountElement.TryGetProperty("login", out var loginElement)
                ? loginElement.GetString()
                : "unknown";
        var selection = installationValue.GetProperty("repository_selection").GetString() ?? "unknown";

        results.Add(
            suspendedAt is not null
                ? new RepositoryAttachmentPrerequisite(
                    "installation-suspension",
                    PrerequisiteState.Denied,
                    $"The DSF App installation on account '{account}' is suspended (since {suspendedAt}).",
                    "organization/account administrator (unsuspend the installation)")
                : new RepositoryAttachmentPrerequisite(
                    "installation-suspension",
                    PrerequisiteState.Confirmed,
                    $"The DSF App installation on account '{account}' is active (not suspended).",
                    null));

        if (string.Equals(selection, "all", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(
                new RepositoryAttachmentPrerequisite(
                    "selected-repository-membership",
                    PrerequisiteState.Confirmed,
                    $"Installation on account '{account}' is configured for all repositories.",
                    null));
        }
        else
        {
            var selected = await IsRepositorySelectedAsync(
                identity.InstallationId, repository.RepositoryId, cancellationToken);
            results.Add(
                selected
                    ? new RepositoryAttachmentPrerequisite(
                        "selected-repository-membership",
                        PrerequisiteState.Confirmed,
                        $"Repository '{repository.Owner}/{repository.Name}' is selected for this installation.",
                        null)
                    : new RepositoryAttachmentPrerequisite(
                        "selected-repository-membership",
                        PrerequisiteState.Pending,
                        $"Repository '{repository.Owner}/{repository.Name}' is not (yet) selected for this "
                        + "installation.",
                        "repository or organization administrator (add the repository in GitHub App settings)"));
        }

        var permissions = installationValue.TryGetProperty("permissions", out var permissionsElement)
            ? permissionsElement
            : default;
        var missingPermissions = new List<string>();
        foreach (var (permission, requiredLevel) in RequiredInstallationPermissions)
        {
            var granted = permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty(permission, out var grantedElement)
                ? grantedElement.GetString()
                : null;
            if (PermissionRank(granted) < PermissionRank(requiredLevel))
            {
                missingPermissions.Add(permission);
            }
        }

        results.Add(
            missingPermissions.Count == 0
                ? new RepositoryAttachmentPrerequisite(
                    "installation-permissions",
                    PrerequisiteState.Confirmed,
                    "The installation grants the required issues:write, contents:read, and metadata:read rights.",
                    null)
                : new RepositoryAttachmentPrerequisite(
                    "installation-permissions",
                    PrerequisiteState.Pending,
                    $"The installation is missing required permission(s): {string.Join(", ", missingPermissions)}.",
                    "organization/account administrator (approve the required permissions in GitHub App settings)"));

        return results;
    }

    /// <summary>
    /// Ranks a GitHub App installation permission level so a required level (e.g. "write") can be
    /// compared against a granted level (e.g. "admin") without special-casing individual values.
    /// </summary>
    private static int PermissionRank(string? level) => level switch
    {
        "admin" => 3,
        "write" => 2,
        "read" => 1,
        _ => 0,
    };

    private async Task<bool> IsRepositorySelectedAsync(
        string installationId, string repositoryId, CancellationToken cancellationToken)
    {
        var page = 1;
        while (true)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                $"user/installations/{installationId}/repositories?per_page=100&page={page}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            var repositories = document.RootElement.GetProperty("repositories");
            foreach (var entry in repositories.EnumerateArray())
            {
                if (entry.GetProperty("id").GetInt64().ToString() == repositoryId)
                {
                    return true;
                }
            }

            if (repositories.GetArrayLength() < 100)
            {
                return false;
            }

            page++;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string target,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("dsf-cli");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";
}
