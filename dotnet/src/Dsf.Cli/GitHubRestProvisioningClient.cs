using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dsf.Cli;

internal sealed class GitHubRestProvisioningClient : IGitHubProvisioningClient
{
    private const string DefaultApiUrl = "https://api.github.com/";
    private readonly HttpClient httpClient;
    private readonly string? token;

    internal GitHubRestProvisioningClient(HttpClient httpClient, string? token)
    {
        this.httpClient = httpClient;
        this.token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        this.httpClient.BaseAddress ??= new Uri(DefaultApiUrl);
        this.httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dsf-cli");
        this.httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    internal static GitHubRestProvisioningClient FromEnvironment()
    {
        var apiUrl = Environment.GetEnvironmentVariable("DSF_GITHUB_API_URL");
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(
                string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : EnsureTrailingSlash(apiUrl)),
        };
        var token = Environment.GetEnvironmentVariable("GH_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        return new GitHubRestProvisioningClient(httpClient, token);
    }

    public async Task<GitHubRepositoryProvisioningResult> EnsureRepositoryAsync(
        EnsureRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        using var existing = await SendAsync(
            HttpMethod.Get,
            $"repos/{request.Owner}/{request.Repository}",
            body: null,
            cancellationToken,
            allowNotFound: true);
        if (existing.StatusCode != HttpStatusCode.NotFound)
        {
            return await ReadRepositoryAsync(existing, request.DefaultBranch, cancellationToken);
        }

        using var userResponse = await SendAsync(
            HttpMethod.Get,
            "user",
            body: null,
            cancellationToken);
        using var user = await ReadJsonAsync(userResponse, cancellationToken);
        var login = user.RootElement.GetProperty("login").GetString();
        var path = string.Equals(login, request.Owner, StringComparison.OrdinalIgnoreCase)
            ? "user/repos"
            : $"orgs/{request.Owner}/repos";
        using var created = await SendAsync(
            HttpMethod.Post,
            path,
            new Dictionary<string, object?>
            {
                ["name"] = request.Repository,
                ["visibility"] = request.Visibility,
            },
            cancellationToken);
        return await ReadRepositoryAsync(created, request.DefaultBranch, cancellationToken);
    }

    public async Task EnsureLabelsAsync(
        EnsureLabelsRequest request,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? page = $"repos/{request.RepositoryFullName}/labels?per_page=100";
        while (page is not null)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                page,
                body: null,
                cancellationToken);
            page = NextPage(response);
            using var labels = await ReadJsonAsync(response, cancellationToken);
            foreach (var label in labels.RootElement.EnumerateArray())
            {
                var name = label.GetProperty("name").GetString();
                if (name is not null)
                {
                    existing.Add(name);
                }
            }
        }

