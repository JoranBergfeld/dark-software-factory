using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime.GitHubApp;

internal sealed class FactoryIssueRejectedException(int status, string requestId)
    : InvalidOperationException("Original GitHub request explicitly rejected.")
{
    internal int Status { get; } = status;
    internal string RequestId { get; } = requestId;
}

/// <summary>Ephemeral token stays inside the key-isolated module; its only mutation is a single issue POST.</summary>
internal sealed class FactoryGitHubProposalAccess(HttpClient http, TimeProvider clock, string access, GitHubAuthorityProof proof)
{
    internal static readonly IReadOnlyDictionary<string, string> Permissions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
        new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "read", ["issues"] = "write",
            ["actions"] = "read", ["repository_hooks"] = "read" });
    private bool issued;

    internal async Task<string> ReviewAsync(CancellationToken token)
    {
        if (proof.TokenExpiresAt <= clock.GetUtcNow().AddSeconds(30)) throw new InvalidOperationException("Fresh publication token required.");
        var repository = await RepositoryAsync(token);
        var label = await GetAsync("/repos/" + proof.Repository + "/labels/dsf%3Aproposal", token);
        if (label.GetProperty("id").GetInt64() <= 0 || label.GetProperty("name").GetString() != "dsf:proposal"
            || label.GetProperty("description").GetString() != DecideRepositoryLabels.Meanings["dsf:proposal"])
            throw new InvalidOperationException("Existing reviewed proposal-only label required; no label creation or repair.");
        var workflows = await GetAsync("/repos/" + proof.Repository + "/actions/workflows?per_page=100", token);
        var inventory = workflows.GetProperty("workflows");
        if (workflows.GetProperty("total_count").GetInt32() != inventory.GetArrayLength() || inventory.GetArrayLength() > 100
            || inventory.EnumerateArray().Any(w => w.GetProperty("id").GetInt64() <= 0
                || w.GetProperty("state").GetString() is not ("disabled_manually" or "disabled_inactivity" or "deleted")))
            throw new InvalidOperationException("Active, incomplete or opaque workflow inventory blocks proposal mutation.");
        var hooks = await GetAsync("/repos/" + proof.Repository + "/hooks?per_page=100", token);
        if (hooks.ValueKind != JsonValueKind.Array || hooks.GetArrayLength() >= 100
            || hooks.EnumerateArray().Any(h => h.GetProperty("id").GetInt64() <= 0 || h.GetProperty("active").GetBoolean()))
            throw new InvalidOperationException("Active, incomplete or opaque webhook inventory blocks proposal mutation.");
        if (!JsonElement.DeepEquals(repository, await RepositoryAsync(token)))
            throw new InvalidOperationException("Durable repository changed during publication review.");
        return FactoryModelsValidation.Hash(new
        {
            Repository = repository,
            Label = new { Id = label.GetProperty("id").GetInt64(), Name = "dsf:proposal", Meaning = label.GetProperty("description").GetString() },
            Workflows = workflows, Hooks = hooks.EnumerateArray().Select(h => new { Id = h.GetProperty("id").GetInt64(), Active = false }).ToArray()
        });
    }

    internal async Task<FactoryPublicationReceipt> CreateAsync(FactoryPublicationIntent intent, CancellationToken token)
    {
        if (issued || intent.FactoryId != proof.FactoryId || intent.RepositoryId != proof.RepositoryId)
            throw new InvalidOperationException("Original factory dispatch may be issued once.");
        token.ThrowIfCancellationRequested();
        using var request = Request(HttpMethod.Post, "/repos/" + proof.Repository + "/issues");
        request.Content = new StringContent(JsonSerializer.Serialize(new { title = intent.Title, body = intent.Body, labels = new[] { "dsf:proposal" } }),
            Encoding.UTF8, "application/json");
        issued = true;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var requestId = response.Headers.TryGetValues("X-GitHub-Request-Id", out var headers) ? headers.SingleOrDefault() : null;
        if (requestId is not { Length: > 0 and <= 256 } || requestId.Any(char.IsControl)) throw new JsonException();
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            throw new FactoryIssueRejectedException((int)response.StatusCode, requestId);
        if (response.StatusCode != HttpStatusCode.Created) throw new JsonException();
        using var document = await EvidencePayload.ReadAsync(response, token, 256 * 1024);
        var issue = document.RootElement;
        var number = issue.GetProperty("number").GetInt64();
        var url = "https://github.com/" + proof.Repository + "/issues/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (issue.GetProperty("id").GetInt64() <= 0 || number <= 0 || string.IsNullOrWhiteSpace(issue.GetProperty("node_id").GetString())
            || issue.GetProperty("html_url").GetString() != url
            || issue.GetProperty("url").GetString() != "https://api.github.com/repos/" + proof.Repository + "/issues/" + number
            || issue.GetProperty("repository_url").GetString() != "https://api.github.com/repos/" + proof.Repository
            || issue.GetProperty("title").GetString() != intent.Title || issue.GetProperty("body").GetString() != intent.Body
            || issue.GetProperty("state").GetString() != "open" || issue.TryGetProperty("pull_request", out _)
            || issue.GetProperty("assignees").GetArrayLength() != 0 || issue.GetProperty("milestone").ValueKind != JsonValueKind.Null
            || !issue.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()).SequenceEqual(["dsf:proposal"]))
            throw new JsonException("Original unassigned proposal creation response required.");
        _ = await RepositoryAsync(token);
        return new(intent.RepositoryId, issue.GetProperty("id").GetInt64(), issue.GetProperty("node_id").GetString()!,
            number, url, issue.GetProperty("created_at").GetDateTimeOffset(), requestId, FactoryModelsValidation.Hash(issue))
            { ResponseJson = issue.GetRawText() };
    }

    private async Task<JsonElement> RepositoryAsync(CancellationToken token)
    {
        var repository = await GetAsync("/repositories/" + proof.RepositoryId, token);
        if (repository.GetProperty("id").GetInt64() != proof.RepositoryId
            || repository.GetProperty("owner").GetProperty("id").GetInt64() != proof.AccountId
            || repository.GetProperty("full_name").GetString() != proof.Repository || repository.GetProperty("archived").GetBoolean()
            || repository.GetProperty("disabled").GetBoolean() || !repository.GetProperty("has_issues").GetBoolean()
            || repository.GetProperty("default_branch").GetString() != proof.DefaultBranch)
            throw new InvalidOperationException("Exact current durable repository identity required.");
        return repository;
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.Contains("Link"))
            throw new InvalidOperationException("Complete current publication metadata unavailable.");
        using var body = await EvidencePayload.ReadAsync(response, token, 256 * 1024);
        return body.RootElement.Clone();
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, "https://api.github.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("dsf-factory-proposal");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
}
