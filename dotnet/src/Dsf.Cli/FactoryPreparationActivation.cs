using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    private async Task RequireCurrentReviewedActivationAsync(FactoryReservationResult admission, OwnerReservationStore store,
        FactoryActivationReview review, bool allowPromotion, CancellationToken token, bool invocation = false)
    {
        if (allowPromotion && (await store.ReadNamespaceAsync(FactoryPromotion.Prefix, token)).Count != 0)
        {
            if (invocation)
            {
                var records = await store.ReadNamespaceAsync("dsf/factory/", token, FactoryAudit.MaximumRecords, FactoryAudit.MaximumBytes, 1024);
                var values = records.ToDictionary(p => p.Key, p => p.Value.Value);
                var publisher = FactoryPublisher.ReadConfirmed(values.Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal))
                    .ToDictionary(), clock.GetUtcNow());
                var factory = admission.Identity ?? throw new InvalidOperationException();
                _ = FactoryPromotion.RequireApproved(values, records.ToDictionary(p => p.Key,
                    p => new AuditRecordIdentity(factory.FactoryId, AuditStoreKind.Governance, p.Key,
                        p.Value.ETag.ToString(), FactoryAudit.TextHash(p.Value.Value))), publisher.Plan, clock.GetUtcNow());
                if (publisher.Plan.Deployment.Manifest.Identity != factory || publisher.Plan.Activation.Fingerprint != review.Fingerprint)
                    throw Failure("OriginalInvocationPromotionAuthorityChanged");
                // The native invocation route owns current admission/inspection, not global activation quiescence.
                return;
            }
            var current = await new FactoryPromotionService(azure, http ?? throw new InvalidOperationException(), clock)
                .ExecuteAsync(admission, null, null, token);
            if (!current.IsActivated || !current.CurrentReady || !current.ApprovalCurrent
                || current.Promotion?.Phase != FactoryPromotionPhase.Active || current.Approval?.ReviewFingerprint != review.Fingerprint)
                throw Failure("CurrentCompletedPromotionOrReviewedActivationChanged");
            return;
        }
        var approval = await new FactoryActivationService(reviewer, azure, http, clock).ExecuteAsync(admission, null, null, token);
        if (!approval.ApprovalCurrent || approval.Review?.Fingerprint != review.Fingerprint)
            throw Failure("CurrentReviewedActivationOrPauseChanged");
    }
}
