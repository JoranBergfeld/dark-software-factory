using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record PreviewRuntimeConfiguration(
    string RuntimeImage, string AudienceClientId, string MetadataRoleDefinitionId);
public sealed record PreviewWorkloadSelection(string RuntimeImage, string AudienceClientId);
public sealed record PreviewTelemetryPreparationSelection(string RuntimeImage, string AudienceClientId,
    string MetadataRoleDefinitionId, int RowsPerTable)
{
    [JsonIgnore]
    public PreviewRuntimeConfiguration Runtime =>
        new(RuntimeImage, AudienceClientId, MetadataRoleDefinitionId);
}
public sealed record PreviewSupplementalPreparationSelection(string Kind, string RuntimeImage, string AudienceClientId,
    string MetadataRoleDefinitionId)
{
    [JsonIgnore]
    public PreviewRuntimeConfiguration Runtime =>
        new(RuntimeImage, AudienceClientId, MetadataRoleDefinitionId);
}
public sealed record PreviewPreparationSelection(Guid OperationId, string RuntimeImage, string AudienceClientId,
    string MetadataRoleDefinitionId, PreviewWorkloadSelection Models, PreviewTelemetryPreparationSelection Telemetry,
    IReadOnlyList<PreviewSupplementalPreparationSelection> Supplemental, CouncilJudgmentConfiguration Judgment)
{
    [JsonIgnore]
    public PreviewRuntimeConfiguration Runtime =>
        new(RuntimeImage, AudienceClientId, MetadataRoleDefinitionId);
}

public enum PreviewPreparationState { Reviewed, Partial, ConfirmedNotReady, SeedAwaitingApproval, ConfiguredAwaitingEvaluation, Blocked, KnownFailed, Unknown, Canceled }
public enum PreviewPreparationEffectState { Intended, Dispatched, Confirmed, KnownFailed, Unknown }
public enum PreviewPreparationAction { WorkloadDeployment, MetadataAssignment }
public sealed record PreviewPreparationPolicy(string Revision, int RequestSeconds, int MaximumEffects, int PlanBytes,
    int ChunkBytes, int MaximumChunks, int NativeResponseBytes, int CleanupSeconds);
public sealed record PreviewPreparationSource(Guid AdoptionRevision, string AdoptionReceiptRevision, string Charter,
    string SourceSnapshotId, string Commit, string Tree, Guid AnalysisRevision, string AnalysisChain, string SourceIntegrity,
    string ModelRevision, IReadOnlyList<string> Unknowns);
public sealed record PreviewPreparationNative(string Target, string ApiVersion, string Revision, string Incarnation);
public sealed record PreviewPreparationGrant(string Target, string PrincipalId, string RoleDefinitionId, string Scope,
    string RoleRevision)
{
    public IReadOnlyList<string> Actions { get; init; } = [];
}
public sealed record PreviewPreparationStep(int Index, PreviewPreparationAction Action, string Target, string ApiVersion,
    string BodySha256, IReadOnlyList<string> ResourceTargets, PreviewPreparationGrant? Grant);
public sealed record PreviewPreparationPermission(string Scope, IReadOnlyList<string> Actions);
public sealed record PreviewPreparationPlan(int Protocol, PreviewPreparationPolicy Policy, PreviewPreparationSelection Selection,
    FactoryManifest Manifest, FactoryPreviewConfiguration Configuration, PreviewSupplementalConfiguration? SupplementalConfiguration,
    IReadOnlyDictionary<string, FoundationOperation> Bindings, PreviewPreparationSource Source,
    string RepositoryRevision, IReadOnlyDictionary<string, string> RecordRevisions,
    IReadOnlyList<PreviewPreparationNative> Prerequisites, IReadOnlyList<PreviewPreparationStep> Steps,
    string WorkloadTemplateSha256, string WorkloadBody, IReadOnlyList<string> CostsAndPrerequisites)
{
    [JsonIgnore] public string Fingerprint => FactoryModelsValidation.Hash(this);
    public IReadOnlyList<PreviewPreparationPermission> OperatorPermissions { get; init; } = [];
}

public sealed record PreviewPreparationReceipt(string Target, string ApiVersion, string Incarnation, string NativeRevision,
    int StatusCode, string RequestId, string? CorrelationId, string? DeploymentTarget, string? OperationId)
{
    public string? Endpoint { get; init; }
}
public sealed record PreviewPreparationAcknowledgment(int StatusCode, string? RequestId, string? CorrelationId,
    DateTimeOffset ObservedAt, string? BodySha256, string? DeploymentCorrelationId, string? ProvisioningState);
public sealed record PreviewPreparationEffect(int Protocol, Guid FactoryId, Guid OperationId, string PlanFingerprint,
    int Index, string Target, string BodySha256, Guid RequestId, DateTimeOffset IntendedAt,
    PreviewPreparationEffectState State, IReadOnlyList<string> ReceiptHashes, string? Reason)
{
    public Guid? ReusedFromOperationId { get; init; }
    public string? CouncilEndpoint { get; init; }
    public string? DeploymentOutputsSha256 { get; init; }
    public PreviewPreparationAcknowledgment? Acknowledgment { get; init; }
}
public sealed record PreviewPreparationCoordinator(int Protocol, string Kind, Guid FactoryId, Guid OperationId,
    string PlanFingerprint, string PlanSha256, int PlanBytes, int PlanChunks, string ApprovedBy, DateTimeOffset ApprovedAt,
    Guid Owner, PreviewPreparationState State, int ConfirmedEffects, string? Reason);
