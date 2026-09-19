namespace Dsf.Core.Onboarding;

public enum FactoryPublicationState { Intended, Dispatched, NotDispatched, Rejected, Unknown, Confirmed }

public sealed record FactoryPublicationIntent(int Protocol, Guid FactoryId, string Product, long RepositoryId,
    string LogicalIntent, Guid InvocationId, string Title, string Body, string InputsSha256,
    IReadOnlyList<AuditRecordIdentity> References)
{
    [System.Text.Json.Serialization.JsonRequired] public RepositoryMutationReview AutomationReview { get; init; } = null!;
}

public sealed record FactoryPublicationAttempt(Guid Id, DateTimeOffset IssuedAt, FactoryPublicationState State,
    int? StatusCode = null, string? RequestId = null)
{
    public string? MutationReviewSha256 { get; init; }
}

public sealed record FactoryPublicationReceipt(long RepositoryId, long IssueId, string NodeId, long Number,
    string Url, DateTimeOffset CreatedAt, string RequestId, string ResponseSha256)
{
    [System.Text.Json.Serialization.JsonRequired] public string ResponseJson { get; init; } = "";
}

public sealed record FactoryPublicationOrigin(int Protocol, string Id, string Product, Guid FactoryId, long RepositoryId,
    string LogicalIntent, string IntentSha256, DateTimeOffset CreatedAt);

public sealed record FactoryPublicationDocument(int Protocol, string Id, string Product, FactoryPublicationIntent Intent,
    DateTimeOffset IntendedAt, FactoryPublicationState State, IReadOnlyList<FactoryPublicationAttempt> Attempts,
    FactoryPublicationReceipt? Receipt)
{
    [System.Text.Json.Serialization.JsonRequired] public AuditRecordIdentity Origin { get; init; } = null!;
    public string Checksum { get; init; } = "";
    public string CalculateChecksum() => FactoryModelsValidation.Hash(this with { Checksum = "" });
}

public sealed record FactoryPublicationResult(FactoryPublicationState State, FactoryPublicationDocument Journal, string? Reason = null)
{
    public AuditRecordIdentity? NativeIdentity { get; init; }
    public FactoryPublicationReceipt? Receipt => State == FactoryPublicationState.Confirmed ? Journal.Receipt : null;
    public IReadOnlyList<FactoryPublicationAttempt> Attempts => Journal.Attempts;
}

public static class FactoryPublication
{
    public const string Container = "proposal-publications";
    public const int MaximumAttempts = 64;
    public static IReadOnlyDictionary<string, string> GitHubPermissions { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        { ["metadata"] = "read", ["contents"] = "read", ["issues"] = "write", ["actions"] = "read", ["repository_hooks"] = "read" });
    public static string Id(FactoryPublicationIntent intent) => "proposal-" + FactoryModelsValidation.Hash(
        new { intent.FactoryId, intent.RepositoryId, intent.LogicalIntent });
    public static string OriginId(FactoryPublicationIntent intent) => Id(intent) + "-origin";

