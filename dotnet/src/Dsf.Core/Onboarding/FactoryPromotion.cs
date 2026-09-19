using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public enum FactoryPromotionPhase { Reviewed, Pending, Active, Rejected }
public sealed record FactoryPromotionApproval(int Protocol, Guid FactoryId, Guid OperationId, string ApprovedBy,
    DateTimeOffset ApprovedAt, string ReviewFingerprint, string AuthorityRevision, string LiveFingerprint,
    string PublisherFingerprint, IReadOnlyList<AuditRecordIdentity> References)
{
    public AuditRecordIdentity? NativeIndices { get; init; }
    public FactoryPromotionPhase Phase { get; init; } = FactoryPromotionPhase.Reviewed;
}
public sealed record FactoryPromotionIntent(int Protocol, FactoryPromotionApproval Review, DateTimeOffset RequestedAt);
public sealed record FactoryPromotionCompletion(int Protocol, Guid FactoryId, Guid OperationId, AuditRecordIdentity Approval,
    string ReviewFingerprint, string ApprovedBy, DateTimeOffset CompletedAt);
public sealed record FactoryPromotionSeeds(int Protocol, Guid FactoryId, Guid OperationId, string ReviewFingerprint,
    FactoryNativeIndexReceipt Decisions, FactoryNativeIndexReceipt Publications, FactoryNativeIndexReceipt Problems);
public sealed record FactoryNativeIndexReceipt(AuditRecordIdentity Identity, string Json);
public sealed record FactoryPromotionObservation(int Protocol, Guid FactoryId, string AuthorityRevision,
    string LiveFingerprint, string PublisherFingerprint, bool CurrentReady, bool FirstConfirmedFiling);
public sealed record FactoryDecisionCapability(int Protocol, Guid FactoryId, string RolloutFingerprint, string Workflow,
    PreviewExecutionPolicy Policy);
public sealed record FactoryPublicationDecisionScope(Guid InvocationId, AuditRecordIdentity Promotion, AuditRecordIdentity Publisher, bool DryRun = false);
public sealed record FactoryDecisionRequest(Guid InvocationId, bool DryRun = false);
public sealed record FactoryPublicationRequest(Guid InvocationId, string CandidateId)
{
    public FactoryPublicationRetry? Retry { get; init; }
}
public sealed record FactoryPublicationAdmission(FactoryPublicationIntent Intent, AuditRecordIdentity Promotion,
    AuditRecordIdentity Publisher, AuditRecordIdentity Evaluation, AuditRecordIdentity Run, string AuthorityRevision);
public sealed record FactoryPublisherObservation(int Protocol, Guid FactoryId, string CapabilityFingerprint,
    string AuthorityRevision, GitHubAuthorityProof GitHub, string MutationReviewSha256)
{
    public string? RecoveryProtocol { get; init; }
    public bool IsActivated => false;
    public bool PublicationPermitted => false;
}

