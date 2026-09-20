using Dsf.Core.Charters;

namespace Dsf.Core.Onboarding;

public enum IntentState { DraftReady, Partial, Blocked, Failed, Canceled, Unknown }
public enum IntentClaimField { Purpose, Users, Goal, NonGoal, SuccessMeasure }
public enum IntentClaimKind { ImplementedBehavior, InferredPurpose, RecordedIntent }
public enum IntentSupportVerdict { Supports, Neutral, Contradicts, Unsupported }
public enum IntentModelOutcome { Pending, Dispatched, Confirmed, KnownFailure, Unknown }
public sealed record IntentRequest(bool NewAnalysis = false, int Page = 0);
public sealed record IntentEvidence(int PathIndex, int ChunkIndex, string Excerpt, string Explanation);
public sealed record IntentClaim(IntentClaimField Field, IntentClaimKind Kind, string Text, string Authority,
    string Currency, IReadOnlyList<IntentEvidence> Evidence);
public sealed record IntentCandidate(bool Complete, string Findings, IReadOnlyList<IntentClaim> Claims,
    CharterKnowledge NonGoals, CharterKnowledge SuccessMeasures, IReadOnlyList<string> Limitations);
public sealed record IntentClaimCheck(int Claim, IntentSupportVerdict Verdict, string Explanation, string Excerpt);
public sealed record IntentVerification(bool Complete, IReadOnlyList<IntentClaimCheck> Checks, IReadOnlyList<string> Limitations);
public sealed record IntentDispatch(Guid Id, int Index, bool Checking, string InputSha256, ModelCapabilityReference Capability);
public sealed record IntentFailure(IntentDispatch Dispatch, IntentState State, string Reason)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModelCapabilityReason? ModelRejection { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModelInferenceRejection? ModelRejectionProof { get; init; }
}
public sealed record IntentSelection(Guid Revision, SourceSnapshot Snapshot, string SourceIntegritySha256,
    string ModelConfigurationRevision, int AvailableChunks);
public sealed record IntentProgress(int Protocol, Guid Revision, int RevisionNumber, SourceSnapshot Snapshot,
    string SourceIntegritySha256, string ModelConfigurationRevision, int AvailableChunks)
{
    public IntentState State { get; init; } = IntentState.Partial;
    public string Reason { get; init; } = "PendingAnalysis";
    public int AnalyzedChunks { get; init; }
    public int CheckedChunks { get; init; }
    public string? ChainSha256 { get; init; }
    public IntentDispatch? Dispatch { get; init; }
    [System.Text.Json.Serialization.JsonRequired]
    public int ModelAttempts { get; init; }
    [System.Text.Json.Serialization.JsonRequired]
    public IntentModelOutcome ModelOutcome { get; init; } = IntentModelOutcome.Pending;
    public IntentFailure? LastFailure { get; init; }
    public IntentSelection? PendingSelection { get; init; }
}
public sealed record IntentChunkAnalysis(Guid Revision, IntentDispatch Dispatch, SourceChunkReference Source,
    string? PreviousSha256, ModelWorkloadObservation Model, IntentCandidate? Candidate, IntentVerification? Verification,
    IReadOnlyList<SourceChunkReference> Dependencies);
public sealed record IntentOriginalReference(SourceChunkReference Chunk, string Path, string Url, int ByteOffset, int ByteLength);
public sealed record IntentReviewClaim(int Index, IntentClaim Claim, bool Grounded, bool Conflicted,
    IReadOnlyList<IntentOriginalReference> Originals);
public sealed record IntentResult(IntentState State, string Detail, IntentProgress? Progress,
    SourceProgress? Acquisition, GroundedCharterDraft? Draft, IReadOnlyList<IntentReviewClaim> Claims,
    IReadOnlyList<IntentChunkAnalysis> Chunks, IReadOnlyList<string> Limitations, int? NextPage)
{
    public ModelAuthorityResult? ModelAuthority { get; init; }
    public bool Governs { get; init; }
    public string DeployedSourceCorrelation { get; init; } = "Unknown";
    public IReadOnlyList<SourcePathCoverage> ExcludedPaths { get; init; } = [];
    public int? NextExclusionPage { get; init; }
    public string NextAction { get; init; } = State == IntentState.DraftReady ? "Owner reviews this private draft; no adoption or publication has occurred."
        : State == IntentState.Partial ? "Operator inspects coverage and repeats the same infer command."
        : "Operator inspects current authority and durable progress; never force retry an unknown dispatch.";
}

public static class FactoryIntentLimits
{
    public const int Protocol = 1;
    public const int MaximumChunks = 512;
    public const int MaximumClaims = 8;
    public const int MaximumRevisions = 8;
    public const int PageSize = 8;
    public const int MaximumAdditionalAttempts = 16;
    public const int MaximumPage = FactorySourceValidation.MaximumNodes / PageSize;
    public static TimeSpan AdvanceBudget => TimeSpan.FromSeconds(120);
    public static TimeSpan RequestBudget => TimeSpan.FromSeconds(150);
}
