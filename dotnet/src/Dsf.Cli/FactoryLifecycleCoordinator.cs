using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed record FactoryLifecycleLease(
    int Protocol, string Kind, Guid FactoryId, string Transition, Guid OperationId,
    string State, string ApprovedBy, DateTimeOffset StartedAt);

internal static class FactoryLifecycleCoordinator
{
    internal const string Key = "dsf/factory/lifecycle";
    private const string Kind = "decide-lifecycle-coordinator";
    internal const string Reconfiguration = "reconfiguration";
    internal const string Offboarding = "offboarding";
    internal const string Pause = "pause";
    internal const string Active = "active";
    internal const string Completed = "completed";
    internal const string Detached = "detached";

    internal static async Task<ConfigurationSetting> AcquireAsync(
        OwnerReservationStore store, FactoryReservationIdentity identity,
        string transition, Guid operationId, TimeProvider clock, CancellationToken token)
    {
        if (transition == Reconfiguration)
            await store.RequireAdmissionOpenAsync(token);
        else if (transition is Offboarding or Pause)
            await RequireNoActiveReconfigurationAsync(store, identity, clock, token);
        else
            throw new JsonException();

        var desired = new FactoryLifecycleLease(1, Kind, identity.FactoryId, transition,
            operationId, Active, identity.AzureActorId, clock.GetUtcNow());
        var value = FactoryReservationRecords.Serialize(desired);
        var current = await store.ReadAsync(Key, token);
        if (current is null)
        {
            try
            {
                return await store.WriteAsync(Key, value, null, token);
            }
            catch (ReservationStoreException e) when (e.State == FactoryReservationState.Blocked)
            {
                current = await store.ReadAsync(Key, token);
                if (current is null) throw;
            }
        }

        var retained = Read(current.Value, identity, clock.GetUtcNow());
        if (retained.Transition == transition && retained.OperationId == operationId)
            return current;
        if (retained.Transition == Reconfiguration && retained.State == Completed
            && transition is Offboarding or Pause)
            return await store.WriteAsync(Key, value, current.ETag, token);
        throw new ReservationStoreException(FactoryReservationState.Blocked,
            $"Lifecycle admission is held by retained {retained.Transition}; no concurrent {transition} transition.");
    }

    internal static async Task RequireAsync(
        OwnerReservationStore store, FactoryReservationIdentity identity,
        string transition, Guid operationId, TimeProvider clock, CancellationToken token)
    {
        if (transition == Reconfiguration)
            await store.RequireAdmissionOpenAsync(token);
        var current = await store.ReadAsync(Key, token)
            ?? throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Lifecycle coordinator missing; no transition dispatch.");
        var retained = Read(current.Value, identity, clock.GetUtcNow());
        if (retained.Transition != transition || retained.OperationId != operationId || retained.State != Active)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Lifecycle coordinator changed or closed; no transition dispatch.");
    }

    internal static async Task RequireOwnedIfPresentAsync(
        OwnerReservationStore store, FactoryReservationIdentity identity,
        string transition, Guid operationId, TimeProvider clock, CancellationToken token)
    {
        var current = await store.ReadAsync(Key, token);
        if (current is null) return;
        var retained = Read(current.Value, identity, clock.GetUtcNow());
        if (retained.Transition != transition || retained.OperationId != operationId
            || retained.State is not (Active or Completed))
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Lifecycle coordinator is owned by another or terminal transition; no resume.");
    }

    internal static async Task CompleteAsync(
        OwnerReservationStore store, FactoryReservationIdentity identity,
        string transition, Guid operationId, string state, TimeProvider clock, CancellationToken token)
    {
        if (transition == Reconfiguration)
            await store.RequireAdmissionOpenAsync(token);
        var current = await store.ReadAsync(Key, token)
            ?? throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Lifecycle coordinator missing after terminal transition.");
        var retained = Read(current.Value, identity, clock.GetUtcNow());
        if (retained.Transition != transition || retained.OperationId != operationId)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Lifecycle coordinator changed before terminal transition.");
        if (retained.State == state) return;
        if (retained.State != Active
            || transition == Reconfiguration && state != Completed
            || transition == Offboarding && state != Detached)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Lifecycle coordinator cannot enter the requested terminal state.");
        await store.WriteAsync(Key, FactoryReservationRecords.Serialize(retained with { State = state }),
            current.ETag, token);
    }

    internal static FactoryLifecycleLease Read(
        string value, FactoryReservationIdentity identity, DateTimeOffset now)
    {
        var lease = FactoryReservationRecords.Parse<FactoryLifecycleLease>(value);
        if (lease.Protocol != 1 || lease.Kind != Kind || lease.FactoryId != identity.FactoryId
            || lease.OperationId == Guid.Empty || lease.ApprovedBy != identity.AzureActorId
            || lease.StartedAt.Offset != TimeSpan.Zero || lease.StartedAt < identity.CreatedAt
            || lease.StartedAt > now || lease.Transition is not (Reconfiguration or Offboarding or Pause)
            || lease.State is not (Active or Completed or Detached)
            || lease.Transition != Reconfiguration && lease.State == Completed
            || lease.Transition != Offboarding && lease.State == Detached)
            throw new JsonException("Protected lifecycle coordinator binding is invalid.");
        return lease;
    }

    private static async Task RequireNoActiveReconfigurationAsync(
        OwnerReservationStore store, FactoryReservationIdentity identity,
        TimeProvider clock, CancellationToken token)
    {
        var root = await store.ReadAsync(FactoryReconfigurationOperationRecords.RootKey, token);
        if (root is null) return;
        var operation = FactoryReconfigurationOperationRecords.Read(root.Value, identity, clock.GetUtcNow());
        if (operation.Phase != FactoryReconfigurationPhase.Completed)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Non-completed reconfiguration remains a quiescence blocker; no competing lifecycle admission.");
    }
}
