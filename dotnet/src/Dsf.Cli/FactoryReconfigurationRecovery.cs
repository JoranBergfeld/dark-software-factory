using System.Text;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryReconfigurationTransition
{
    private async Task<FactoryReconfigurationOperation> ResumeInputArchiveAsync(FactoryReservationResult admission,
        OwnerReservationStore store, IReadOnlyDictionary<string, ConfigurationSetting> records, ConfigurationSetting root,
        FactoryReconfigurationOperation operation, FactoryReconfigurationInputs? supplied, Action beforeDispatch, CancellationToken token)
    {
        var complete = ReadInputs(records, operation, out var retained);
        var input = supplied is null ? retained : supplied with { Boundary = BoundarySelectionInput.Normalize(supplied.Boundary) };
        if (input is null)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Incomplete input archive requires the exact original --input as repair data, never new authority.");
        var bytes = Encoding.UTF8.GetBytes(Serialize(input));
        if (bytes.Length != operation.InputBytes || FactoryReservationRecords.Hash(bytes) != operation.InputSha256)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Repair input differs from the original admitted bytes; no mutation or replacement.");
        var review = await new FactoryReconfigurationService(reviewer, azure, http, clock)
            .ReviewAsync(admission, input, operation.ApprovalFingerprint, token, operation.OperationId);
        if (review.State != FactoryReconfigurationState.Reviewed || review.Fingerprint != operation.ApprovalFingerprint)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Current governing inputs, authority or evidence differ from the original approval; no forced replay.");
        if (complete) return operation;
        var request = operation with { CoordinatorId = operation.OperationId, ConfirmedChunks = 0, Phase = FactoryReconfigurationPhase.Quiescing };
        operation = operation with { CoordinatorId = Guid.NewGuid() };
        beforeDispatch();
        root = await store.WriteAsync(root.Key, Serialize(operation), root.ETag, token);
        var archive = Enumerable.Range(0, operation.InputChunks).Select(index =>
            (Key: FactoryReconfigurationOperationRecords.InputKey(operation.OperationId, index),
             Value: Serialize(new FactoryReconfigurationInputChunk(1, operation.OperationId, index,
                 Convert.ToBase64String(bytes.AsSpan(index * FactoryReconfigurationOperationRecords.ChunkBytes,
                     Math.Min(FactoryReconfigurationOperationRecords.ChunkBytes,
                         bytes.Length - index * FactoryReconfigurationOperationRecords.ChunkBytes)))))))
            .Prepend((Key: FactoryReconfigurationOperationRecords.RequestKey(operation.OperationId), Value: Serialize(request)));
        foreach (var record in archive)
        {
            var current = await store.ReadAsync(root.Key, token);
            if (current?.ETag != root.ETag || current.Value != root.Value)
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Original recovery coordinator changed; no further dispatch.");
            if (records.TryGetValue(record.Key, out var existing))
            {
                if (existing.Value != record.Value)
                    throw new ReservationStoreException(FactoryReservationState.Unknown, "Original immutable input archive differs; no overwrite.");
                continue;
            }
            await store.WriteAsync(record.Key, record.Value, null, token);
        }
        operation = operation with { CoordinatorId = Guid.NewGuid(), ConfirmedChunks = operation.InputChunks };
        root = await store.WriteAsync(root.Key, Serialize(operation), root.ETag, token);
        var final = await store.ReadNamespaceAsync(FactoryReconfigurationOperationRecords.Prefix, token,
            FactoryReconfigurationOperationRecords.MaximumRecords);
        if (!final.TryGetValue(root.Key, out var confirmed) || confirmed.ETag != root.ETag || confirmed.Value != root.Value
            || !ReadInputs(final, operation, out _))
            throw new ReservationStoreException(FactoryReservationState.Unknown, "Original archive completion unconfirmed; no next-stage authority.");
        return operation;
    }
}
