using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public enum FactoryActivationState { Reviewed, ApprovalOnly, Activated, Partial, Blocked, Failed, Canceled, Unknown }
public enum FactoryActivationPhase { Intended, ApprovalOnly, Rejected }
public sealed record FactoryActivationReview(int Protocol, Guid OperationId, Guid FactoryId, long RepositoryId,
    string AuthorityRevision, string ManifestRevision, string ManifestSha256, string ProfilePin,
    Guid AnalysisRevision, string AnalysisChain, string InputRevision, string ProofSha256,
    string ReviewedBy, IReadOnlyList<AuditRecordIdentity> References)
{
    public string Fingerprint => FactoryModelsValidation.Hash(new { Protocol, OperationId, FactoryId, RepositoryId,
        AuthorityRevision, ManifestRevision, ManifestSha256, ProfilePin, AnalysisRevision, AnalysisChain,
        InputRevision, ProofSha256, ReviewedBy, References });
}
public sealed record FactoryActivationApproval(int Protocol, Guid FactoryId, Guid OperationId,
    string ReviewFingerprint, string ApprovedBy, DateTimeOffset ApprovedAt, DateTimeOffset ReadinessObservedAt);
public sealed record FactoryActivationOperation(int Protocol, Guid FactoryId, Guid OperationId,
    string ReviewFingerprint, string RequestedBy, DateTimeOffset RequestedAt, FactoryActivationPhase Phase)
{
    public string? ApprovalSha256 { get; init; }
}
public sealed record FactoryActivationResult(FactoryActivationState State, string Detail)
{
    public FactoryActivationReview? Review { get; init; }
    public FactoryPromotionApproval? PromotionReview { get; init; }
    public FactoryPromotionApproval? Promotion { get; init; }
    public string? ApprovalFingerprint => PromotionReview?.ReviewFingerprint ?? Review?.Fingerprint;
    public FactoryActivationApproval? Approval { get; init; }
    public bool ApprovalRetained => Approval is not null;
    public bool ApprovalCurrent { get; init; }
    public bool CurrentReady { get; init; }
    public JsonElement? Evaluation { get; init; }
    public bool IsActivated { get; init; }
    public bool HistoricallyCompleted { get; init; }
    public bool FirstConfirmedFiling { get; init; }
    public string FirstFilingState { get; init; } = "None";
    public string? PromotionBlockedBy => IsActivated ? null : "Live promotion requires exact explicit final review and current native readiness; prior approval alone is insufficient.";
}
public sealed class FactoryActivationException(FactoryActivationState state, string detail) : Exception(detail)
{
    public FactoryActivationState State { get; } = state;
}

/// <summary>Operator-only approval history. Neither a retained receipt nor a runtime preview can activate a factory.</summary>
public static class FactoryActivation
{
    public const string Prefix = FactoryAudit.Namespace + "activation/";
    public const string RootKey = Prefix + "current";
    public static string ReviewKey(Guid operation) => Prefix + operation.ToString("N") + "/review";
    public static string ApprovalKey(Guid operation) => Prefix + operation.ToString("N") + "/approval";