public sealed record PreviewPreparationResult(PreviewPreparationState State, string Detail,
    PreviewPreparationPlan? Plan = null, PreviewPreparationCoordinator? Preparation = null,
    IReadOnlyList<PreviewPreparationEffect>? Effects = null, IReadOnlyList<PreviewPreparationReceipt>? Receipts = null)
{
    public PreviewPublicationReview? Publication { get; init; }
    public PreviewPublicationJournal? PublicationState { get; init; }
    public string? ApprovalFingerprint => Publication?.ApprovalFingerprint ?? Plan?.Fingerprint;
    public PreviewCapabilityRequest? EvaluationRequest => State == PreviewPreparationState.ConfiguredAwaitingEvaluation && Publication is not null
        ? Publication.SeedRequest with { RequestId = FactoryPreviewPublication.RequestId(Publication.SeedRequest.OperationId, "evaluation") } : null;
    public bool CurrentReady => false;
    public string NextActor => State switch
    {
        PreviewPreparationState.ConfirmedNotReady => "Operator: review the --configure continuation; capability and native execution-seed approvals remain separate.",
        PreviewPreparationState.SeedAwaitingApproval => "Operator: inspect and approve the exact returned native seed and execution configuration fingerprint.",
        PreviewPreparationState.ConfiguredAwaitingEvaluation => "Operator: invoke --evaluate with evaluationRequest operationId and approvalFingerprint; configuration is not readiness or activation.",
        PreviewPreparationState.Reviewed when Publication is not null => "Operator: approve this exact protected publication and native seed intent, or leave the review read-only.",
        PreviewPreparationState.Unknown => "Azure/configuration administrator: reconcile original request IDs and retained keys; no replay, name adoption or reset.",
        _ => "Operator: review exact native preparation and prerequisites; capability publication and execution-seed approval remain separate.",
    };
}

public static class FactoryPreviewPreparation
{
    public const string Namespace = "dsf/factory/preview-preparation";
    public const string RootKey = Namespace + "/current";
    public const string Kind = "native-preview-preparation";
    public static PreviewPreparationPolicy Policy { get; } = new("native-preview-preparation-v1", 3600, 64, 512 * 1024, 4096, 128, 4 * 1024 * 1024, 30);
    public static string ChunkKey(Guid operation, int index) => Namespace + "/" + operation.ToString("D") + "/plan/" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    public static string EffectKey(Guid operation, int index) => Namespace + "/" + operation.ToString("D") + "/effects/" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    public static string ReceiptKey(Guid operation, int effect, int index) => EffectKey(operation, effect) + "/receipts/" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    public static string GrantBody(PreviewPreparationGrant grant) => JsonSerializer.Serialize(new { properties = new
        { principalId = grant.PrincipalId, roleDefinitionId = grant.RoleDefinitionId, principalType = "ServicePrincipal" } });

    public static void Validate(PreviewPreparationSelection selection)
    {
        if (selection.OperationId == Guid.Empty || selection.Models is null || selection.Telemetry is null
            || selection.Supplemental is null || selection.Supplemental.Count > 2 || selection.Supplemental.Any(s => s is null) || selection.Judgment is null)
            throw new JsonException("Complete explicit preview selection required.");
        FactorySourceValidation.ValidateSelection(new(selection.RuntimeImage, selection.AudienceClientId));
        FactorySourceValidation.ValidateSelection(new(selection.Models.RuntimeImage, selection.Models.AudienceClientId));
        FactorySourceValidation.ValidateSelection(new(selection.Telemetry.RuntimeImage, selection.Telemetry.AudienceClientId));
        if (selection.Telemetry.RowsPerTable is < 1 or > 8
            || selection.Supplemental.Select(s => s.Kind).Distinct(StringComparer.Ordinal).Count() != selection.Supplemental.Count)
            throw new JsonException("Bounded explicit telemetry projection and unique selected sources required.");
        foreach (var source in selection.Supplemental)
        {
            _ = FactoryPreviewSupplementalValidation.HostKind(source.Kind);
            FactorySourceValidation.ValidateSelection(new(source.RuntimeImage, source.AudienceClientId));
        }
        FactoryPreviewInputValidation.Validate(new(FactoryPreviewInputValidation.Policy, selection.Judgment));
        foreach (var role in new[] { selection.Runtime.MetadataRoleDefinitionId, selection.Telemetry.Runtime.MetadataRoleDefinitionId }
            .Concat(selection.Supplemental.Select(s => s.Runtime.MetadataRoleDefinitionId)))
        {
            var segments = role?.Split('/') ?? [];
            if (segments.Length != 7 || segments[1] != "subscriptions" || !Guid.TryParseExact(segments[2], "D", out var subscription)
                || subscription == Guid.Empty || segments[3] != "providers" || segments[4] != "microsoft.authorization"
                || segments[5] != "roledefinitions" || !Guid.TryParseExact(segments[6], "D", out var id) || id == Guid.Empty)
                throw new JsonException("Exact subscription custom metadata role definition IDs required.");
        }
    }
}
