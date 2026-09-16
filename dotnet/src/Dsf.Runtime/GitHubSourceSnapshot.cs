using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class GitHubSourceSnapshot(FactorySourceAccess access, TimeProvider clock)
{
    private string? ephemeralToken;
    private DateTimeOffset tokenObserved;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal async Task<SourceSnapshot> PinAsync(CancellationToken token)
    {
        var repository = await RepositoryAsync(token);
        using var reference = await ReadAsync(repository.Name, "git/ref/heads/" + Uri.EscapeDataString(repository.Branch), 16384, token);
        var json = reference.RootElement;
        var commit = json.GetProperty("object").GetProperty("sha").GetString()!;
        if (json.GetProperty("ref").GetString() != "refs/heads/" + repository.Branch
            || json.GetProperty("object").GetProperty("type").GetString() != "commit" || !FactorySourceValidation.GitId(commit)) throw new JsonException();
        using var revision = await ReadAsync(repository.Name, "git/commits/" + commit, 1024 * 1024, token);
        var tree = revision.RootElement.GetProperty("tree").GetProperty("sha").GetString()!;
        if (revision.RootElement.GetProperty("sha").GetString() != commit || !FactorySourceValidation.GitId(tree)) throw new JsonException();
        if (repository != await RepositoryAsync(token)) throw new SourceAcquisitionException(SourceState.Blocked, "RepositoryMetadataRace");
        var factory = access.Manifest.Identity.FactoryId;
        var id = FactoryModelsValidation.Hash(new { factory, access.Source.OperationId, access.GitHub.RepositoryId, repository.Branch, commit, tree });
        return new(1, factory, access.Source.OperationId, access.Source.ApprovalFingerprint,
            access.GitHub.RepositoryId, access.GitHub.AccountId, repository.Name, repository.Branch, commit, tree, clock.GetUtcNow(), id);
    }

    internal async Task CheckSnapshotAsync(SourceSnapshot snapshot, CancellationToken token)
    {
        var current = await PinAsync(token);
        if (current.Id != snapshot.Id || current.RepositoryId != snapshot.RepositoryId || current.AccountId != snapshot.AccountId
            || current.Commit != snapshot.Commit || current.RootTree != snapshot.RootTree || current.DefaultBranch != snapshot.DefaultBranch
            || snapshot.OperationId != access.Source.OperationId || snapshot.ObservedAt == default || snapshot.ObservedAt > clock.GetUtcNow()
            || snapshot.ConfigurationRevision != access.Source.ApprovalFingerprint || snapshot.FactoryId != access.Manifest.Identity.FactoryId)
            throw new SourceAcquisitionException(SourceState.Blocked, "SourceIdentityChanged");
    }

    internal async Task<IReadOnlyList<SourceNode>> TreeAsync(SourceSnapshot snapshot, SourceNode parent, CancellationToken token)
    {
        var repository = await RepositoryAsync(token);
        CheckMetadata(snapshot, repository);
        using var document = await ReadAsync(repository.Name, "git/trees/" + parent.ObjectId, FactorySourceValidation.MaximumTreeBytes, token);
        var json = document.RootElement;
        if (json.GetProperty("sha").GetString() != parent.ObjectId) throw new JsonException();
        if (json.GetProperty("truncated").ValueKind != JsonValueKind.False)
            throw new SourceAcquisitionException(SourceState.Partial, "IncompleteTree");
        var entries = json.GetProperty("tree").EnumerateArray().ToArray();
        if (entries.Length > FactorySourceValidation.MaximumNodes) throw new SourceAcquisitionException(SourceState.Partial, "InventoryLimit");
        var children = new List<SourceNode>();
        using var canonical = new MemoryStream();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries.OrderBy(e => SortKey(e), ByteComparer.Instance))
        {
            var name = item.GetProperty("path").GetString() ?? throw new JsonException();
            var mode = item.GetProperty("mode").GetString() ?? throw new JsonException();
            var type = item.GetProperty("type").GetString();
            var sha = item.GetProperty("sha").GetString()!;
            if (SourceContentPolicy.Sensitive(name)) throw new SourceAcquisitionException(SourceState.Partial, "SensitivePathMetadataGap");
            if (name.Length == 0 || name.Contains('/') || name.Contains('\0') || name is "." or ".."
                || !names.Add(name) || !FactorySourceValidation.GitId(sha)
                || mode is not ("040000" or "100644" or "100755" or "120000" or "160000")
                || type != (mode == "040000" ? "tree" : mode == "160000" ? "commit" : "blob"))
                throw new JsonException("Tree object is not a lossless tracked-path inventory.");
            canonical.Write(Utf8.GetBytes(mode.TrimStart('0') + " " + name + "\0"));
            canonical.Write(Convert.FromHexString(sha));
            var path = parent.Path.Length == 0 ? name : parent.Path + "/" + name;
            if (Utf8.GetByteCount(path) > 2048) throw new SourceAcquisitionException(SourceState.Partial, "PathMetadataLimit");
            var size = item.TryGetProperty("size", out var bytes) && bytes.ValueKind != JsonValueKind.Null ? bytes.GetInt64() : (long?)null;
            if (size < 0) throw new JsonException();
            children.Add(new(snapshot.Id, 0, path, mode, sha, size));
        }
        if (ObjectId("tree", canonical.ToArray()) != parent.ObjectId) throw new SourceAcquisitionException(SourceState.Failed, "TreeIdentityMismatch");
        if (repository != await RepositoryAsync(token)) throw new SourceAcquisitionException(SourceState.Blocked, "RepositoryMetadataRace");
        return children;
    }

    internal async Task<byte[]> BlobAsync(SourceSnapshot snapshot, SourceNode node, CancellationToken token)
    {
        if (node.Size > FactorySourceValidation.MaximumBlobBytes)
            throw new SourceAcquisitionException(SourceState.Partial, "BlobLimit");
        var repository = await RepositoryAsync(token);
        CheckMetadata(snapshot, repository);
        using var document = await ReadAsync(repository.Name, "git/blobs/" + node.ObjectId,
            FactorySourceValidation.MaximumBlobBytes * 2, token);
        var json = document.RootElement;
        if (json.GetProperty("sha").GetString() != node.ObjectId || json.GetProperty("encoding").GetString() != "base64"
            || json.GetProperty("size").GetInt64() > FactorySourceValidation.MaximumBlobBytes) throw new JsonException();
        var bytes = Convert.FromBase64String(json.GetProperty("content").GetString()!);
        try
        {
            if (bytes.Length != json.GetProperty("size").GetInt64() || node.Size is not null && bytes.Length != node.Size
                || ObjectId("blob", bytes) != node.ObjectId) throw new SourceAcquisitionException(SourceState.Failed, "BlobIdentityMismatch");
            if (repository != await RepositoryAsync(token)) throw new SourceAcquisitionException(SourceState.Blocked, "RepositoryMetadataRace");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private async Task<(string Name, string Branch)> RepositoryAsync(CancellationToken token)
    {
        using var request = await RequestAsync("https://api.github.com/repositories/" + access.GitHub.RepositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
        using var document = await access.SendAsync(request, 64 * 1024, token);
        var json = document.RootElement;
        var name = json.GetProperty("full_name").GetString() ?? throw new JsonException();
        var branch = json.GetProperty("default_branch").GetString() ?? throw new JsonException();
        if (SourceContentPolicy.Sensitive(name) || SourceContentPolicy.Sensitive(branch))
            throw new SourceAcquisitionException(SourceState.Partial, "SensitiveRepositoryMetadataGap");
        if (json.GetProperty("id").GetInt64() != access.GitHub.RepositoryId || json.GetProperty("owner").GetProperty("id").GetInt64() != access.GitHub.AccountId
            || json.GetProperty("archived").ValueKind != JsonValueKind.False || json.GetProperty("disabled").ValueKind != JsonValueKind.False
            || !Regex.IsMatch(name, "^[A-Za-z0-9-]+/[A-Za-z0-9_.-]+$") || branch.Length is < 1 or > 256 || branch.Any(char.IsControl))
            throw new SourceAcquisitionException(SourceState.Blocked, "RepositoryIdentityMismatch");
        return (name, branch);
    }
    private static void CheckMetadata(SourceSnapshot snapshot, (string Name, string Branch) metadata)
    {
        if (snapshot.DefaultBranch != metadata.Branch)
            throw new SourceAcquisitionException(SourceState.Blocked, "RepositoryMetadataRace");
    }
    private async Task<JsonDocument> ReadAsync(string repository, string path, int limit, CancellationToken token)
    {
        using var request = await RequestAsync("https://api.github.com/repos/" + repository + "/" + path, token);
        return await access.SendAsync(request, limit, token);
    }
    private async Task<System.Net.Http.HttpRequestMessage> RequestAsync(string uri, CancellationToken token)
    {
        if (ephemeralToken is null || clock.GetUtcNow() - tokenObserved >= TimeSpan.FromSeconds(15))
        {
            ephemeralToken = await access.TokenAsync(token);
            tokenObserved = clock.GetUtcNow();
        }
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ephemeralToken);
        request.Headers.UserAgent.ParseAdd("DSF-Source/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
    private static string ObjectId(string type, byte[] bytes) =>
        Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes(type + " " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"), .. bytes]));
    private static byte[] SortKey(JsonElement entry) => Utf8.GetBytes(entry.GetProperty("path").GetString()
        + (entry.GetProperty("mode").GetString() == "040000" ? "/" : ""));
    private sealed class ByteComparer : IComparer<byte[]>
    {
        internal static readonly ByteComparer Instance = new();
        public int Compare(byte[]? left, byte[]? right) => left.AsSpan().SequenceCompareTo(right);
    }
}
