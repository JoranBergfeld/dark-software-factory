using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryReconfigurationTransition
{
    private async Task<(ConfigurationSetting Root, FactoryReconfigurationOperation Operation)> RetireOldGenerationAsync(
        FactoryReservationResult admission, OwnerReservationStore store, ConfigurationSetting root,
        FactoryReconfigurationOperation operation, Func<Task> beforeDispatch, CancellationToken token)
    {
        if (operation.RetiredRecords > 0 && operation.RetiredApplied == operation.RetiredRecords)
            return (root, operation);
        var records = (await store.ReadFactoryAuthorityAsync(token)).Values
            .Where(record => record.Key.StartsWith(ProductConfigurationKeys.FactoryPreview, StringComparison.Ordinal))
            .Concat((await store.ReadNamespaceAsync(FactoryActivation.Prefix, token, 128)).Values)
            .Concat((await store.ReadNamespaceAsync(FactoryLive.Prefix, token, 128)).Values)
            .Concat((await store.ReadNamespaceAsync(FactoryPublisher.Prefix, token, 128)).Values)
            .Concat((await store.ReadNamespaceAsync(FactoryPromotion.Prefix, token, 128)).Values)
            .OrderBy(record => record.Key, StringComparer.Ordinal).ToArray();
        if (operation.RetiredRecords == 0)
        {
            foreach (var (record, index) in records.Select((record, index) => (record, index)))
                await EnsureRetiredAsync(store, operation, index, record, beforeDispatch, token);
            operation = operation with
            {
                RetiredRecords = records.Length,
                CoordinatorId = Guid.NewGuid(),
            };
            await beforeDispatch();
            root = await store.WriteAsync(root.Key, Serialize(operation), root.ETag, token);
        }
        else if (operation.RetiredRecords != records.Length + operation.RetiredApplied)
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Retained generation retirement count differs; no unrecorded deletion.");

        for (var index = operation.RetiredApplied; index < operation.RetiredRecords; index++)
        {
            var retired = await ReadRetiredAsync(store, operation, index, token);
            var current = await store.ReadAsync(retired.Record.Key, token);
            if (current is not null)
            {
                if (current.ETag.ToString() != retired.Record.ETag
                    || FactoryReservationRecords.Hash(current.Value) != retired.Record.Sha256
                    || current.Value != retired.Value)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "ConcurrentGenerationChange: retained preview or activation changed; no deletion.");
                await beforeDispatch();
                await store.DeleteRetiredFactoryAuthorityAsync(current, retired.Record, token);
            }
            operation = operation with { RetiredApplied = index + 1, CoordinatorId = Guid.NewGuid() };
            await beforeDispatch();
            root = await store.WriteAsync(root.Key, Serialize(operation), root.ETag, token);
        }
        return (root, operation);
    }

    private async Task EnsureRetiredAsync(OwnerReservationStore store, FactoryReconfigurationOperation operation,
        int index, ConfigurationSetting record, Func<Task> beforeDispatch, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(record.Value);
        var descriptor = new FactoryReconfigurationRetiredRecord(1, operation.OperationId, index, record.Key,
            record.ETag.ToString(), FactoryReservationRecords.Hash(record.Value), bytes.Length,
            (bytes.Length + SnapshotChunkBytes - 1) / SnapshotChunkBytes);
        var key = FactoryReconfigurationOperationRecords.RetiredKey(operation.OperationId, index);
        var existing = await store.ReadAsync(key, token);
        if (existing is null)
        {
            await beforeDispatch();
            await store.WriteAsync(key, Serialize(descriptor), null, token);
        }
        else if (existing.Value != Serialize(descriptor)) throw new JsonException();
        for (var chunkIndex = 0; chunkIndex < descriptor.Chunks; chunkIndex++)
        {
            var chunk = new FactoryReconfigurationRetiredChunk(1, operation.OperationId, index, chunkIndex,
                Convert.ToBase64String(bytes.AsSpan(chunkIndex * SnapshotChunkBytes,
                    Math.Min(SnapshotChunkBytes, bytes.Length - chunkIndex * SnapshotChunkBytes))));
            var chunkKey = FactoryReconfigurationOperationRecords.RetiredChunkKey(operation.OperationId, index, chunkIndex);
            existing = await store.ReadAsync(chunkKey, token);
            if (existing is null)
            {
                await beforeDispatch();
                await store.WriteAsync(chunkKey, Serialize(chunk), null, token);
            }
            else if (existing.Value != Serialize(chunk)) throw new JsonException();
        }
    }

    private static async Task<(FactoryReconfigurationRetiredRecord Record, string Value)> ReadRetiredAsync(
        OwnerReservationStore store, FactoryReconfigurationOperation operation, int index, CancellationToken token)
    {
        var record = FactoryReservationRecords.Parse<FactoryReconfigurationRetiredRecord>(
            (await store.ReadAsync(FactoryReconfigurationOperationRecords.RetiredKey(operation.OperationId, index), token))?.Value
            ?? throw new JsonException());
        if (record.Protocol != 1 || record.OperationId != operation.OperationId || record.Index != index
            || record.Bytes < 0 || record.Chunks != (record.Bytes + SnapshotChunkBytes - 1) / SnapshotChunkBytes)
            throw new JsonException();
        using var content = new MemoryStream();
        for (var chunkIndex = 0; chunkIndex < record.Chunks; chunkIndex++)
        {
            var chunk = FactoryReservationRecords.Parse<FactoryReconfigurationRetiredChunk>(
                (await store.ReadAsync(FactoryReconfigurationOperationRecords.RetiredChunkKey(
                    operation.OperationId, index, chunkIndex), token))?.Value ?? throw new JsonException());
            var bytes = Convert.FromBase64String(chunk.Data);
            if (chunk.Protocol != 1 || chunk.OperationId != operation.OperationId || chunk.Record != index
                || chunk.Index != chunkIndex || bytes.Length != Math.Min(SnapshotChunkBytes, record.Bytes - chunkIndex * SnapshotChunkBytes))
                throw new JsonException();
            content.Write(bytes);
        }
        var value = Encoding.UTF8.GetString(content.ToArray());
        if (content.Length != record.Bytes || FactoryReservationRecords.Hash(value) != record.Sha256) throw new JsonException();
        return (record, value);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadRetiredGenerationAsync(
        OwnerReservationStore store, FactoryReconfigurationOperation operation, CancellationToken token)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < operation.RetiredRecords; index++)
        {
            var retired = await ReadRetiredAsync(store, operation, index, token);
            values.Add(retired.Record.Key, retired.Value);
        }
        return values;
    }
}