    public static FactoryActivationReview Review(AuditGraph graph, JsonElement report, Guid operation, string actor)
    {
        var manifestRecord = graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Governance
            && r.Identity.Key == ProductConfigurationKeys.FactoryManifest);
        var manifest = Parse<FactoryManifest>(manifestRecord.Content);
        var previewRecord = graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Preview
            && JsonDocumentOperation(r.Content) == manifest.PreviewOperationId);
        using var document = JsonDocument.Parse(previewRecord.Content);
        var preview = PreviewEvaluationDocument.ReadNative(document.RootElement);
        if (preview.Phase != PreviewExecutionPhase.Evaluated || preview.PendingRun is not null || preview.Owner == Guid.Empty
            || preview.Owner != preview.Admission?.WorkerId
            || preview.Admission?.State != PreviewWorkState.Released || preview.Admission.Readiness != PreviewReadiness.ReadyAwaitingActivation
            || preview.Proof is not { Candidates.Count: > 0 } proof || preview.Inputs is not { } inputs
            || preview.Run is not { } run || preview.FinalInputs is null || preview.Failure is not null
            || preview.Checksum != preview.CalculateChecksum() || inputs.Revision != inputs.CalculateRevision()
            || proof.InputRevision != inputs.Revision || proof.RunChecksum != run.Checksum)
            throw new FactoryActivationException(FactoryActivationState.Blocked, "Complete current durable native preview and exact released worker required.");
        if (inputs.Manifest.Identity != manifest.Identity || FactoryModelsValidation.Hash(inputs.Manifest) != FactoryModelsValidation.Hash(manifest))
            throw new FactoryActivationException(FactoryActivationState.Blocked, "Current manifest differs from original complete preview.");
        if (FactoryModelsValidation.Hash(report.GetProperty("inputs").Deserialize<PreviewInputSnapshot>(FactorySourceValidation.RecordJson))
                != FactoryModelsValidation.Hash(inputs)
            || FactoryModelsValidation.Hash(report.GetProperty("proof").Deserialize<PreviewReadinessProof>(FactorySourceValidation.RecordJson))
                != FactoryModelsValidation.Hash(proof)
            || FactoryModelsValidation.Hash(report.GetProperty("receipt").Deserialize<PreviewRunReceipt>(FactorySourceValidation.RecordJson))
                != FactoryModelsValidation.Hash(run)
            || FactoryModelsValidation.Hash(report.GetProperty("dispatches").Deserialize<PreviewModelDispatch[]>(FactorySourceValidation.RecordJson))
                != FactoryModelsValidation.Hash(preview.Dispatches))
            throw new FactoryActivationException(FactoryActivationState.Blocked, "Observed inputs, proof, receipt or dispatches differ from original native evidence.");
        var progress = inputs.Context.Intent.Analysis.Progress ?? throw new JsonException();
        var references = new List<AuditRecordIdentity> { previewRecord.Identity };
        references.Add(graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Run
            && r.Identity.NativeResourceId == run.ResourceId && r.Identity.NativeRevision == run.ETag).Identity);
        references.Add(Governance(ProductConfigurationKeys.FactoryProfile).Identity);
        references.Add(Governance(ProductConfigurationKeys.CharterAdoptionReceipt(inputs.Context.Adoption.Revision, "receipt")).Identity);
        for (var i = 0; i < manifest.PlanChunks; i++) references.Add(Governance(ProductConfigurationKeys.FactoryPlanChunk(i)).Identity);
        return new(1, operation, graph.FactoryId, manifest.Identity.RepositoryId,
            FactoryModelsValidation.Hash(graph.Records.Where(r => r.Identity.Store == AuditStoreKind.Governance
                && !r.Identity.Key.StartsWith(FactoryAudit.Namespace, StringComparison.Ordinal))
                .OrderBy(r => r.Identity.Key, StringComparer.Ordinal).Select(r => r.Identity).ToArray()),
            manifestRecord.Identity.NativeRevision, FactoryModelsValidation.Hash(manifest), manifest.ProfilePin,
            progress.Revision, progress.ChainSha256!, inputs.Revision, FactoryModelsValidation.Hash(proof), actor,
            references.OrderBy(FactoryAudit.Id, StringComparer.Ordinal).ToArray());

        FactoryAuditRecord Governance(string key) => graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Governance && r.Identity.Key == key);
        static Guid JsonDocumentOperation(string content)
        {
            using var value = JsonDocument.Parse(content);
            return value.RootElement.GetProperty("operationId").GetGuid();
        }
    }

    public static (FactoryActivationOperation Operation, FactoryActivationReview? Review, FactoryActivationApproval? Approval)
        Read(IReadOnlyDictionary<string, string> values, Guid factory, DateTimeOffset now)
    {
        if (!values.TryGetValue(RootKey, out var root))
            throw new JsonException("Activation coordinator missing despite surviving history; no recreation.");
        var operation = Parse<FactoryActivationOperation>(root);
        if (operation.Protocol != 1 || operation.FactoryId != factory || operation.OperationId == Guid.Empty
            || !FactorySourceValidation.Hash(operation.ReviewFingerprint) || !Guid.TryParse(operation.RequestedBy, out var actor)
            || actor == Guid.Empty || operation.RequestedAt == default || operation.RequestedAt > now
            || !Enum.IsDefined(operation.Phase)
            || values.Keys.Any(k => k != RootKey && k != ReviewKey(operation.OperationId) && k != ApprovalKey(operation.OperationId)))
            throw new JsonException("Invalid protected activation operation.");
        var review = values.TryGetValue(ReviewKey(operation.OperationId), out var reviewed) ? Parse<FactoryActivationReview>(reviewed) : null;
        var approval = values.TryGetValue(ApprovalKey(operation.OperationId), out var approved) ? Parse<FactoryActivationApproval>(approved) : null;
        if (review is not null && (review.Protocol != 1 || review.OperationId != operation.OperationId || review.FactoryId != factory
            || review.Fingerprint != operation.ReviewFingerprint || review.ReviewedBy != operation.RequestedBy
            || review.RepositoryId <= 0 || review.AnalysisRevision == Guid.Empty || !FactorySourceValidation.Hash(review.AnalysisChain)
            || !FactorySourceValidation.Hash(review.AuthorityRevision) || !FactorySourceValidation.Hash(review.ManifestSha256)
            || !FactorySourceValidation.Hash(review.InputRevision) || !FactorySourceValidation.Hash(review.ProofSha256)
            || review.References is not { Count: >= 5 and <= 32 } || review.References.Any(r => r is null || r.FactoryId != factory)))
            throw new JsonException("Invalid protected activation review.");
        if (approval is not null && (review is null || approval.Protocol != 1 || approval.FactoryId != factory
            || approval.OperationId != operation.OperationId || approval.ReviewFingerprint != review.Fingerprint
            || approval.ApprovedBy != operation.RequestedBy || approval.ApprovedAt < operation.RequestedAt || approval.ApprovedAt > now
            || approval.ReadinessObservedAt < operation.RequestedAt || approval.ReadinessObservedAt > approval.ApprovedAt))
            throw new JsonException("Invalid protected activation receipt.");
        if (operation.Phase == FactoryActivationPhase.ApprovalOnly
            && (approval is null || operation.ApprovalSha256 != FactoryModelsValidation.Hash(approval)))
            throw new JsonException("Confirmed activation approval lacks exact receipt.");
        return (operation, review, approval);
    }

    public static void RequireEvidence(FactoryActivationReview review, IReadOnlyList<FactoryAuditRecord> targets)
    {
        var previewRecord = targets.Single(r => r.Identity.Store == AuditStoreKind.Preview);
        using var document = JsonDocument.Parse(previewRecord.Content);
        var preview = PreviewEvaluationDocument.ReadNative(document.RootElement);
        var inputs = preview.Inputs ?? throw new JsonException();
        var progress = inputs.Context.Intent.Analysis.Progress ?? throw new JsonException();
        var manifest = inputs.Manifest;
        var run = targets.Single(r => r.Identity.Store == AuditStoreKind.Run);
        if (review.FactoryId != manifest.Identity.FactoryId || review.RepositoryId != manifest.Identity.RepositoryId
            || review.ReviewedBy != manifest.Identity.AzureActorId || review.ManifestSha256 != FactoryModelsValidation.Hash(manifest)
            || review.ProfilePin != manifest.ProfilePin || review.AnalysisRevision != progress.Revision || review.AnalysisChain != progress.ChainSha256
            || review.InputRevision != inputs.Revision || review.ProofSha256 != FactoryModelsValidation.Hash(preview.Proof)
            || preview.Run is not { } receipt || receipt.ResourceId != run.Identity.NativeResourceId || receipt.ETag != run.Identity.NativeRevision
            || FactoryAudit.TextHash(Governance(ProductConfigurationKeys.FactoryProfile).Content) != review.ProfilePin)
            throw new JsonException("Activation approval differs from the original referenced inputs, analysis, proof or native run.");
        _ = Governance(ProductConfigurationKeys.CharterAdoptionReceipt(inputs.Context.Adoption.Revision, "receipt"));
        for (var i = 0; i < manifest.PlanChunks; i++) _ = Governance(ProductConfigurationKeys.FactoryPlanChunk(i));
        if (targets.Count != manifest.PlanChunks + 4 || targets.Select(r => r.Identity).Distinct().Count() != targets.Count)
            throw new JsonException("Activation review reference set is incomplete or inconsistent.");

        FactoryAuditRecord Governance(string key) => targets.Single(r => r.Identity.Store == AuditStoreKind.Governance && r.Identity.Key == key);
    }

    private static T Parse<T>(string content) => JsonSerializer.Deserialize<T>(content, FactorySourceValidation.RecordJson) ?? throw new JsonException();
}
