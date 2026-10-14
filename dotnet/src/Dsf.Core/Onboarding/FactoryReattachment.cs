using System.Security.Cryptography;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public enum FactoryReattachmentState
{
    ReviewRequired, Reattaching, Partial, AwaitingReadiness, Blocked, Failed, Canceled, Unknown,
}

public sealed record FactoryReattachmentPlan(
    int Protocol, string Kind, Guid OperationId, Guid FactoryId, long RepositoryId,
    string ApplicationEnvironment, string AuthorityRevision,
    IReadOnlyList<string> Claims, IReadOnlyList<string> Access,
    IReadOnlyList<string> DriftFindings, IReadOnlyList<string> PreservedAssets,
    string Fingerprint);

public sealed record FactoryReattachmentRequest(
    int Protocol, string Kind, Guid OperationId, Guid FactoryId, string PlanFingerprint,
    string AuthorityRevision, string ApprovedBy, DateTimeOffset RequestedAt);

public sealed record FactoryReattachmentRetirementProgress(
    int Protocol, Guid OperationId, string PlanFingerprint, int Records, int Applied);

public sealed record FactoryReattachmentRetiredRecord(
    int Protocol, Guid OperationId, int Index, string Key, string ETag, string Sha256, int Bytes, int Chunks);

public sealed record FactoryReattachmentRetiredChunk(
    int Protocol, Guid OperationId, int Record, int Index, string Data);

public sealed record FactoryReattachmentPointer(
    Guid OperationId, FactoryReattachmentState State, string PlanFingerprint,
    string AuthorityRevision, DateTimeOffset RequestedAt);

public sealed record FactoryReattachmentResult(FactoryReattachmentState State, string Detail)
{
    public FactoryReattachmentPlan? Plan { get; init; }
    public FactoryReattachmentRequest? Request { get; init; }
    public bool AdmissionOpen { get; init; }
    public bool IsActivated => false;
    public IReadOnlyList<string> CompletedActions { get; init; } = [];
    public IReadOnlyList<string> BlockedActions { get; init; } = [];
    public string NextAction => State switch
    {
        FactoryReattachmentState.ReviewRequired =>
            "Review current same-identity drift, claims and access; approve only this fingerprint.",
        FactoryReattachmentState.AwaitingReadiness =>
            "Renew source, charter, telemetry, model and supplemental proof; run a full supervised preview, then separately activate its exact revision.",
        _ => "Inspect the original reattachment operation and current owner/provider state; never replace identity, force claims or infer activation.",
    };
}

public static class FactoryReattachment
{
    public const string Key = "dsf/factory/reattachment";
    public const string AccessPrefix = "dsf/factory/reattachment-access/";
    public const string HistoryPrefix = FactoryAudit.Namespace + "reattachment/";
    public const string Kind = "decide-reattachment-request";
    public static string HistoryKey(Guid operation) => HistoryPrefix + operation.ToString("N") + "/request";
    public static string PlanKey(Guid operation) => HistoryPrefix + operation.ToString("N") + "/plan";
    public static string RetirementProgressKey(Guid operation) =>
        HistoryPrefix + operation.ToString("N") + "/retirement/current";
    public static string RetiredKey(Guid operation, int index) =>
        HistoryPrefix + operation.ToString("N") + "/retired/" + index.ToString("D4") + "/current";
    public static string RetiredChunkKey(Guid operation, int record, int index) =>
        HistoryPrefix + operation.ToString("N") + "/retired/" + record.ToString("D4") + "/chunks/" + index.ToString("D4");
    public static string AccessKey(Guid operation, int index) =>
        AccessPrefix + operation.ToString("N") + "/" + index.ToString("D4");
    public static string RetirementKey(Guid factory) => "dsf/onboarding/retired/" + factory.ToString("N");
    public static bool IsStopRecord(string key) => key == Key;

    public static string Fingerprint(FactoryReattachmentPlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan with { Fingerprint = "" }, FactoryGitHubJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static FactoryReattachmentRequest Read(string value, FactoryReservationIdentity identity)
    {
        var request = JsonSerializer.Deserialize<FactoryReattachmentRequest>(value, FactoryGitHubJson.Options)
            ?? throw new JsonException();
        if (request.Protocol != 1 || request.Kind != Kind || request.OperationId == Guid.Empty
            || request.FactoryId != identity.FactoryId || request.PlanFingerprint.Length != 64
            || request.PlanFingerprint.Any(character => !char.IsAsciiHexDigit(character))
            || string.IsNullOrWhiteSpace(request.AuthorityRevision)
            || request.ApprovedBy != identity.AzureActorId || request.RequestedAt.Offset != TimeSpan.Zero
            || request.RequestedAt < identity.CreatedAt)
            throw new JsonException("Protected reattachment request binding is invalid.");
        return request;
    }
}