        foreach (var label in request.Labels.Where(label => !existing.Contains(label.Name)))
        {
            using var ignored = await SendAsync(
                HttpMethod.Post,
                $"repos/{request.RepositoryFullName}/labels",
                new Dictionary<string, object?>
                {
                    ["name"] = label.Name,
                    ["color"] = label.Color,
                    ["description"] = label.Description,
                },
                cancellationToken);
        }
    }

    public async Task<GitHubAppBindingProvisioningResult?> EnsureAppBindingAsync(
        EnsureAppBindingRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InstallationId))
        {
            return null;
        }

        using var repositoryResponse = await SendAsync(
            HttpMethod.Get,
            $"repos/{request.RepositoryFullName}",
            body: null,
            cancellationToken);
        using var repository = await ReadJsonAsync(repositoryResponse, cancellationToken);
        var repositoryId = repository.RootElement.GetProperty("id").GetInt64();
        if (!await InstallationCoversRepositoryAsync(
                request.InstallationId, repositoryId, cancellationToken))
        {
            using var ignored = await SendAsync(
                HttpMethod.Put,
                $"user/installations/{request.InstallationId}/repositories/{repositoryId}",
                body: null,
                cancellationToken);
        }

        return new GitHubAppBindingProvisioningResult(request.AppId, request.InstallationId);
    }

    /// <summary>
    /// Whether the installation already covers <paramref name="repositoryId"/>: either
    /// it is "all-repositories" (covers every repo of the owner), or a "selected"
    /// installation whose repository list already contains it. Checking first keeps
    /// re-runs idempotent: a PUT against a repo an "all" installation already covers,
    /// or a "selected" installation already lists, would otherwise risk a 403/422.
    /// </summary>
    private async Task<bool> InstallationCoversRepositoryAsync(
        string installationId,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        string? page = $"user/installations/{installationId}/repositories?per_page=100";
        while (page is not null)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                page,
                body: null,
                cancellationToken);
            page = NextPage(response);
            using var body = await ReadJsonAsync(response, cancellationToken);
            var root = body.RootElement;
            if (root.TryGetProperty("repository_selection", out var selection)
                && string.Equals(selection.GetString(), "all", StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var candidate in root.GetProperty("repositories").EnumerateArray())
            {
                if (candidate.GetProperty("id").GetInt64() == repositoryId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public async Task<GitHubRulesetProvisioningResult> EnsureBranchProtectionRulesetAsync(
        EnsureBranchProtectionRulesetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var rulesetId = request.ExistingRulesetId
                ?? await FindRulesetIdAsync(request, cancellationToken);
            var method = rulesetId is null ? HttpMethod.Post : HttpMethod.Put;
            var path = rulesetId is null
                ? $"repos/{request.RepositoryFullName}/rulesets"
                : $"repos/{request.RepositoryFullName}/rulesets/{rulesetId}";
            using var rulesetResponse = await SendAsync(
                method,
                path,
                RulesetPayload(request),
                cancellationToken);
            using var ruleset = await ReadJsonAsync(rulesetResponse, cancellationToken);
            rulesetId ??= ruleset.RootElement.GetProperty("id").GetInt64();

            using var ignored = await SendAsync(
                HttpMethod.Patch,
                $"repos/{request.RepositoryFullName}",
                new Dictionary<string, object?> { ["allow_auto_merge"] = request.AllowAutoMerge },
                cancellationToken);
            return new GitHubRulesetProvisioningResult(rulesetId);
        }
        catch (GitHubApiException exception)
            when (exception.StatusCode == HttpStatusCode.Forbidden
                && exception.ResponseBody.Contains(
                    "upgrade to github pro",
                    StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubRulesetProvisioningResult(null);
        }
    }

    private async Task<long?> FindRulesetIdAsync(
        EnsureBranchProtectionRulesetRequest request,
        CancellationToken cancellationToken)
    {
        string? page = $"repos/{request.RepositoryFullName}/rulesets?includes_parents=false";
        while (page is not null)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                page,
                body: null,
                cancellationToken);
            page = NextPage(response);
            using var rulesets = await ReadJsonAsync(response, cancellationToken);
            foreach (var ruleset in rulesets.RootElement.EnumerateArray())
            {
                if (string.Equals(
                        ruleset.GetProperty("name").GetString(),
                        request.Name,
                        StringComparison.Ordinal))
                {
                    return ruleset.GetProperty("id").GetInt64();
                }
            }
        }

        return null;
    }

    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var link in values.SelectMany(value => value.Split(',')))
        {
            if (!link.Contains("rel=\"next\"", StringComparison.Ordinal))
            {
                continue;
            }

            var start = link.IndexOf('<');
            var end = link.IndexOf('>', start + 1);
            if (start >= 0 && end > start)
            {
                return link[(start + 1)..end];
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        if (token is null)
        {
            throw new InvalidOperationException(
                "GitHub provisioning requires GH_TOKEN or GITHUB_TOKEN.");
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"GitHub API {method} {path} could not be reached: {exception.Message}",
                exception);
        }
        if (response.IsSuccessStatusCode
            || allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return response;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var statusCode = response.StatusCode;
        response.Dispose();
        throw new GitHubApiException(method, path, statusCode, detail);
    }

    private static async Task<GitHubRepositoryProvisioningResult> ReadRepositoryAsync(
        HttpResponseMessage response,
        string fallbackDefaultBranch,
        CancellationToken cancellationToken)
    {
        using var repository = await ReadJsonAsync(response, cancellationToken);
        var root = repository.RootElement;
        var defaultBranch = root.TryGetProperty("default_branch", out var branch)
            ? branch.GetString()
            : null;
        return new GitHubRepositoryProvisioningResult(
            root.GetProperty("id").GetInt64(),
            string.IsNullOrWhiteSpace(defaultBranch) ? fallbackDefaultBranch : defaultBranch);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static Dictionary<string, object?> RulesetPayload(
        EnsureBranchProtectionRulesetRequest request) =>
        new()
        {
            ["name"] = request.Name,
            ["target"] = "branch",
            ["enforcement"] = "active",
            ["conditions"] = new Dictionary<string, object?>
            {
                ["ref_name"] = new Dictionary<string, object?>
                {
                    ["include"] = new[] { "~DEFAULT_BRANCH" },
                    ["exclude"] = Array.Empty<string>(),
                },
            },
            ["rules"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "pull_request",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["required_approving_review_count"] =
                            request.RequiredApprovingReviewCount,
                        ["dismiss_stale_reviews_on_push"] = true,
                        ["require_code_owner_review"] = false,
                        ["require_last_push_approval"] = false,
                        ["required_review_thread_resolution"] = false,
                    },
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "required_status_checks",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["strict_required_status_checks_policy"] = true,
                        ["do_not_enforce_on_create"] = true,
                        ["required_status_checks"] = request.RequiredStatusChecks
                            .Select(context => new Dictionary<string, object?>
                            {
                                ["context"] = context,
                            })
                            .ToArray(),
                    },
                },
            },
        };

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    private sealed class GitHubApiException(
        HttpMethod method,
        string path,
        HttpStatusCode statusCode,
        string responseBody)
        : InvalidOperationException(
            $"GitHub API {method} {path} failed with {(int)statusCode}"
            + (string.IsNullOrWhiteSpace(responseBody) ? "." : $": {responseBody}"))
    {
        public HttpStatusCode StatusCode { get; } = statusCode;

        public string ResponseBody { get; } = responseBody;
    }
}
