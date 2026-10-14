using System.Security.Cryptography;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public enum FactoryPurgeState
{
    ReviewRequired, Purging, Partial, Purged, Blocked, Failed, Canceled, Unknown,
}

public sealed record FactoryPurgeResource(
    string AuthorityKey, string ResourceId, string ResourceType, string ApiVersion,
    Guid FactoryId, Guid CreationOperationId, Guid ClientRequestId, DateTimeOffset CreatedAt,
    string Incarnation, string? ParentResourceId, bool AuditBacking);

public sealed record FactoryPurgePlan(
    int Protocol, string Kind, Guid OperationId, Guid FactoryId, string ProductKey,
    string AuthorityRevision, string OwnerAuthorityRevision, string ProductAuthorityRevision,
    string AuditRevision, IReadOnlyList<string> PreservedAssets,
    IReadOnlyList<FactoryPurgeResource> ProposedDeletions, IReadOnlyList<string> BlockedDeletions,
    IReadOnlyList<string> ResidualEffects, string Fingerprint);

public sealed record FactoryPurgeRequest(
    int Protocol, string Kind, Guid OperationId, Guid FactoryId, string PlanFingerprint,
    string AuthorityRevision, string OwnerAuthorityRevision, string ProductAuthorityRevision,
    string AuditRevision, string AuditDisposition,
    string ApprovedBy, DateTimeOffset RequestedAt);

public sealed record FactoryPurgeDeleteEffect(
    int Protocol, Guid FactoryId, Guid OperationId, int Index, FactoryPurgeResource Target,
    AuditDeleteState State, DateTimeOffset UpdatedAt, string? Detail = null)
{
    public bool Retryable { get; init; }
}

public sealed record FactoryPurgeBoundary(
    int Protocol, Guid FactoryId, Guid OperationId, string PlanFingerprint,
    string AuthorityRevision, string OwnerAuthorityRevision, string ProductAuthorityRevision,
    string AuditRevision, string AuditDisposition,
    DateTimeOffset VerifiedAt);

public sealed record FactoryPurgePointer(
    Guid OperationId, FactoryPurgeState State, string PlanFingerprint, string AuthorityRevision,
    string AuditRevision, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt = null);

public sealed record FactoryRetirementPointer(
    int Protocol, string Kind, Guid FactoryId, Guid PurgeOperationId, string ProductKey,
    string PlanFingerprint, string AuditDisposition, string AuditRevision,
    string ApprovedBy, DateTimeOffset RetiredAt);

public sealed record FactoryPurgeResult(FactoryPurgeState State, string Detail)
{
    public FactoryPurgePlan? Plan { get; init; }
    public FactoryPurgeRequest? Request { get; init; }
    public bool IdentityRetired { get; init; }
    public bool InfrastructurePurged => State == FactoryPurgeState.Purged;
    public IReadOnlyList<string> Deleted { get; init; } = [];
    public IReadOnlyList<string> Preserved { get; init; } = [];
    public IReadOnlyList<string> Blocked { get; init; } = [];
    public IReadOnlyList<string> Residual { get; init; } = [];
    public string NextAction => State switch
    {
        FactoryPurgeState.ReviewRequired => "Verify current owned resources, cascade impact and audit disposition; approve only this purge fingerprint.",
        FactoryPurgeState.Purged => "Retain the owner retirement pointer permanently; onboard only with a new factory identity.",
        _ => "Inspect the original purge operation, current provider objects and unresolved claims/effects; never force or reattach the retired identity.",
    };
}

public static class FactoryPurge
{
    public const string Kind = "decide-purge-request";
    public const string PlanKind = "decide-purge-plan";
    public const string RetirementKind = "decide-factory-retirement";
    public const string Key = "dsf/factory/purge";
    public const string HistoryPrefix = FactoryAudit.Namespace + "purge/";
    public static string HistoryKey(Guid operation) => HistoryPrefix + operation.ToString("N") + "/request";
    public static string PlanKey(Guid operation) => HistoryPrefix + operation.ToString("N") + "/plan";
    public static string EffectKey(Guid operation, int index) =>
        HistoryPrefix + operation.ToString("N") + "/effects/" + index.ToString("D4");
    public static string BoundaryKey(Guid operation) =>
        HistoryPrefix + operation.ToString("N") + "/boundary";
    public static string RetirementKey(Guid factory) =>
        HistoryPrefix + "retired/" + factory.ToString("N") + "/pointer";

    public static string Fingerprint(FactoryPurgePlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan with { Fingerprint = "" }, FactoryGitHubJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
