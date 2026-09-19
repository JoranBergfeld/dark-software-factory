using System.Text.Json;

namespace Dsf.Core.Onboarding;

public sealed record FactoryPromotionApproval(int Protocol, Guid FactoryId, Guid OperationId, string ApprovedBy,
    DateTimeOffset ApprovedAt, string ReviewFingerprint, string AuthorityRevision, string LiveFingerprint,
    string PublisherFingerprint, IReadOnlyList<AuditRecordIdentity> References);
public sealed record FactoryPublicationDecisionScope(Guid InvocationId, AuditRecordIdentity Promotion, AuditRecordIdentity Publisher);
public sealed record FactoryPublicationRequest(Guid InvocationId, string CandidateId);
public sealed record FactoryPublicationAdmission(FactoryPublicationIntent Intent, AuditRecordIdentity Promotion,
    AuditRecordIdentity Publisher, AuditRecordIdentity Evaluation, AuditRecordIdentity Run, string AuthorityRevision);
public sealed record FactoryPublisherObservation(int Protocol, Guid FactoryId, string CapabilityFingerprint,
    string AuthorityRevision, GitHubAuthorityProof GitHub, string MutationReviewSha256)
{
    public bool IsActivated => false;
    public bool PublicationPermitted => false;
}

public static class FactoryPromotion
{
    public const string Prefix = FactoryAudit.Namespace + "promotion/";
    public const string RootKey = Prefix + "current";
    public const string DecisionKind = "council-live-decision-v1";
    public static string DecisionId(Guid factory, Guid invocation) => "decision-" + FactoryModelsValidation.Hash(new { FactoryId = factory, InvocationId = invocation });
    public static string Fingerprint(FactoryPromotionApproval approval) => FactoryModelsValidation.Hash(new
    {
        approval.Protocol, approval.FactoryId, approval.OperationId, approval.ApprovedBy, approval.AuthorityRevision,
        approval.LiveFingerprint, approval.PublisherFingerprint, approval.References
    });
    public static FactoryPromotionApproval RequireApproved(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, AuditRecordIdentity> identities, FactoryPublisherPlan plan, DateTimeOffset now)
    {
        if (!values.TryGetValue(RootKey, out var value))
            throw values.Keys.Any(k => k.StartsWith(Prefix, StringComparison.Ordinal))
                ? new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalFinalPromotionMissingNoRecreation")
                : new PreviewCapabilityException(FactoryCharterState.Blocked, "ExplicitCurrentFinalPromotionRequired");
        var approval = JsonSerializer.Deserialize<FactoryPromotionApproval>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var publisher = JsonSerializer.Deserialize<FactoryPublisherCoordinator>(values[FactoryPublisher.RootKey], FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var factory = plan.Deployment.Manifest.Identity;
        if (publisher.NativeProviderProof is not { } proof || !identities.TryGetValue(proof.Key, out var originalProof) || originalProof != proof
            || approval.Protocol != 1 || approval.FactoryId != factory.FactoryId || approval.OperationId == Guid.Empty
            || approval.ApprovedBy != factory.AzureActorId || approval.ApprovedAt < publisher.ApprovedAt || approval.ApprovedAt > now
            || approval.ApprovedAt.Offset != TimeSpan.Zero || approval.ReviewFingerprint != Fingerprint(approval)
            || approval.AuthorityRevision != plan.Activation.AuthorityRevision
            || approval.PublisherFingerprint != plan.Fingerprint || !FactorySourceValidation.Hash(approval.LiveFingerprint)
            || approval.References.Count is < 6 or > 128 || approval.References.Select(r => r.Key).Distinct().Count() != approval.References.Count
            || approval.References.Any(r => r.Store != AuditStoreKind.Governance || r.FactoryId != factory.FactoryId
                || !identities.TryGetValue(r.Key, out var original) || original != r)
            || new[] { plan.Approval.Key, plan.Live.Key, plan.GitHub.Key, FactoryPublisher.RootKey, FactoryPublisher.ObservationKey(plan.Selection.OperationId),
                Dsf.Core.Runtime.ProductConfigurationKeys.FactoryManifest }.Any(k => !approval.References.Any(r => r.Key == k)))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ExactCurrentFinalPromotionReferencesRequired");
        var live = FactoryLive.ReadConfirmed(values.Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(), now);
        if (approval.LiveFingerprint != live.Plan.Fingerprint) throw new JsonException("Original promoted live rollout changed.");
        return approval;
    }
    public static void Validate(FactoryPublicationRequest request)
    {
        if (request.InvocationId == Guid.Empty || string.IsNullOrWhiteSpace(request.CandidateId) || request.CandidateId.Length > 512
            || request.CandidateId.Any(char.IsControl)) throw new JsonException("Exact original native decision and candidate identifiers required.");
    }
}
