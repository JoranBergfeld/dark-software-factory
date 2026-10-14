using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal static class FactoryReconfigurationGate
{
    internal static async Task RequireAsync(OwnerReservationStore store, FactoryReservationIdentity identity,
        TimeProvider clock, CancellationToken token, params FactoryReconfigurationPhase[] allowed)
    {
        var records = await store.ReadNamespaceAsync(FactoryReconfigurationOperationRecords.Prefix, token,
            FactoryReconfigurationOperationRecords.MaximumRecords);
        if (records.Count == 0) return;
        if (!records.TryGetValue(FactoryReconfigurationOperationRecords.RootKey, out var root))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown,
                "ReconfigurationCoordinatorMissing: admission remains closed.");
        var operation = FactoryReconfigurationOperationRecords.Read(root.Value, identity, clock.GetUtcNow());
        if (!allowed.Contains(operation.Phase))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "Reconfiguration phase " + operation.Phase + " does not authorize this effect.");
    }
}