public static class FactoryPromotion
{
    public const string Prefix = FactoryAudit.Namespace + "promotion/";
    public const string RootKey = Prefix + "current";
    public const string IntentKey = Prefix + "intent";
    public static string SeedsKey(Guid operation) => Prefix + operation.ToString("N") + "/native-indices";
    public static string CompletionKey(Guid operation) => Prefix + operation.ToString("N") + "/completion";
    public const string DecisionKind = "council-live-decision-v1";
    public const string Workflow = "promoted-conveyor-createonce-v1";
    // Export/retention bookkeeping cannot revoke a captured run; every authorizing namespace and stop intent can.
    public static bool IsLiveAuthorityKey(string key) => !key.StartsWith(FactoryAudit.Namespace, StringComparison.Ordinal)
        || new[] { FactoryActivation.Prefix, FactoryLive.Prefix, FactoryPublisher.Prefix, FactoryPause.HistoryPrefix, Prefix }
            .Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
    public static string ProblemScope(Guid factory, long repository) => $"factory:{factory:D}:repository:{repository}";
    public static string ProblemIndex(Guid factory, long repository) => "problem-index-" + FactoryAudit.TextHash(ProblemScope(factory, repository));
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
        if (approval.Phase != FactoryPromotionPhase.Active
            || !values.TryGetValue(CompletionKey(approval.OperationId), out var completed))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalFinalPromotionCloseoutUnconfirmedNoAdmission");
        var completion = JsonSerializer.Deserialize<FactoryPromotionCompletion>(completed, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (completion.Protocol != 1 || completion.FactoryId != approval.FactoryId || completion.OperationId != approval.OperationId
            || completion.Approval != identities[RootKey] || completion.ReviewFingerprint != approval.ReviewFingerprint
            || completion.ApprovedBy != approval.ApprovedBy || completion.CompletedAt < approval.ApprovedAt || completion.CompletedAt > now)
            throw new JsonException("Exact original conditional final-promotion closeout required.");
        RequireReview(approval, values, identities, plan, now);
        var intent = RequireIntended(values, identities, plan, now);
        if (approval.NativeIndices is not { } proof || !identities.TryGetValue(proof.Key, out var originalProof) || proof != originalProof
            || proof.Key != SeedsKey(approval.OperationId) || proof.Store != AuditStoreKind.Governance
            || intent.Review.ReviewFingerprint != approval.ReviewFingerprint || approval.ApprovedAt < intent.RequestedAt)
            throw new JsonException("Original explicit promotion/native index receipts required.");
        var seeds = JsonSerializer.Deserialize<FactoryPromotionSeeds>(values[proof.Key], FactorySourceValidation.RecordJson) ?? throw new JsonException();
        RequireSeeds(seeds, approval, plan.Deployment.Manifest, now);
        return approval;
    }
    public static FactoryPromotionIntent RequireIntended(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, AuditRecordIdentity> identities, FactoryPublisherPlan plan, DateTimeOffset now)
    {
        var intent = JsonSerializer.Deserialize<FactoryPromotionIntent>(values[IntentKey], FactorySourceValidation.RecordJson) ?? throw new JsonException();
        RequireReview(intent.Review, values, identities, plan, now);
        if (intent.Protocol != 1 || intent.RequestedAt == default || intent.RequestedAt > now || intent.RequestedAt.Offset != TimeSpan.Zero
            || intent.Review.NativeIndices is not null || intent.Review.Phase != FactoryPromotionPhase.Reviewed)
            throw new JsonException("Exact original promotion intent required.");
        return intent;
    }
    public static void RequireReview(FactoryPromotionApproval approval, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, AuditRecordIdentity> identities, FactoryPublisherPlan plan, DateTimeOffset now)
    {
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
    }
    public static void RequireSeeds(FactoryPromotionSeeds seeds, FactoryPromotionApproval review, FactoryManifest manifest, DateTimeOffset now)
    {
        if (seeds.Protocol != 1 || seeds.FactoryId != review.FactoryId || seeds.OperationId != review.OperationId
            || seeds.ReviewFingerprint != review.ReviewFingerprint) throw new JsonException("Native indices do not belong to reviewed promotion.");
        foreach (var (created, publication) in new[] { (seeds.Decisions, false), (seeds.Publications, true) })
        {
            var receipt = created.Identity;
            if (receipt.FactoryId != review.FactoryId || receipt.Store != (publication ? AuditStoreKind.Publication : AuditStoreKind.Preview)
                || receipt.Key != FactoryActivityIndex.Key(manifest.Identity.ProductKey, review.FactoryId, publication)
                || string.IsNullOrWhiteSpace(receipt.NativeRevision) || !FactorySourceValidation.Hash(receipt.Sha256))
                throw new JsonException("Exact original native discovery seed required.");
            _ = Dsf.Core.Runtime.NativeCosmosResourceId.RequireDocument(receipt.NativeResourceId);
            using var native = JsonDocument.Parse(created.Json, new() { AllowDuplicateProperties = false });
            var initial = FactoryActivityIndex.Read(created.Json, review.FactoryId, manifest.Identity.ProductKey, review.OperationId,
                publication, now);
            if (receipt.Sha256 != FactoryAudit.TextHash(created.Json) || native.RootElement.GetProperty("_etag").GetString() != receipt.NativeRevision
                || native.RootElement.GetProperty("_rid").GetString() != receipt.NativeResourceId || initial.Entries.Count != 0)
                throw new JsonException("Original empty native index creation response required.");
        }
        var problem = seeds.Problems.Identity;
        using var body = JsonDocument.Parse(seeds.Problems.Json, new() { AllowDuplicateProperties = false });
        var nativeProblem = body.RootElement;
        if (problem.FactoryId != review.FactoryId || problem.Store != AuditStoreKind.Preview
            || problem.Key != $"dbs/{manifest.Identity.ProductKey}/colls/{FactoryPreviewValidation.PreviewContainer}/docs/{ProblemIndex(review.FactoryId, manifest.Identity.RepositoryId)}"
            || problem.Sha256 != FactoryAudit.TextHash(seeds.Problems.Json) || nativeProblem.GetProperty("_etag").GetString() != problem.NativeRevision
            || nativeProblem.GetProperty("_rid").GetString() != NativeCosmosResourceId.RequireDocument(problem.NativeResourceId)
            || nativeProblem.GetProperty("id").GetString() != ProblemIndex(review.FactoryId, manifest.Identity.RepositoryId)
            || nativeProblem.GetProperty("product").GetString() != manifest.Identity.ProductKey
            || nativeProblem.GetProperty("scope").GetString() != ProblemScope(review.FactoryId, manifest.Identity.RepositoryId)
            || nativeProblem.GetProperty("problems").GetArrayLength() != 0)
            throw new JsonException("Original empty native logical-problem index creation response required.");
    }
    public static void Validate(FactoryPublicationRequest request)
    {
        if (request.InvocationId == Guid.Empty || string.IsNullOrWhiteSpace(request.CandidateId) || request.CandidateId.Length > 512
            || request.CandidateId.Any(char.IsControl)) throw new JsonException("Exact original native decision and candidate identifiers required.");
    }
}
