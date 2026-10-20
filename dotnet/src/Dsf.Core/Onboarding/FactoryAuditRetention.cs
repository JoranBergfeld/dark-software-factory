namespace Dsf.Core.Onboarding;

public enum AnalysisRetentionKind { Protected, Expiring, Expired }
public enum AuditDeleteState { Intended, Dispatched, Confirmed, KnownNoEffect, Unknown }
public sealed record AuditRetentionApproval(int Protocol, Guid FactoryId, string Policy, int Days, string ApprovedBy, DateTimeOffset ApprovedAt);
public sealed record AnalysisRetentionClaim(int Protocol, Guid FactoryId, Guid Revision, AnalysisRetentionKind Kind,
    Guid Operation, string ApprovedBy, DateTimeOffset ApprovedAt, string ContextRevision, string ChainSha256, int Records);
public sealed record AuditDeleteEffect(Guid Operation, int Index, AuditRecordIdentity Target, AuditDeleteState State);
public sealed record AnalysisEvidenceClaim(int Protocol, Guid FactoryId, Guid Revision, string ChainSha256,
    AnalysisRetentionKind Kind, Guid Operation, string ContextRevision, int Records);
public sealed record PreviewAnalysisRetentionRequest(PreviewCapabilityRequest Preview, Guid Revision,
    string ChainSha256, string SourceSnapshotId, string ModelRevision, Guid AdoptionRevision);
public sealed record PreviewAnalysisRetentionReceipt(Guid RequestId, Guid FactoryId, Guid Revision,
    string ChainSha256, string NativeRevision);

public static class FactoryAuditRetention
{
    public const string PolicyKey = FactoryAudit.Namespace + "policy";
    public const string ClaimsPrefix = FactoryAudit.Namespace + "analysis/";
    public static string ClaimKey(Guid revision) => ClaimsPrefix + revision.ToString("N");
    public static string EffectKey(Guid revision, int index) => ClaimKey(revision) + "/effects/" + index.ToString("D4");
    public static string EvidenceKey(Guid revision) => $"intent/{revision:N}/retention";
    public const string EvidenceContentType = "application/vnd.dsf.intent+json;version=1";
    public const int MaximumRecords = FactoryIntentLimits.MaximumRevisions * (2 * FactoryIntentLimits.MaximumChunks + 1) + 1;

    public static void RequireEvidence(AnalysisEvidenceClaim claim, Guid factory, Guid revision, string chain, int records)
    {
        if (claim.Protocol != 1 || claim.FactoryId != factory || claim.Revision != revision || revision == Guid.Empty
            || claim.ChainSha256 != chain || claim.Records != records || records is < 1 or > 2 * FactoryIntentLimits.MaximumChunks
            || claim.Operation == Guid.Empty || claim.ContextRevision.Length != 64
            || claim.Kind is not (AnalysisRetentionKind.Protected or AnalysisRetentionKind.Expiring))
            throw new System.Text.Json.JsonException("Original analysis retention interlock differs.");
    }
}
