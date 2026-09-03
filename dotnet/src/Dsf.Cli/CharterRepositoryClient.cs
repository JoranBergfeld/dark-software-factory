using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dsf.Cli;

internal sealed record CharterFile(string Content, string Sha);

internal sealed record CharterPullRequest(string HtmlUrl, string State);

internal sealed record CharterIssue(string HtmlUrl, string NodeId);

internal sealed record CodingAgentPullRequest(int Number, string Url, bool IsDraft, string State);

internal sealed class GitHubApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

internal interface ICharterRepositoryClient
{
    Task<CharterFile?> ReadAsync(
        string repository,
        string path,
        string? reference,
        CancellationToken cancellationToken);

    Task<string> OpenInitialPullRequestAsync(
        string repository,
        string product,
        string content,
        CancellationToken cancellationToken);

    Task<CharterPullRequest?> LatestPullRequestWithHeadPrefixAsync(
        string repository,
        string headPrefix,
        CancellationToken cancellationToken);

    Task<string> OpenFilePullRequestAsync(
        string repository,
        string path,
        string content,
        string branch,
        string title,
        string body,
        string message,
        bool enableAutoMerge,
        string? existingSha,
        CancellationToken cancellationToken);

    Task<CharterIssue> CreateIssueAsync(
        string repository,
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken);

    Task<bool> AssignCopilotWithGhAsync(
        string repository,
        string issueNodeId,
        CancellationToken cancellationToken);

    Task<bool> AssignCopilotWithAppAsync(
        string repository,
        string issueNodeId,
        CancellationToken cancellationToken);

    Task<int?> NewestReadyIssueAsync(
        string repository,
        string label,
        CancellationToken cancellationToken);

    Task<CodingAgentPullRequest?> FindCodingAgentPullRequestAsync(
        string repository,
        int issueNumber,
        CancellationToken cancellationToken);

    Task<bool> HasCopilotReviewRequestAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken);

    Task RequestCopilotReviewAsync(
        string repository,
        string pullRequestUrl,
        CancellationToken cancellationToken);

    Task<bool> AgentWorkFinishedAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken);

    Task MarkPullRequestReadyAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken);
}

/// <summary>Reads charter files from provisioned product repositories through GitHub's contents API.</summary>
internal sealed class GitHubCharterRepositoryClient(HttpClient httpClient, string? token) : ICharterRepositoryClient
{
    private const string DefaultApiUrl = "https://api.github.com/";

