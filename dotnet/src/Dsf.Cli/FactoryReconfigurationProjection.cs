using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal static class FactoryReconfigurationProjection
{
    internal static async Task<FactoryReservationResult> ApplyAsync(FactoryReservationResult admission,
        RepositoryAttachmentReviewer? reviewer, IAzureCliRunner azure, HttpClient? http,
        TimeProvider clock, CancellationToken token)
    {
        if (admission.State != FactoryReservationState.Reserved || admission.Identity is not { } identity
            || admission.Foundation?.ProductEndpoint is not { } endpoint)
            return admission;
        var product = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http);
        var records = await product.ReadNamespaceAsync(FactoryReconfigurationOperationRecords.Prefix, token,
            FactoryReconfigurationOperationRecords.MaximumRecords);
        if (!records.TryGetValue(FactoryReconfigurationOperationRecords.RootKey, out var root)) return admission;
        var operation = FactoryReconfigurationOperationRecords.Read(root.Value, identity, clock.GetUtcNow());
        if (operation.Phase < FactoryReconfigurationPhase.AwaitingEvidence)
            return admission with { ReconfigurationPhase = operation.Phase };
        var input = FactoryReconfigurationOperationRecords.ReadInputArchive(operation,
            records.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal)).Input;
        var owner = new OwnerReservationStore(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId,
            azure, http);
        var proof = new ReservationAdmissionProof();
        foreach (var record in (await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token)).Values)
            proof.Add(record);
        var plan = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
            .PlanAsync(input.Boundary, token, reservationProof: proof);
        if (plan.State != BoundaryPlanState.Reviewed || plan.RepositoryReview.Repository?.Id != identity.RepositoryId)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Applied reconfiguration boundary no longer has current exact authority.");
        return admission with { ReconfiguredPlan = plan, ReconfigurationPhase = operation.Phase };
    }
}
