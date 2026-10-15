using System.Text;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryReattachmentFinalizer(
    IAzureCliRunner azure, HttpClient? http)
{
    internal async Task CompleteAsync(
        FactoryReservationIdentity identity, OwnerReservationStore product,
        FactoryActivationApproval? activation, CancellationToken token)
    {
        var stage = "reading protected request";
        try
        {
            var requestRecord = await product.ReadAsync(FactoryReattachment.Key, token);
            var owner = new OwnerReservationStore(identity.OwnerEndpoint, identity.TenantId,
                identity.SubscriptionId, azure, http, identity.AzureActorId);
            var ownerKey = ProductConfigurationKeys.OwnerProductClaim(identity.ProductKey);
            var ownerRecord = await owner.ReadAsync(ownerKey, token)
                ?? throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Owner registration disappeared during reattachment finalization.");
            var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(ownerRecord.Value);
            if (anchor.Identity != identity)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Owner registration identity changed during reattachment finalization.");
            if (requestRecord is null)
            {
                if (anchor.Reattachment is null) return;
                stage = "recovering completed reattachment";
                _ = await RequireFinalizedRequestAsync(product, identity, anchor.Reattachment, token);
                await owner.WriteAsync(ownerKey,
                    FactoryReservationRecords.Serialize(anchor with { Reattachment = null }),
                    ownerRecord.ETag, token);
                return;
            }
            var request = FactoryReattachment.Read(requestRecord.Value, identity);
            if (activation is null || activation.FactoryId != identity.FactoryId
                || activation.ApprovedAt < request.RequestedAt
                || activation.ReadinessObservedAt < request.RequestedAt)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Fresh revision-bound activation after reattachment is required.");
            stage = "archiving restored access";
            var progressKey = FactoryReattachment.FinalizationProgressKey(request.OperationId);
            var progressRecord = await product.ReadAsync(progressKey, token);
            FactoryReattachmentRetirementProgress progress;
            if (progressRecord is null)
            {
                var access = await product.ReadNamespaceAsync(
                    FactoryReattachment.AccessPrefix + request.OperationId.ToString("N") + "/", token,
                    maximumRecords: FactoryAudit.GovernanceHistoryRecords);
                var records = access.Values.OrderBy(record => record.Key, StringComparer.Ordinal)
                    .Append(requestRecord).ToArray();
                for (var index = 0; index < records.Length; index++)
                    await ArchiveAsync(product, request.OperationId, index, records[index], token);
                progress = new(1, request.OperationId, request.PlanFingerprint, records.Length, 0);
                progressRecord = await product.WriteAsync(progressKey,
                    FactoryReservationRecords.Serialize(progress), null, token);
            }
            else
            {
                progress = FactoryReservationRecords.Parse<FactoryReattachmentRetirementProgress>(progressRecord.Value);
                if (progress.Protocol != 1 || progress.OperationId != request.OperationId
                    || progress.PlanFingerprint != request.PlanFingerprint || progress.Records < 1
                    || progress.Applied < 0 || progress.Applied > progress.Records)
                    throw new JsonException();
            }
            stage = "retiring restored access";
            var archivedAccess = new (FactoryReattachmentRetiredRecord Record, string Value)[progress.Records - 1];
            for (var index = 0; index < archivedAccess.Length; index++)
            {
                archivedAccess[index] = await ReadArchivedAsync(product, request.OperationId, index, token);
                if (!archivedAccess[index].Record.Key.StartsWith(
                        FactoryReattachment.AccessPrefix + request.OperationId.ToString("N") + "/",
                        StringComparison.Ordinal))
                    throw new JsonException();
            }
            for (var index = progress.Applied; index < progress.Records - 1; index++)
            {
                var archived = archivedAccess[index];
                var current = await product.ReadAsync(archived.Record.Key, token);
                if (current is not null)
                {
                    RequireSame(archived, current);
                    await product.DeleteReattachmentGenerationAsync(current, token);
                }
                progress = progress with { Applied = index + 1 };
                progressRecord = await product.WriteAsync(progressKey,
                    FactoryReservationRecords.Serialize(progress), progressRecord.ETag, token);
            }
            var archivedRequest = await ReadArchivedAsync(
                product, request.OperationId, progress.Records - 1, token);
            if (archivedRequest.Record.Key != FactoryReattachment.Key
                || archivedRequest.Value != requestRecord.Value)
                throw new JsonException();
            RequireSame(archivedRequest, requestRecord);
            var remaining = await product.ReadNamespaceAsync(
                FactoryReattachment.AccessPrefix + request.OperationId.ToString("N") + "/", token,
                maximumRecords: FactoryAudit.GovernanceHistoryRecords);
            if (remaining.Count != 0)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Restored access set changed during reattachment finalization.");

            stage = "committing reattachment finalization";
            progress = progress with { Applied = progress.Records };
            await product.WriteAsync(progressKey,
                FactoryReservationRecords.Serialize(progress), progressRecord.ETag, token);
            stage = "clearing owner pointer";
            if (anchor.Reattachment is { } pointer)
            {
                if (pointer.OperationId != request.OperationId
                    || pointer.State != FactoryReattachmentState.AwaitingReadiness
                    || pointer.PlanFingerprint != request.PlanFingerprint
                    || pointer.AuthorityRevision != request.AuthorityRevision
                    || pointer.RequestedAt != request.RequestedAt)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Owner reattachment pointer changed; no finalization takeover.");
                await owner.WriteAsync(ownerKey,
                    FactoryReservationRecords.Serialize(anchor with { Reattachment = null }),
                    ownerRecord.ETag, token);
            }
            stage = "retiring reattachment stop";
            await product.DeleteReattachmentGenerationAsync(requestRecord, token);
        }
        catch (ReservationStoreException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException
            or InvalidOperationException or KeyNotFoundException or HttpRequestException or IOException
            or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException)
        {
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Reattachment finalization failed while " + stage + "; exact activation remains unconfirmed.");
        }
    }

    private static async Task<FactoryReattachmentRequest> RequireFinalizedRequestAsync(
        OwnerReservationStore product, FactoryReservationIdentity identity,
        FactoryReattachmentPointer pointer, CancellationToken token)
    {
        var history = await product.ReadAsync(FactoryReattachment.HistoryKey(pointer.OperationId), token)
            ?? throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Completed reattachment request provenance is missing.");
        var request = FactoryReattachment.Read(history.Value, identity);
        if (pointer.OperationId != request.OperationId
            || pointer.State != FactoryReattachmentState.AwaitingReadiness
            || pointer.PlanFingerprint != request.PlanFingerprint
            || pointer.AuthorityRevision != request.AuthorityRevision
            || pointer.RequestedAt != request.RequestedAt)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Completed reattachment pointer differs from retained provenance.");
        var records = await product.ReadNamespaceAsync(
            FactoryReattachment.HistoryPrefix + pointer.OperationId.ToString("N") + "/finalized/",
            token, maximumRecords: FactoryAudit.GovernanceHistoryRecords);
        if (!records.TryGetValue(FactoryReattachment.FinalizationProgressKey(pointer.OperationId), out var progressRecord))
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Completed reattachment finalization progress is missing.");
        var progress = FactoryReservationRecords.Parse<FactoryReattachmentRetirementProgress>(progressRecord.Value);
        if (progress.Protocol != 1 || progress.OperationId != pointer.OperationId
            || progress.PlanFingerprint != pointer.PlanFingerprint
            || progress.Records < 1 || progress.Applied != progress.Records)
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Completed reattachment finalization progress is incomplete.");
        var descriptor = records.Values.Select(record =>
                record.Key != FactoryReattachment.FinalizationProgressKey(pointer.OperationId)
                && record.Key.EndsWith("/current", StringComparison.Ordinal)
                    ? JsonSerializer.Deserialize<FactoryReattachmentRetiredRecord>(
                        record.Value, FactoryGitHubJson.Options) : null)
            .SingleOrDefault(record => record?.Key == FactoryReattachment.Key)
            ?? throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Completed reattachment request archive is missing.");
        if (descriptor.Protocol != 1 || descriptor.OperationId != pointer.OperationId
            || descriptor.Bytes != Encoding.UTF8.GetByteCount(history.Value)
            || descriptor.Sha256 != FactoryReservationRecords.Hash(history.Value)
            || descriptor.Chunks is < 1 or > FactoryIntentLimits.MaximumChunks)
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Completed reattachment request archive binding is invalid.");
        var bytes = new List<byte>(descriptor.Bytes);
        for (var index = 0; index < descriptor.Chunks; index++)
        {
            var key = FactoryReattachment.FinalizedChunkKey(pointer.OperationId, descriptor.Index, index);
            if (!records.TryGetValue(key, out var record))
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Completed reattachment request archive chunk is missing.");
            var chunk = JsonSerializer.Deserialize<FactoryReattachmentRetiredChunk>(
                record.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            if (chunk.Protocol != 1 || chunk.OperationId != pointer.OperationId
                || chunk.Record != descriptor.Index || chunk.Index != index)
                throw new JsonException();
            bytes.AddRange(Convert.FromBase64String(chunk.Data));
        }
        if (bytes.Count != descriptor.Bytes || Encoding.UTF8.GetString(bytes.ToArray()) != history.Value)
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "Completed reattachment request archive content is invalid.");
        return request;
    }

    private static async Task ArchiveAsync(
        OwnerReservationStore product, Guid operation, int index,
        Azure.Data.AppConfiguration.ConfigurationSetting record, CancellationToken token)
    {
        const int chunkBytes = FactoryReconfigurationOperationRecords.ChunkBytes;
        var bytes = Encoding.UTF8.GetBytes(record.Value);
        var descriptor = new FactoryReattachmentRetiredRecord(
            1, operation, index, record.Key, record.ETag.ToString(),
            FactoryReservationRecords.Hash(record.Value), bytes.Length,
            (bytes.Length + chunkBytes - 1) / chunkBytes);
        await EnsureAsync(FactoryReattachment.FinalizedKey(operation, index),
            FactoryReservationRecords.Serialize(descriptor));
        for (var chunk = 0; chunk < descriptor.Chunks; chunk++)
        {
            var value = new FactoryReattachmentRetiredChunk(
                1, operation, index, chunk,
                Convert.ToBase64String(bytes.AsSpan(chunk * chunkBytes,
                    Math.Min(chunkBytes, bytes.Length - chunk * chunkBytes))));
            await EnsureAsync(FactoryReattachment.FinalizedChunkKey(operation, index, chunk),
                FactoryReservationRecords.Serialize(value));
        }
        return;

        async Task EnsureAsync(string key, string value)
        {
            var existing = await product.ReadAsync(key, token);
            if (existing is null) await product.WriteAsync(key, value, null, token);
            else if (existing.Value != value)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Reattachment finalization archive changed; no deletion.");
        }
    }

    private static async Task<(FactoryReattachmentRetiredRecord Record, string Value)> ReadArchivedAsync(
        OwnerReservationStore product, Guid operation, int index, CancellationToken token)
    {
        const int chunkBytes = FactoryReconfigurationOperationRecords.ChunkBytes;
        var descriptor = FactoryReservationRecords.Parse<FactoryReattachmentRetiredRecord>(
            (await product.ReadAsync(FactoryReattachment.FinalizedKey(operation, index), token))?.Value
            ?? throw new JsonException());
        if (descriptor.Protocol != 1 || descriptor.OperationId != operation
            || descriptor.Index != index || descriptor.Bytes < 0
            || descriptor.Chunks != (descriptor.Bytes + chunkBytes - 1) / chunkBytes)
            throw new JsonException();
        using var content = new MemoryStream();
        for (var chunk = 0; chunk < descriptor.Chunks; chunk++)
        {
            var archived = FactoryReservationRecords.Parse<FactoryReattachmentRetiredChunk>(
                (await product.ReadAsync(
                    FactoryReattachment.FinalizedChunkKey(operation, index, chunk), token))?.Value
                ?? throw new JsonException());
            var bytes = Convert.FromBase64String(archived.Data);
            if (archived.Protocol != 1 || archived.OperationId != operation
                || archived.Record != index || archived.Index != chunk
                || bytes.Length != Math.Min(chunkBytes, descriptor.Bytes - chunk * chunkBytes))
                throw new JsonException();
            content.Write(bytes);
        }
        var value = Encoding.UTF8.GetString(content.ToArray());
        if (content.Length != descriptor.Bytes
            || FactoryReservationRecords.Hash(value) != descriptor.Sha256)
            throw new JsonException();
        return (descriptor, value);
    }

    private static void RequireSame(
        (FactoryReattachmentRetiredRecord Record, string Value) archived,
        Azure.Data.AppConfiguration.ConfigurationSetting current)
    {
        if (current.ETag.ToString() != archived.Record.ETag
            || FactoryReservationRecords.Hash(current.Value) != archived.Record.Sha256
            || current.Value != archived.Value)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Concurrent reattachment-access change; no deletion.");
    }
}
