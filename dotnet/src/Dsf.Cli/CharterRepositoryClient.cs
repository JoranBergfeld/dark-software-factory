using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dsf.Cli;

internal sealed record CharterFile(string Content, string Sha);

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

        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("dsf-cli");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"GitHub API GET {target} failed with {(int)response.StatusCode}: {detail}");
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
        CancellationToken cancellationToken)
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
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new InvalidOperationException(
            $"GitHub API {method} {target} failed with {(int)response.StatusCode}: {detail}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";
}