    public static FactoryPublicationDocument ReadDocument(string json, DateTimeOffset now)
    {
        var value = System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: new() { AllowDuplicateProperties = false })!.AsObject();
        foreach (var key in new[] { "_rid", "_etag", "_self", "_attachments", "_ts" }) value.Remove(key);
        var document = System.Text.Json.JsonSerializer.Deserialize<FactoryPublicationDocument>(value.ToJsonString(),
            FactorySourceValidation.RecordJson) ?? throw new System.Text.Json.JsonException();
        Validate(document.Intent);
        if (document.Protocol != 1 || document.Id != Id(document.Intent) || document.Product != document.Intent.Product
            || document.Checksum != document.CalculateChecksum() || document.IntendedAt == default || document.IntendedAt > now
            || !Enum.IsDefined(document.State) || document.Attempts.Count > MaximumAttempts
            || document.Attempts.Select(a => a.Id).Distinct().Count() != document.Attempts.Count
            || document.Attempts.Any(a => a.Id == Guid.Empty || !Enum.IsDefined(a.State) || a.IssuedAt < document.IntendedAt
                || a.IssuedAt > now || a.State == FactoryPublicationState.Intended
                || a.MutationReviewSha256 is not null && !FactorySourceValidation.Hash(a.MutationReviewSha256)
                || a.State == FactoryPublicationState.Rejected && (a.StatusCode is not (401 or 403 or 404 or 422) || !RequestId(a.RequestId))
                || a.State == FactoryPublicationState.Confirmed && (a.StatusCode != 201 || !RequestId(a.RequestId))
                || a.State is FactoryPublicationState.Dispatched or FactoryPublicationState.NotDispatched or FactoryPublicationState.Unknown
                    && (a.StatusCode is not null || a.RequestId is not null))
            || document.Attempts.SkipLast(1).Any(a => a.State is not (FactoryPublicationState.Rejected or FactoryPublicationState.NotDispatched))
            || (document.State == FactoryPublicationState.Intended ? document.Attempts.Count != 0
                : document.Attempts.Count == 0 || document.Attempts.Last().State != document.State)
            || (document.State == FactoryPublicationState.Confirmed) != (document.Receipt is not null)
            || document.Origin.FactoryId != document.Intent.FactoryId || document.Origin.Store != AuditStoreKind.Publication
            || document.Origin.Key != $"dbs/{document.Product}/colls/{Container}/docs/{OriginId(document.Intent)}"
            || !FactorySourceValidation.Hash(document.Origin.Sha256) || string.IsNullOrWhiteSpace(document.Origin.NativeRevision))
            throw new System.Text.Json.JsonException("Complete original native publication journal required.");
        _ = Dsf.Core.Runtime.NativeCosmosResourceId.RequireDocument(document.Origin.NativeResourceId);
        if (document.Receipt is { } receipt) ValidateReceipt(document.Intent, document.Attempts.Last(), receipt, now);
        return document;
    }

    public static void ValidateReceipt(FactoryPublicationIntent intent, FactoryPublicationAttempt attempt, FactoryPublicationReceipt receipt, DateTimeOffset now)
    {
        if (receipt.RepositoryId != intent.RepositoryId || receipt.IssueId <= 0 || receipt.Number <= 0
            || string.IsNullOrWhiteSpace(receipt.NodeId) || receipt.NodeId.Length > 256
            || !RequestId(receipt.RequestId) || !FactorySourceValidation.Hash(receipt.ResponseSha256)
            || attempt.State == FactoryPublicationState.Confirmed && attempt.RequestId != receipt.RequestId
            || receipt.CreatedAt < attempt.IssuedAt.AddMinutes(-5) || receipt.CreatedAt > now.AddMinutes(5)
            || System.Text.Encoding.UTF8.GetByteCount(receipt.ResponseJson) is not (> 0 and <= 256 * 1024)
            || !Uri.TryCreate(receipt.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com"
            || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0
            || url.AbsolutePath.Split('/') is not ["", { Length: > 0 } owner, { Length: > 0 } repository, "issues", { } number]
            || number != receipt.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new System.Text.Json.JsonException("Original returned issue identity required.");
        using var response = System.Text.Json.JsonDocument.Parse(receipt.ResponseJson, new() { AllowDuplicateProperties = false });
        var body = response.RootElement;
        if (FactoryModelsValidation.Hash(body) != receipt.ResponseSha256 || body.GetProperty("id").GetInt64() != receipt.IssueId
            || body.GetProperty("node_id").GetString() != receipt.NodeId || body.GetProperty("number").GetInt64() != receipt.Number
            || body.GetProperty("html_url").GetString() != receipt.Url || body.GetProperty("created_at").GetDateTimeOffset() != receipt.CreatedAt
            || body.GetProperty("repository_url").GetString() != $"https://api.github.com/repos/{owner}/{repository}"
            || body.GetProperty("url").GetString() != $"https://api.github.com/repos/{owner}/{repository}/issues/{number}"
            || body.GetProperty("title").GetString() != intent.Title || body.GetProperty("body").GetString() != intent.Body
            || body.GetProperty("state").GetString() != "open" || body.TryGetProperty("pull_request", out _)
            || body.GetProperty("assignees").GetArrayLength() != 0 || body.GetProperty("milestone").ValueKind != System.Text.Json.JsonValueKind.Null
            || !body.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()).SequenceEqual(["dsf:proposal"]))
            throw new System.Text.Json.JsonException("Original native issue creation response differs from retained identity.");
    }

    private static bool RequestId(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    public static void Validate(FactoryPublicationIntent intent)
    {
        if (intent.Protocol != 1 || intent.FactoryId == Guid.Empty || intent.InvocationId == Guid.Empty
            || intent.RepositoryId <= 0 || string.IsNullOrWhiteSpace(intent.Product) || intent.Product.Length > 90
            || intent.Product.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || string.IsNullOrWhiteSpace(intent.LogicalIntent) || intent.LogicalIntent.Length > 2048
            || intent.LogicalIntent.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(intent.Title) || intent.Title.Length > 256 || intent.Title.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(intent.Body) || System.Text.Encoding.UTF8.GetByteCount(intent.Body) > 60000
            || !FactorySourceValidation.Hash(intent.InputsSha256) || intent.References is not { Count: > 0 and <= 128 }
            || intent.AutomationReview is not { Mutation: RepositoryMutationClass.ProposalIssue, State: RepositoryReviewState.Confirmed,
                Declaration: RepositoryAutomationDeclaration.Clear, VisibleInventoryComplete: true, DeclarationOwner.Length: > 0 }
            || intent.References.Any(r => r.FactoryId != intent.FactoryId || !Enum.IsDefined(r.Store)
                || string.IsNullOrWhiteSpace(r.Key) || string.IsNullOrWhiteSpace(r.NativeRevision) || !FactorySourceValidation.Hash(r.Sha256))
            || intent.References.Select(r => (r.Store, r.Key)).Distinct().Count() != intent.References.Count)
            throw new System.Text.Json.JsonException("Complete bounded original publication intent required.");
    }
}