    public static GitHubCharterRepositoryClient FromEnvironment()
    {
        var apiUrl = Environment.GetEnvironmentVariable("DSF_GITHUB_API_URL");
        var client = new HttpClient
        {
            BaseAddress = new Uri(string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : EnsureTrailingSlash(apiUrl)),
        };
        return new GitHubCharterRepositoryClient(
            client,
            Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    }

    public async Task<CharterFile?> ReadAsync(
        string repository,
        string path,
        string? reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GitHub charter operations require GH_TOKEN or GITHUB_TOKEN.");
        }

        var target = $"repos/{repository}/contents/{path}";
        if (!string.IsNullOrWhiteSpace(reference))
        {
            target += $"?ref={Uri.EscapeDataString(reference)}";
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            target,
            null,
            cancellationToken,
            allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var content = document.RootElement.GetProperty("content").GetString() ?? string.Empty;
        var sha = document.RootElement.GetProperty("sha").GetString() ?? string.Empty;
        return new CharterFile(Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", string.Empty))), sha);
    }

    public async Task<string> OpenInitialPullRequestAsync(
        string repository,
        string product,
        string content,
        CancellationToken cancellationToken)
    {
        RequireToken();
        using var repositoryResponse = await SendAsync(HttpMethod.Get, $"repos/{repository}", null, cancellationToken);
        using var repositoryDocument = await ReadJsonAsync(repositoryResponse, cancellationToken);
        var defaultBranch = repositoryDocument.RootElement.GetProperty("default_branch").GetString()
            ?? throw new InvalidOperationException($"GitHub repository '{repository}' has no default branch.");

        using var referenceResponse = await SendAsync(
            HttpMethod.Get,
            $"repos/{repository}/git/ref/heads/{defaultBranch}",
            null,
            cancellationToken);
        using var referenceDocument = await ReadJsonAsync(referenceResponse, cancellationToken);
        var sha = referenceDocument.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException($"GitHub repository '{repository}' default branch has no commit SHA.");
        var branch = $"charter/init-{Guid.NewGuid():N}"[..21];

        using var ignoredRef = await SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha },
            cancellationToken);
        using var ignoredContent = await SendAsync(
            HttpMethod.Put,
            $"repos/{repository}/contents/.dsf/charter.md",
            new
            {
                message = $"docs: add product charter for {product}",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                branch,
            },
            cancellationToken);
        using var pullResponse = await SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/pulls",
            new
            {
                title = $"Add product charter for {product}",
                head = branch,
                @base = defaultBranch,
                body = "Human-owned Product Charter. Review, edit, and merge to make it authoritative.",
            },
            cancellationToken);
        using var pullDocument = await ReadJsonAsync(pullResponse, cancellationToken);
        return pullDocument.RootElement.GetProperty("html_url").GetString()
            ?? throw new InvalidOperationException("GitHub created a charter pull request without a URL.");
    }

    public async Task<CharterPullRequest?> LatestPullRequestWithHeadPrefixAsync(
        string repository,
        string headPrefix,
        CancellationToken cancellationToken)
    {
        RequireToken();
        var target = $"repos/{repository}/pulls?state=open&per_page=100";
        using var response = await SendAsync(HttpMethod.Get, target, null, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var latest = document.RootElement.EnumerateArray()
            .Where(item =>
                item.GetProperty("head").GetProperty("ref").GetString()
                    ?.StartsWith(headPrefix, StringComparison.Ordinal) == true)
            .OrderByDescending(item => item.GetProperty("number").GetInt32())
            .FirstOrDefault();
        if (latest.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return new CharterPullRequest(
            latest.GetProperty("html_url").GetString() ?? string.Empty,
            latest.GetProperty("state").GetString() ?? string.Empty);
    }

    public async Task<string> OpenFilePullRequestAsync(
        string repository,
        string path,
        string content,
        string branch,
        string title,
        string body,
        string message,
        bool enableAutoMerge,
        string? existingSha,
        CancellationToken cancellationToken)
    {
        RequireToken();
        using var repositoryResponse = await SendAsync(HttpMethod.Get, $"repos/{repository}", null, cancellationToken);
        using var repositoryDocument = await ReadJsonAsync(repositoryResponse, cancellationToken);
        var defaultBranch = repositoryDocument.RootElement.GetProperty("default_branch").GetString() ?? "main";

        using var referenceResponse = await SendAsync(
            HttpMethod.Get,
            $"repos/{repository}/git/ref/heads/{defaultBranch}",
            null,
            cancellationToken);
        using var referenceDocument = await ReadJsonAsync(referenceResponse, cancellationToken);
        var sha = referenceDocument.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException($"GitHub repository '{repository}' default branch has no commit SHA.");

        using var ignoredRef = await SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha },
            cancellationToken);
        var contentBody = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            ["branch"] = branch,
        };
        if (!string.IsNullOrWhiteSpace(existingSha))
        {
            contentBody["sha"] = existingSha;
        }

        using var ignoredContent = await SendAsync(
            HttpMethod.Put,
            $"repos/{repository}/contents/{path}",
            contentBody,
            cancellationToken);
        using var pullResponse = await SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/pulls",
            new
            {
                title,
                head = branch,
                @base = defaultBranch,
                body,
            },
            cancellationToken);
        using var pullDocument = await ReadJsonAsync(pullResponse, cancellationToken);
        var url = pullDocument.RootElement.GetProperty("html_url").GetString()
            ?? throw new InvalidOperationException("GitHub created a pull request without a URL.");

        if (enableAutoMerge)
        {
            RunGh(["pr", "merge", url, "--repo", repository, "--auto", "--squash"], cancellationToken);
        }

        return url;
    }

    public async Task<CharterIssue> CreateIssueAsync(
        string repository,
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        RequireToken();
        using var response = await SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/issues",
            new { title, body, labels },
            cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        return new CharterIssue(
            document.RootElement.GetProperty("html_url").GetString()
                ?? throw new InvalidOperationException("GitHub created an issue without a URL."),
            document.RootElement.GetProperty("node_id").GetString() ?? string.Empty);
    }

    public Task<bool> AssignCopilotWithGhAsync(
        string repository,
        string issueNodeId,
        CancellationToken cancellationToken)
    {
        try
        {
            var (owner, name) = SplitRepository(repository);
            var actors = RunGh(
                [
                    "api", "graphql",
                    "-f", "query=query($owner:String!,$name:String!){repository(owner:$owner,name:$name){suggestedActors(capabilities:[CAN_BE_ASSIGNED],first:100){nodes{login __typename ... on Bot{id}}}}}",
                    "-f", $"owner={owner}",
                    "-f", $"name={name}",
                ],
                cancellationToken,
                throwOnError: false);
            if (actors is null)
            {
                return Task.FromResult(false);
            }

            using var actorsDocument = JsonDocument.Parse(actors);
            string? botId = null;
            foreach (var node in actorsDocument.RootElement
                         .GetProperty("data")
                         .GetProperty("repository")
                         .GetProperty("suggestedActors")
                         .GetProperty("nodes")
                         .EnumerateArray())
            {
                if (node.GetProperty("login").GetString() == "copilot-swe-agent")
                {
                    botId = node.GetProperty("id").GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(botId))
            {
                return Task.FromResult(false);
            }

            var assigned = RunGh(
                [
                    "api", "graphql",
                    "-f", "query=mutation($assignableId:ID!,$actorId:ID!){replaceActorsForAssignable(input:{assignableId:$assignableId,actorIds:[$actorId]}){assignable{__typename}}}",
                    "-f", $"assignableId={issueNodeId}",
                    "-f", $"actorId={botId}",
                ],
                cancellationToken,
                throwOnError: false) is not null;
            return Task.FromResult(assigned);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> AssignCopilotWithAppAsync(
        string repository,
        string issueNodeId,
        CancellationToken cancellationToken)
    {
        RequireToken();
        var (owner, name) = SplitRepository(repository);
        try
        {
            using var actors = await GraphQlAsync(
                "query($owner:String!,$name:String!){repository(owner:$owner,name:$name){suggestedActors(capabilities:[CAN_BE_ASSIGNED],first:100){nodes{login __typename ... on Bot{id}}}}}",
                new Dictionary<string, object?>
                {
                    ["owner"] = owner,
                    ["name"] = name,
                },
                cancellationToken);
            var nodes = actors.RootElement.GetProperty("data").GetProperty("repository")
                .GetProperty("suggestedActors").GetProperty("nodes");
            var botId = nodes.EnumerateArray()
                .FirstOrDefault(node => node.GetProperty("login").GetString() == "copilot-swe-agent")
                .GetProperty("id")
                .GetString();
            if (string.IsNullOrWhiteSpace(botId))
            {
                return false;
            }

            using var ignored = await GraphQlAsync(
                "mutation($assignableId:ID!,$actorId:ID!){replaceActorsForAssignable(input:{assignableId:$assignableId,actorIds:[$actorId]}){assignable{__typename}}}",
                new Dictionary<string, object?>
                {
                    ["assignableId"] = issueNodeId,
                    ["actorId"] = botId,
                },
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is GitHubApiException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity }
                or JsonException
                or KeyNotFoundException)
        {
            return false;
        }
    }

    public Task<int?> NewestReadyIssueAsync(
        string repository,
        string label,
        CancellationToken cancellationToken)
    {
        var output = RunGh(
            ["issue", "list", "--repo", repository, "--label", label, "--state", "open", "--limit", "20", "--json", "number"],
            cancellationToken,
            throwOnError: false);
        if (output is null)
        {
            return Task.FromResult<int?>(null);
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var numbers = document.RootElement.EnumerateArray()
                .Where(row => row.TryGetProperty("number", out _))
                .Select(row => row.GetProperty("number").GetInt32());
            return Task.FromResult<int?>(numbers.Any() ? numbers.Max() : null);
        }
        catch (JsonException)
        {
            return Task.FromResult<int?>(null);
        }
    }

    public Task<CodingAgentPullRequest?> FindCodingAgentPullRequestAsync(
        string repository,
        int issueNumber,
        CancellationToken cancellationToken)
    {
        var (owner, name) = SplitRepository(repository);
        var output = RunGh(
            [
                "api", "graphql",
                "-f", "query=query($owner:String!,$name:String!,$num:Int!){repository(owner:$owner,name:$name){issue(number:$num){timelineItems(itemTypes:[CONNECTED_EVENT,CROSS_REFERENCED_EVENT],first:50){nodes{__typename ... on ConnectedEvent{subject{__typename ... on PullRequest{number url isDraft state author{login}}}} ... on CrossReferencedEvent{source{__typename ... on PullRequest{number url isDraft state author{login}}}}}}}}}",
                "-f", $"owner={owner}",
                "-f", $"name={name}",
                "-F", $"num={issueNumber}",
            ],
            cancellationToken,
            throwOnError: true);
        using var document = JsonDocument.Parse(output!);
        var pulls = new List<CodingAgentPullRequest>();
        foreach (var node in document.RootElement.GetProperty("data").GetProperty("repository").GetProperty("issue")
                     .GetProperty("timelineItems").GetProperty("nodes").EnumerateArray())
        {
            JsonElement pr;
            if (node.TryGetProperty("subject", out var subject))
            {
                pr = subject;
            }
            else if (node.TryGetProperty("source", out var source))
            {
                pr = source;
            }
            else
            {
                continue;
            }

            if (pr.GetProperty("__typename").GetString() != "PullRequest")
            {
                continue;
            }

            var login = pr.GetProperty("author").GetProperty("login").GetString() ?? string.Empty;
            if (login.Split('/')[^1] != "copilot-swe-agent")
            {
                continue;
            }

            pulls.Add(new CodingAgentPullRequest(
                pr.GetProperty("number").GetInt32(),
                pr.GetProperty("url").GetString() ?? string.Empty,
                pr.GetProperty("isDraft").GetBoolean(),
                pr.GetProperty("state").GetString() ?? string.Empty));
        }

        return Task.FromResult<CodingAgentPullRequest?>(pulls
            .OrderByDescending(pr => string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(pr => pr.Number)
            .FirstOrDefault());
    }

    public Task<bool> HasCopilotReviewRequestAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        var (owner, name) = SplitRepository(repository);
        var output = RunGh(
            [
                "api", "graphql",
                "-f", "query=query($owner:String!,$name:String!,$num:Int!){repository(owner:$owner,name:$name){pullRequest(number:$num){reviewRequests(first:20){nodes{requestedReviewer{__typename ... on Bot{login} ... on User{login}}}}}}}",
                "-f", $"owner={owner}",
                "-f", $"name={name}",
                "-F", $"num={pullRequestNumber}",
            ],
            cancellationToken,
            throwOnError: true);
        using var document = JsonDocument.Parse(output!);
        var requested = document.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest")
            .GetProperty("reviewRequests").GetProperty("nodes").EnumerateArray()
            .Any(node => (node.GetProperty("requestedReviewer").GetProperty("login").GetString() ?? string.Empty)
                .Contains("copilot", StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(requested);
    }

    public Task RequestCopilotReviewAsync(
        string repository,
        string pullRequestUrl,
        CancellationToken cancellationToken)
    {
        RunGh(["pr", "edit", pullRequestUrl, "--repo", repository, "--add-reviewer", "@copilot"], cancellationToken);
        return Task.CompletedTask;
    }

    public Task<bool> AgentWorkFinishedAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        var (owner, name) = SplitRepository(repository);
        var output = RunGh(
            ["api", "--paginate", $"repos/{owner}/{name}/issues/{pullRequestNumber}/timeline"],
            cancellationToken);
        using var document = JsonDocument.Parse(output!);
        string? latest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var itemEvent = item.TryGetProperty("event", out var value) ? value.GetString() : null;
            if (itemEvent is "copilot_work_started" or "copilot_work_finished")
            {
                latest = itemEvent;
            }
        }

        return Task.FromResult(latest == "copilot_work_finished");
    }

    public Task MarkPullRequestReadyAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        RunGh(["pr", "ready", pullRequestNumber.ToString(), "--repo", repository], cancellationToken);
        return Task.CompletedTask;
    }

    private void RequireToken()
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GitHub charter operations require GH_TOKEN or GITHUB_TOKEN.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string target,
        object? body,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("dsf-cli");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new GitHubApiException(
            response.StatusCode,
            $"GitHub API {method} {target} failed with {(int)response.StatusCode}: {detail}");
    }

    private async Task<JsonDocument> GraphQlAsync(
        string query,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "graphql",
            new { query, variables },
            cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);
        if (document.RootElement.TryGetProperty("errors", out var errors))
        {
            document.Dispose();
            throw new InvalidOperationException($"GraphQL error: {errors}");
        }

        return document;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private static (string Owner, string Name) SplitRepository(string repository)
    {
        var parts = repository.Split('/', 2);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"GitHub repository '{repository}' must be owner/name.");
        }

        return (parts[0], parts[1]);
    }

    private static string? RunGh(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnError = true)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "gh";
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode == 0)
            {
                return output;
            }

            if (throwOnError)
            {
                throw new InvalidOperationException($"gh {string.Join(' ', arguments)} failed: {error}");
            }

            return null;
        }
        catch (System.ComponentModel.Win32Exception) when (!throwOnError)
        {
            return null;
        }
    }
}
