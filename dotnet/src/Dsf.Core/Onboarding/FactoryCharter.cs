namespace Dsf.Core.Onboarding;

public static class FactoryCharterLimits
{
    public const string AuditPrefix = "dsf/factory/charter/";
    public const int MaximumPayloadRecords = 512;
    public const int MaximumRevisions = 8;
    public const int MaximumAuditRecords = MaximumRevisions * (2 * MaximumPayloadRecords + 3) + 4;
    public const int RecordBytes = 10 * 1024;
    public const string RecordContentType = "application/vnd.dsf.reservation+json;version=1";
}

public enum FactoryCharterState { Reviewed, Published, Adopted, Partial, Blocked, Failed, Canceled, Unknown }

public sealed record CharterSource(SourceSnapshot Snapshot, string? Content, string? Blob)
{
    [System.Text.Json.Serialization.JsonRequired]
    public string? AnalyzedCharterBlob { get; init; }
    [System.Text.Json.Serialization.JsonRequired]
    public string Reconciliation { get; init; } = "";
}
public sealed record CharterEvidence(IntentResult Analysis, CharterSource Source);
public sealed record CharterReview(Guid FactoryId, long RepositoryId, string Repository, string DefaultBranch,
    string Commit, string RootTree, string Path, string? Before, string After, string? BeforeBlob, string AfterBlob,
    Guid AnalysisRevision, string AnalysisChain, string AnalysisSource, string SourceIntegrity, string ModelRevision,
    string BoundaryFingerprint, long OperatorId, bool Existing, string EvidenceFingerprint, string RepositoryNodeId)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => FactoryModelsValidation.Hash(this);
}
public sealed record FactoryCharterResult(FactoryCharterState State, string Detail, CharterReview? Review = null)
{
    public string? Revision { get; init; }
    public CharterEvidence? Evidence { get; init; }
    public CharterPublication? Publication { get; init; }
    public CharterAdoption? Adoption { get; init; }
    public CharterAdoption? HistoricalAdoption { get; init; }
    public CharterMergeReceipt? Merge { get; init; }
    public bool Activated => false;
    public string BlockedOperation { get; init; } = "Activation";
    public string NextActor => "Operator / human repository reviewer";
    public string NextAction { get; init; } = "Review exact private evidence and charter; activation is separate.";
}

public enum CharterEffectState { Intended, Dispatched, Confirmed, KnownFailed, Unknown }
public sealed record CharterNativeReceipt(string Method, string Route, int Status, string RequestId, string ResponseSha256);
public sealed record CharterPullReceipt(long? Id, string NodeId, int Number, string Url, long RepositoryId,
    string Base, string BaseCommit, string Head, string HeadCommit);
public sealed record CharterEffect(int Index, string Kind, CharterEffectState State, string RequestSha256,
    CharterNativeReceipt? Receipt = null, string? ObjectId = null);
public sealed record CharterPublication(int Protocol, Guid Operation, Guid FactoryId, int Sequence,
    string ReviewFingerprint, int PayloadRecords, string PayloadSha256, string Branch, DateTimeOffset ApprovedAt,
    long OperatorId, FactoryCharterState State, IReadOnlyList<CharterEffect> Effects, CharterPullReceipt? PullRequest)
{
    public Guid? PreviousOperation { get; init; }
}
public sealed record CharterPublicationPayload(CharterReview Review, CharterEvidence Evidence);
public sealed record CharterMergeReceipt(long PullRequestId, string PullRequestNodeId, int Number, string Commit,
    long HumanId, DateTimeOffset MergedAt, CharterNativeReceipt Native);
public sealed record CharterAdoption(int Protocol, Guid Revision, Guid FactoryId, int Sequence, string Route, string ReviewFingerprint,
    string CharterBlob, string CharterSha256, string SourceCommit, string SourceTree, Guid AnalysisRevision,
    string AnalysisChain, string AnalysisSource, string SourceIntegrity, string ModelRevision, long OperatorId,
    DateTimeOffset AdoptedAt, Guid? PublicationOperation, CharterMergeReceipt? Merge, int PayloadRecords, string PayloadSha256);
public sealed record CharterAdoptionOperation(CharterAdoption Adoption, string? PreviousETag, CharterEffectState State);
