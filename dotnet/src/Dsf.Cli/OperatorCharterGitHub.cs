using System.Globalization;
using System.Text.Json;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed class OperatorCharterGitHub(IGhCliRunner gh)
{
    internal sealed record Response(int Status, JsonElement Body, CharterNativeReceipt Receipt, string ResponseJson);

    internal Response Request(string method, string route, object? body, CancellationToken token)
    {
        var input = body is null ? null : JsonSerializer.Serialize(body);
        string[] arguments = ["api", "--hostname", "github.com", "--method", method, "--include",
            "--header", "X-GitHub-Api-Version: 2022-11-28", .. input is null ? Array.Empty<string>() : ["--input", "-"], route];
        var result = gh.Run(arguments, token, input);
        var text = result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (text.Length > 4 * 1024 * 1024 || end < 0 || !text.StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(text.Split('\n')[0].Split(' ')[1], CultureInfo.InvariantCulture, out var status))
            throw new CharterOperationException(FactoryCharterState.Unknown, "Native GitHub response unconfirmed; never replay an uncertain effect.");
        var requestId = text[..end].Split('\n').SingleOrDefault(line => line.StartsWith("x-github-request-id:", StringComparison.OrdinalIgnoreCase))?
            .Split(':', 2)[1].Trim() ?? "";
        if (requestId.Length > 128 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != ':'))
            throw new JsonException();
        var json = text[(end + 2)..];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        if (result.ExitCode != 0 && status is >= 200 and < 300) throw new JsonException();
        return new(status, document.RootElement.Clone(), new(method, route, status, requestId, FactoryModelsValidation.Hash(json)), json);
    }

    internal void CheckBase(CharterReview review, CancellationToken token)
    {
        var repository = Get("repositories/" + review.RepositoryId, token);
        if (repository.GetProperty("id").GetInt64() != review.RepositoryId
            || repository.GetProperty("node_id").GetString() != review.RepositoryNodeId
            || repository.GetProperty("owner").GetProperty("login").GetString() + "/" + repository.GetProperty("name").GetString() != review.Repository
            || repository.GetProperty("default_branch").GetString() != review.DefaultBranch
            || !repository.GetProperty("permissions").GetProperty("push").GetBoolean())
            throw new CharterOperationException(FactoryCharterState.Blocked, "Current operator push permission and exact durable repository/default branch required.");
        if (Head(review, review.DefaultBranch, token) != review.Commit)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Default head changed; review current complete source before publication.");
        var commit = Get(Route(review, "git/commits/" + review.Commit), token);
        if (commit.GetProperty("sha").GetString() != review.Commit || commit.GetProperty("tree").GetProperty("sha").GetString() != review.RootTree)
            throw new JsonException();
        CheckFile(review, review.Commit, review.Before, review.BeforeBlob, token);
    }

    internal void CheckFile(CharterReview review, string commit, string? content, string? blob, CancellationToken token)
    {
        var file = Request("GET", Route(review, "contents/" + CharterMarkdown.CharterPath + "?ref=" + commit), null, token);
        if (content is null && file.Status == 404) return;
        if (file.Status != 200 || content is null || file.Body.GetProperty("type").GetString() != "file"
            || file.Body.GetProperty("path").GetString() != CharterMarkdown.CharterPath
            || file.Body.GetProperty("sha").GetString() != blob || file.Body.GetProperty("encoding").GetString() != "base64"
            || !Convert.FromBase64String(file.Body.GetProperty("content").GetString()!).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(content)))
            throw new CharterOperationException(FactoryCharterState.Blocked, "Exact native charter content changed or unreadable; no overwrite.");
    }

    internal string? Head(CharterReview review, string branch, CancellationToken token)
    {
        var response = Request("GET", Route(review, "git/ref/heads/" + Uri.EscapeDataString(branch)), null, token);
        if (response.Status == 404) return null;
        if (response.Status != 200 || response.Body.GetProperty("ref").GetString() != "refs/heads/" + branch
            || response.Body.GetProperty("object").GetProperty("type").GetString() != "commit")
            throw new CharterOperationException(FactoryCharterState.Unknown, "Native branch identity unavailable.");
        return GitId(response.Body.GetProperty("object").GetProperty("sha"));
    }

    internal void CheckProposal(CharterReview review, CharterPublication publication, CancellationToken token)
    {
        var commit = publication.Effects[1].ObjectId!;
        CheckBranch(review, publication, commit, token);
        CheckCommit(review, publication, token);
    }

    internal void CheckEmptyBranch(CharterReview review, CharterPublication publication, CancellationToken token) =>
        CheckBranch(review, publication, review.Commit, token);

    private void CheckBranch(CharterReview review, CharterPublication publication, string head, CancellationToken token)
    {
        var response = Get(Route(review, "git/ref/heads/" + Uri.EscapeDataString(publication.Branch["refs/heads/".Length..])), token);
        if (response.GetProperty("ref").GetString() != publication.Branch
            || response.GetProperty("node_id").GetString() != publication.Effects[0].ObjectId
            || response.GetProperty("object").GetProperty("type").GetString() != "commit"
            || response.GetProperty("object").GetProperty("sha").GetString() != head)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Original proposal ref/head changed; no human overwrite, force-push or inferred ref ownership.");
    }

    internal void CheckCommit(CharterReview review, CharterPublication publication, CancellationToken token)
    {
        var commit = publication.Effects[1].ObjectId!;
        CheckFile(review, commit, review.After, review.AfterBlob, token);
        var compared = Get(Route(review, "compare/" + review.Commit + "..." + commit), token);
        var files = compared.GetProperty("files").EnumerateArray().ToArray();
        if (compared.GetProperty("ahead_by").GetInt32() != 1 || compared.GetProperty("behind_by").GetInt32() != 0
            || compared.GetProperty("total_commits").GetInt32() != 1 || files.Length != 1
            || files[0].GetProperty("filename").GetString() != CharterMarkdown.CharterPath
            || files[0].GetProperty("sha").GetString() != review.AfterBlob
            || files[0].GetProperty("status").GetString() != (review.Before is null ? "added" : "modified"))
            throw new CharterOperationException(FactoryCharterState.Blocked, "Proposal is not the exact approved charter-only change.");
    }

    internal JsonElement Get(string route, CancellationToken token)
    {
        var result = Request("GET", route, null, token);
        return result.Status == 200 ? result.Body : throw new CharterOperationException(
            result.Status is 401 or 403 or 404 ? FactoryCharterState.Blocked : FactoryCharterState.Unknown, "Native GitHub read unavailable; no cached authority.");
    }
    internal static string GitId(JsonElement value) => value.GetString() is { } id && FactorySourceValidation.GitId(id) ? id : throw new JsonException();
    internal static string Route(CharterReview review, string route) => "repos/" + review.Repository + "/" + route;
}
