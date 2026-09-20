namespace Dsf.Core.Onboarding;

public enum SourceRefreshState { Current, Pending, Compatible, ReviewRequired, Partial, Blocked, Failed, Canceled, Unknown }
public enum SourceRefreshPhase { Acquisition, Analysis, Compatibility, Complete }
public enum SourceRefreshRecovery { KnownNoModelDispatch, TransientSourceRead, RejectedModel, PreModelSourceRead }
public enum SourceCompatibilityVerdict { Compatible, Irrelevant, Conflict, Unknown }
public sealed record SourceCompatibilityJudgment(string Target, SourceCompatibilityVerdict Verdict, string Reason, string Excerpt);
public sealed record SourceCompatibilityResult(bool Complete, IReadOnlyList<SourceCompatibilityJudgment> Judgments);
public sealed record SourceRefreshBinding(SourceSnapshot Snapshot, Guid AdoptionRevision, string AdoptionETag,
    string AdoptionOperationETag, string CharterSha256, string GovernanceRevision, string ModelRevision, Guid OriginalAnalysis);
public sealed record SourceRefreshObservation(string Protocol, Guid RequestId, SourceRefreshState State, string Reason,
    SourceRefreshBinding Binding, AuditRecordIdentity? Progress);
public sealed record SourceRefreshRequest(PreviewCapabilityRequest Preview, Guid Invocation, Guid Admission, Guid Worker,
    AuditRecordIdentity Evaluation, SourceRefreshBinding Binding)
{
    public bool Continuation { get; init; }
}
public sealed record SourceRefreshProgress(int Protocol, Guid Id, SourceRefreshBinding Binding)
{
    public Guid? PreviousRefresh { get; init; }
    public Guid? LastRequest { get; init; }
    public SourceRefreshState State { get; init; } = SourceRefreshState.Pending;
    public SourceRefreshPhase Phase { get; init; } = SourceRefreshPhase.Acquisition;
    public string Reason { get; init; } = "CompleteCurrentSourceRefreshRequired";
    public SourceRefreshRequest? Work { get; init; }
    public Guid? AnalysisRevision { get; init; }
    public string? AnalysisChain { get; init; }
    public string? SourceIntegrity { get; init; }
    public int Chunks { get; init; }
    public int CheckedChunks { get; init; }
    public string? CompatibilityChain { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? CompatibilityAttempts { get; init; }
    public IReadOnlyList<string> AddressedTargets { get; init; } = [];
    public IntentDispatch? Dispatch { get; init; }
    public IntentFailure? LastFailure { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SourceRefreshRecovery? Recovery { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SourceReadFailure? ReadFailure { get; init; }
    public bool Governs => false;
    public string DeployedSourceCorrelation => "Unknown";
}
public sealed record SourceCompatibilityStep(Guid Refresh, Guid Analysis, IntentDispatch Dispatch,
    SourceChunkReference Source, IReadOnlyList<SourceChunkReference> Dependencies, string? PreviousSha256,
    ModelWorkloadObservation Model, SourceCompatibilityResult Result);
public sealed record SourceRefreshReceipt(Guid RequestId, string RequestSha256, SourceRefreshState State,
    string Reason, AuditRecordIdentity Progress);
public sealed record SourceRefreshDispatch(SourceRefreshRequest Request, PreviewModelDispatchState State,
    SourceRefreshReceipt? Receipt);
public sealed record SourceRefreshQualification(int Protocol, SourceRefreshProgress Progress,
    AuditRecordIdentity Checkpoint, AuditRecordIdentity Receipt);

public static class FactorySourceRefresh
{
    public const string Protocol = "complete-source-refresh-v1";
    public const string Prefix = "intent/refresh/";
    public const string CurrentKey = Prefix + "current";
    public static string InitialKey(Guid id) => Prefix + id.ToString("N") + "/initialized";
    public static string HistoryKey(Guid id) => Prefix + id.ToString("N") + "/completed";
    public static string StepKey(Guid id, int index) => Prefix + id.ToString("N") + "/checks/" + index.ToString("D4");
    public static string ReceiptKey(Guid request) => Prefix + "receipts/" + request.ToString("N");
    public static string CheckpointKey(Guid request) => Prefix + "checkpoints/" + request.ToString("N");
    public const int AdvancesPerWorker = 48;
}
