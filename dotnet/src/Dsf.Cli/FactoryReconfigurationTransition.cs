using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryReconfigurationTransition(
    RepositoryAttachmentReviewer? reviewer, IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryReconfigurationReview> ExecuteAsync(FactoryReservationResult admission,
        FactoryReconfigurationInputs? input, Guid operationId, string? approval, bool inspect, bool resume, CancellationToken token)
    {
        FactoryReconfigurationOperation? operation = null;
        var effects = false;
        bool? closed = null;
        try
        {
            if (admission.State != FactoryReservationState.Reserved || admission.Identity is not { } identity
                || admission.Foundation?.ProductEndpoint is not { } endpoint)
                return new(FactoryReconfigurationState.Blocked, "Trusted retained factory required; no transition admitted.");
            var store = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
            var records = await store.ReadNamespaceAsync(FactoryReconfigurationOperationRecords.Prefix, token,
                FactoryReconfigurationOperationRecords.MaximumRecords);
            if (records.Count != 0)
            {
                closed = true;
                if (!records.TryGetValue(FactoryReconfigurationOperationRecords.RootKey, out var root))
                    return Result(FactoryReconfigurationState.Unknown, "Reconfiguration coordinator missing despite retained history; never recreate or resume old configuration.");
                operation = FactoryReconfigurationOperationRecords.Read(root.Value, identity, clock.GetUtcNow());
                if (operation.OperationId != operationId)
                    return Result(FactoryReconfigurationState.Blocked, "A different original reconfiguration remains retained; no replacement or takeover.");
                if (resume)
                {
                    FactoryReconfigurationReview? reviewed = null;
                    if (operation.Phase == FactoryReconfigurationPhase.Quiescing)
                    {
                        operation = await ResumeInputArchiveAsync(admission, store, records, root, operation, input, () => effects = true, token);
                        records = await store.ReadNamespaceAsync(FactoryReconfigurationOperationRecords.Prefix, token,
                            FactoryReconfigurationOperationRecords.MaximumRecords);
                        root = records[FactoryReconfigurationOperationRecords.RootKey];
                        operation = FactoryReconfigurationOperationRecords.Read(root.Value, identity, clock.GetUtcNow());
                        if (!ReadInputs(records, operation, out var retained) || retained is null) throw new JsonException();
                        input = retained;
                        reviewed = await new FactoryReconfigurationService(reviewer, azure, http, clock)
                            .ReviewAsync(admission, input, operation.ApprovalFingerprint, token, operation.OperationId);
                        if (reviewed.State != FactoryReconfigurationState.Reviewed
                            || reviewed.Fingerprint != operation.ApprovalFingerprint)
                            throw new ReservationStoreException(FactoryReservationState.Blocked,
                                "Current governing authority differs from the admitted review; no configuration writes.");
                    }
                    else
                    {
                        if (!ReadInputs(records, operation, out var retained) || retained is null) throw new JsonException();
                        if (input is not null && FactoryModelsValidation.Hash(input) != FactoryModelsValidation.Hash(retained))
                            throw new ReservationStoreException(FactoryReservationState.Blocked,
                                "Resume input differs from retained reviewed input; no replacement.");
                        input = retained;
                    }
                    if (operation.Phase is FactoryReconfigurationPhase.Quiescing or FactoryReconfigurationPhase.Reconciling)
                    {
                        operation = await ApplyConfigurationAsync(admission, store, root, operation, input!, reviewed,
                            () => effects = true, token);
                        return Result(FactoryReconfigurationState.Partial,
                            "Reviewed configuration applied conditionally. Previous evidence and activation are historical; renewed checks and preview required.");
                    }
                    return Result(FactoryReconfigurationState.Partial,
                        "Reviewed configuration remains applied under the original operation; continue only the required renewed evidence and activation stages.");
                }
                if (!inspect)
                    return Result(FactoryReconfigurationState.Partial, "Original transition retained. Inspect the same operation; no repeated application or old-configuration restart.");
                var complete = ReadInputs(records, operation, out _);
                return Result(FactoryReconfigurationState.Partial, complete
                    ? operation.Phase >= FactoryReconfigurationPhase.AwaitingEvidence
                        ? "Exact reviewed configuration is applied and old admission remains closed. Renewed evidence, preview and activation are still required."
                        : "Exact reviewed inputs retained and old admission closed. Original admitted/issued effects still require native reconciliation; no completed-stop or application claim."
                    : "Old admission closed, but original input archive is incomplete. Retain this operation and exact original input; no replacement.");
            }
            if (inspect || resume)
                return Result(FactoryReconfigurationState.Unknown, "No original reconfiguration visible; status cannot invent an operation or reopening authority.");
            if (input is null || approval is null || !FactorySourceValidation.Hash(approval))
                return Result(FactoryReconfigurationState.Blocked, "Exact current review approval and complete desired input required; generic yes is not approval.");
            var review = await new FactoryReconfigurationService(reviewer, azure, http, clock).ReviewAsync(admission, input, approval, token);
            if (review.State != FactoryReconfigurationState.Reviewed || review.Fingerprint != approval)
                return review with { State = FactoryReconfigurationState.Blocked, Detail = "Exact current reviewed change required. " + review.Detail };
            if (!review.RenewedApprovalRequired)
                return review with { State = FactoryReconfigurationState.Blocked,
                    Detail = "No governing change to apply; unchanged configuration or repository metadata does not invalidate activation." };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(review.DesiredInputs, FactorySourceValidation.RecordJson));
            if (bytes.Length is <= 0 or > FactoryReconfigurationOperationRecords.MaximumInputBytes)
                return review with { State = FactoryReconfigurationState.Partial, Detail = "Complete staged input exceeds the native archive budget; no truncation or admission." };
            var manifestRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token) ?? throw new JsonException();
            var manifest = FactoryGovernance.ReadInactiveManifest(manifestRecord.Value,
                (await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token))?.Value, identity.ProductKey, endpoint);
            if (manifest.Identity != identity || !review.Revisions.Any(r => r.Store == AuditStoreKind.Governance.ToString()
                && r.Key == manifestRecord.Key && r.ETag == manifestRecord.ETag.ToString() && r.Sha256 == FactoryAudit.TextHash(manifestRecord.Value)))
                return Result(FactoryReconfigurationState.Blocked, "Reviewed manifest changed before conditional admission; review again.");
            operation = new(1, operationId, identity, approval, review.CurrentRevision!, manifestRecord.ETag.ToString(), manifest,
                FactoryReservationRecords.Hash(bytes), bytes.Length,
                (bytes.Length + FactoryReconfigurationOperationRecords.ChunkBytes - 1) / FactoryReconfigurationOperationRecords.ChunkBytes,
                identity.AzureActorId, clock.GetUtcNow(), FactoryReconfigurationPhase.Quiescing, operationId, 0);
            var initial = Serialize(operation);
            var chunks = Enumerable.Range(0, operation.InputChunks).Select(index =>
                (Key: FactoryReconfigurationOperationRecords.InputKey(operationId, index),
                 Value: Serialize(new FactoryReconfigurationInputChunk(1, operationId, index,
                     Convert.ToBase64String(bytes.AsSpan(index * FactoryReconfigurationOperationRecords.ChunkBytes,
                         Math.Min(FactoryReconfigurationOperationRecords.ChunkBytes, bytes.Length - index * FactoryReconfigurationOperationRecords.ChunkBytes))))))).ToArray();
            foreach (var record in chunks.Append((Key: FactoryReconfigurationOperationRecords.RootKey, Value: initial))
                .Append((Key: FactoryReconfigurationOperationRecords.RequestKey(operationId), Value: initial)))
                OwnerReservationStore.CheckSize(record.Key, ProductConfigurationKeys.NoLabel, record.Value);
            var current = await new FactoryReconfigurationService(reviewer, azure, http, clock).ReviewAsync(admission, input, approval, token);
            if (current.State != FactoryReconfigurationState.Reviewed || current.Fingerprint != approval)
                return Result(FactoryReconfigurationState.Blocked, "Current authority changed before admission; exact fresh review required.");
            effects = true;
            var coordinator = await store.WriteAsync(FactoryReconfigurationOperationRecords.RootKey, initial, null, token);
            closed = true;
            await store.WriteAsync(FactoryReconfigurationOperationRecords.RequestKey(operationId), initial, null, token);
            foreach (var chunk in chunks) await store.WriteAsync(chunk.Key, chunk.Value, null, token);
            operation = operation with { ConfirmedChunks = chunks.Length, CoordinatorId = Guid.NewGuid() };
            coordinator = await store.WriteAsync(coordinator.Key, Serialize(operation), coordinator.ETag, token);
            records = await store.ReadNamespaceAsync(FactoryReconfigurationOperationRecords.Prefix, token,
                FactoryReconfigurationOperationRecords.MaximumRecords);
            if (!records.TryGetValue(coordinator.Key, out var confirmed) || confirmed.ETag != coordinator.ETag
                || confirmed.Value != Serialize(operation) || !ReadInputs(records, operation, out _)) throw new JsonException();
            return Result(FactoryReconfigurationState.Partial,
                "Exact operator application admitted; old work admission closed. Quiescing is not completed stop, configuration application or activation.");
        }
        catch (Exception e) when (e is ReservationStoreException or JsonException or KeyNotFoundException or FormatException
            or HttpRequestException or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException)
        {
            return Result(e switch
            {
                OperationCanceledException => effects ? FactoryReconfigurationState.Unknown : FactoryReconfigurationState.Canceled,
                ReservationStoreException r when r.State == FactoryReservationState.Blocked => FactoryReconfigurationState.Blocked,
                ReservationStoreException r when r.State == FactoryReservationState.Failed => closed == true ? FactoryReconfigurationState.Partial : FactoryReconfigurationState.Failed,
                _ => FactoryReconfigurationState.Unknown,
            }, e is ReservationStoreException reservation
                ? reservation.Message
                : "Original transition state or dispatched write is unconfirmed. Inspect the retained operation; no rollback, old-configuration resume or forced replay.");
        }

        FactoryReconfigurationReview Result(FactoryReconfigurationState state, string detail) => new(state, detail)
        {
            FactoryId = admission.FactoryId, RepositoryId = admission.RepositoryId, Operation = operation,
            HasRemoteEffects = effects, AdmissionClosed = closed,
        };
    }

    private static bool ReadInputs(IReadOnlyDictionary<string, ConfigurationSetting> records, FactoryReconfigurationOperation operation,
        out FactoryReconfigurationInputs? input)
    {
        input = null;
        if (!records.TryGetValue(FactoryReconfigurationOperationRecords.RequestKey(operation.OperationId), out var requestRecord))
            return false;
        _ = FactoryReconfigurationOperationRecords.ReadAdmissionRequest(operation, requestRecord.Value);
        using var content = new MemoryStream();
        for (var index = 0; index < operation.InputChunks; index++)
        {
            if (!records.TryGetValue(FactoryReconfigurationOperationRecords.InputKey(operation.OperationId, index), out var record))
            {
                if (index < operation.ConfirmedChunks) throw new JsonException();
                return false;
            }
            var chunk = JsonSerializer.Deserialize<FactoryReconfigurationInputChunk>(record.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            var bytes = Convert.FromBase64String(chunk.Data);
            if (chunk.Protocol != 1 || chunk.OperationId != operation.OperationId || chunk.Index != index
                || bytes.Length != Math.Min(FactoryReconfigurationOperationRecords.ChunkBytes,
                    operation.InputBytes - index * FactoryReconfigurationOperationRecords.ChunkBytes))
                throw new JsonException();
            content.Write(bytes);
        }
        if (FactoryReservationRecords.Hash(content.ToArray()) != operation.InputSha256) throw new JsonException();
        input = JsonSerializer.Deserialize<FactoryReconfigurationInputs>(content.ToArray(), FactorySourceValidation.RecordJson) ?? throw new JsonException();
        return operation.ConfirmedChunks == operation.InputChunks;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, FactorySourceValidation.RecordJson);
}
