using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    private async Task RequireCurrentReviewedActivationAsync(FactoryReservationResult admission, OwnerReservationStore store,
        FactoryActivationReview review, bool allowPromotion, CancellationToken token)
    {
        if (allowPromotion && (await store.ReadNamespaceAsync(FactoryPromotion.Prefix, token)).Count != 0)
        {
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
