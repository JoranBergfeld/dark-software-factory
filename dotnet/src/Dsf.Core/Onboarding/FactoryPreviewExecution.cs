using System.Text.Json;

namespace Dsf.Core.Onboarding;

/// <summary>Operator-retained native initialization receipt; runtime cannot replace missing seeded state.</summary>
public sealed record PreviewExecutionSeed(string Id, string ResourceId, string ETag, string Checksum);

public sealed record PreviewExecutionPolicy(string Revision, int RequestSeconds, int MaximumDispatches, int JurorSeconds);
public sealed record PreviewExecutionConfiguration(PreviewExecutionPolicy Policy, PreviewExecutionSeed Seed);
public enum PreviewReadiness { NotReady, ReadyAwaitingActivation, Partial, Blocked, Failed, Unknown, Canceled, Queued, Running, Checking }
public sealed record PreviewHttpObservation(int Protocol, Guid FactoryId, Guid OperationId, Guid RequestId,
    string ApprovalFingerprint, DateTimeOffset ObservedAt, PreviewReadiness Readiness, string? Reason, JsonElement? Evaluation = null);
public sealed record PreviewCandidateProof(string Id, string Disposition, string PayloadRevision);
public sealed record PreviewReadinessProof(
    int Protocol, Guid FactoryId, Guid OperationId, string PreviewId, string CapabilityRevision,
    string InputRevision, string FinalInputRevision, string EvaluationRevision, string JudgmentRevision,
    string RunChecksum, string RunResourceId, string RunETag, string DispatchRevision,
    DateTimeOffset CompletedAt, IReadOnlyList<PreviewCandidateProof> Candidates);

public static class FactoryPreviewExecution
{
    public static PreviewExecutionPolicy Policy { get; } = new("supervised-conveyor-v2", 5400, 62, 120);
    public static string ConfigurationKey(Guid operation) => "dsf/factory/preview-execution-configuration/" + operation.ToString("D");
    public static string Id(Guid factory, Guid operation) => "preview-" + FactoryModelsValidation.Hash(new { FactoryId = factory, OperationId = operation });
}
