using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryReconfigurationTransition
{
    private const int SnapshotChunkBytes = 3000;

    private async Task<FactoryReconfigurationOperation> ApplyConfigurationAsync(
        FactoryReservationResult admission, OwnerReservationStore store,
        ConfigurationSetting root, FactoryReconfigurationOperation operation,
        FactoryReconfigurationInputs input, FactoryReconfigurationReview? reviewed,
        Action beforeDispatch, CancellationToken token)
    {
        await ReconcileClaimsAsync(admission, store, operation, input, additions: true, beforeDispatch, token);
        var targets = operation.Phase == FactoryReconfigurationPhase.Quiescing
            ? await BuildTargetsAsync(admission, store, input, reviewed
                ?? throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Fresh exact review required before configuration reconciliation."), token)
            : await RebuildTargetsAsync(admission, store, operation, input, token);
        var snapshots = new List<(FactoryReconfigurationSnapshot Snapshot, string Value, string Desired)>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var current = await store.ReadAsync(target.Key, token)
                ?? throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Original configuration record missing during reconciliation; no replacement invented.");
            var desiredBytes = Encoding.UTF8.GetByteCount(target.Value);
            snapshots.Add((new(1, operation.OperationId, index, target.Key, current.ETag.ToString(),
                FactoryReservationRecords.Hash(current.Value), Encoding.UTF8.GetByteCount(current.Value),
                (Encoding.UTF8.GetByteCount(current.Value) + SnapshotChunkBytes - 1) / SnapshotChunkBytes,
                FactoryReservationRecords.Hash(target.Value), desiredBytes,
                (desiredBytes + SnapshotChunkBytes - 1) / SnapshotChunkBytes), current.Value, target.Value));
        }
        if (operation.Phase == FactoryReconfigurationPhase.Quiescing)
        {
            beforeDispatch();
            foreach (var snapshot in snapshots)
                await EnsureSnapshotAsync(store, operation, snapshot.Snapshot, snapshot.Value,
                    snapshot.Desired, token);
            operation = operation with
            {
                Phase = FactoryReconfigurationPhase.Reconciling,
                CoordinatorId = Guid.NewGuid(),
                SnapshotRecords = snapshots.Count,
            };
            root = await store.WriteAsync(root.Key, Serialize(operation), root.ETag, token);
        }
        else
        {
            if (operation.SnapshotRecords != targets.Count)
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Retained reconciliation target count differs; no silent target expansion.");
            snapshots.Clear();
            for (var index = 0; index < targets.Count; index++)
                snapshots.Add((await ReadSnapshotAsync(store, operation, index, token)));
        }
        for (var index = operation.AppliedRecords; index < targets.Count; index++)
        {
            var target = targets[index];
            var snapshot = snapshots[index];
            if (snapshot.Snapshot.Key != target.Key)
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Retained reconciliation target differs; no reordered application.");
            var current = await store.ReadAsync(target.Key, token)
                ?? throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Configuration disappeared during reconciliation.");
            if (current.Value != target.Value)
            {
                if (current.Value != snapshot.Value || current.ETag.ToString() != snapshot.Snapshot.ETag)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "ConcurrentConfigurationChange: original reviewed record changed; no overwrite or forced continuation.");
                beforeDispatch();
                current = await store.WriteAsync(target.Key, target.Value, current.ETag, token);
            }
            operation = operation with
            {
                AppliedRecords = index + 1,
                CoordinatorId = Guid.NewGuid(),
                AppliedManifestRevision = target.Key == ProductConfigurationKeys.FactoryManifest
                    ? current.ETag.ToString() : operation.AppliedManifestRevision,
            };
            root = await store.WriteAsync(root.Key, Serialize(operation), root.ETag, token);
        }
        operation = operation with { Phase = FactoryReconfigurationPhase.AwaitingEvidence, CoordinatorId = Guid.NewGuid() };
        await ReconcileClaimsAsync(admission, store, operation, input, additions: false, beforeDispatch, token);
        await store.WriteAsync(root.Key, Serialize(operation), root.ETag, token);
        return operation;
    }

    private async Task ReconcileClaimsAsync(FactoryReservationResult admission, OwnerReservationStore product,
        FactoryReconfigurationOperation operation, FactoryReconfigurationInputs input,
        bool additions, Action beforeDispatch, CancellationToken token)
    {
        var owner = new OwnerReservationStore(admission.Identity!.OwnerEndpoint, admission.Identity.TenantId,
            admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);
        var ownerRecords = await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token);
        var proof = new ReservationAdmissionProof();
        foreach (var record in ownerRecords.Values) proof.Add(record);
        var plan = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
            .PlanAsync(input.Boundary, token, reservationProof: proof);
        if (plan.State != BoundaryPlanState.Reviewed || plan.RepositoryReview.Repository?.Id != admission.Identity.RepositoryId)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Current claim authority changed before reconciliation.");
        var current = admission.Plan!.OwnerAdmission!.ProposedClaims.Where(c => c.Kind != "product")
            .ToDictionary(c => c.Key, StringComparer.Ordinal);
        var desired = plan.OwnerAdmission!.ProposedClaims.Where(c => c.Kind != "product")
            .ToDictionary(c => c.Key, StringComparer.Ordinal);
        var changes = current.Keys.Union(desired.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(key => current.ContainsKey(key) != desired.ContainsKey(key)).ToArray();
        for (var index = 0; index < changes.Length; index++)
        {
            var key = changes[index];
            var adding = desired.TryGetValue(key, out var claim);
            if (adding != additions) continue;
            ownerRecords.TryGetValue(key, out var existing);
            string? after = adding
                ? FactoryReservationRecords.Serialize(new FactoryReservationClaim(
                    1, FactoryReservationRecords.ClaimKind, admission.Identity, claim!))
                : null;
            var change = new FactoryReconfigurationClaimChange(1, operation.OperationId, index, key,
                existing?.ETag.ToString(), existing is null ? null : FactoryReservationRecords.Hash(existing.Value),
                after, after is null ? null : FactoryReservationRecords.Hash(after));
            var journalKey = FactoryReconfigurationOperationRecords.ClaimChangeKey(operation.OperationId, index);
            var journal = await product.ReadAsync(journalKey, token);
            if (journal is null)
            {
                beforeDispatch();
                await product.WriteAsync(journalKey, Serialize(change), null, token);
            }
            else if (journal.Value != Serialize(change))
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Retained claim reconciliation intent differs; no replacement.");
            existing = await owner.ReadAsync(key, token);
            if (adding)
            {
                if (existing is null)
                {
                    beforeDispatch();
                    await owner.WriteAsync(key, after!, null, token);
                }
                else if (existing.Value != after)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Competing owner claim prevents reconfiguration; no takeover.");
            }
            else if (existing is not null)
            {
                if (FactoryReservationRecords.Hash(existing.Value) != change.BeforeSha256
                    || existing.ETag.ToString() != change.BeforeETag)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Removed owner claim changed after review; no release.");
                beforeDispatch();
                await owner.DeleteAsync(existing, token);
            }
        }
    }

    private async Task<IReadOnlyList<(string Key, string Value)>> BuildTargetsAsync(
        FactoryReservationResult admission, OwnerReservationStore store, FactoryReconfigurationInputs input,
        FactoryReconfigurationReview review, CancellationToken token)
    {
        var manifestRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token) ?? throw new JsonException();
        var manifest = FactoryGovernance.ReadInactiveManifest(manifestRecord.Value,
            (await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token))?.Value,
            admission.Identity!.ProductKey, admission.Foundation!.ProductEndpoint!);
        var targets = new List<(string Key, string Value)>();
        if (review.Changes.Any(c => c.Category is "application" or "backend" or "grant" or "telemetry"
                or "query" or "stale-query"))
        {
            var currentRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryTelemetry, token) ?? throw new JsonException();
            var current = FactoryReservationRecords.Parse<FactoryTelemetry>(currentRecord.Value);
            var proof = new ReservationAdmissionProof();
            var owner = new OwnerReservationStore(admission.Identity.OwnerEndpoint, admission.Identity.TenantId,
                admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);
            foreach (var record in (await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token)).Values)
                proof.Add(record);
            var plan = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
                .PlanAsync(input.Boundary, token, reservationProof: proof);
            if (plan.State != BoundaryPlanState.Reviewed || plan.RepositoryReview.Repository?.Id != admission.Identity.RepositoryId)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Current boundary authority changed before configuration application.");
            var backends = input.Boundary.EvidenceBackends.Select(selected =>
            {
                var workspace = plan.Azure!.Resources.Single(r => r.Id == selected.ResourceId);
                return new TelemetryBackend(selected.ResourceId, workspace.WorkspaceId!, selected.Tables,
                    plan.Azure.Resources.Where(r => r.Type == "microsoft.insights/components"
                        && r.BackingWorkspaceResourceId == selected.ResourceId
                        && input.Boundary.ApplicationResourceIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(r => r.Id).Order(StringComparer.Ordinal).ToArray());
            }).ToArray();
            var configuration = current.Configuration with
            {
                ApplicationResourceIds = input.Boundary.ApplicationResourceIds,
                Backends = backends,
                RuntimeImage = input.Telemetry.RuntimeImage,
                AudienceClientId = input.Telemetry.AudienceClientId,
                FreshnessSeconds = input.Telemetry.FreshnessSeconds,
                QueryRevision = input.Telemetry.QueryRevision,
                Queries = input.Telemetry.Queries,
                StaleQueries = input.Telemetry.StaleQueries,
            };
            configuration = configuration with
            {
                AccessGrants = FactoryTelemetryValidation.Grants(configuration, admission.Identity.SubscriptionId),
            };
            var telemetry = current with
            {
                ApprovalFingerprint = FactoryTelemetryValidation.Hash(configuration),
                Configuration = configuration,
                State = TelemetryState.Partial,
                Stage = "prepared",
                Detail = "Reconfigured telemetry requires a new current proof; previous evidence is historical.",
                Proof = null,
                LastFailure = TelemetryReason.None,
                CheckId = null,
                LastCheckReference = null,
                LastCheckSha256 = null,
                VerifiedProofReference = null,
                VerifiedProofSha256 = null,
            };
            targets.Add((ProductConfigurationKeys.FactoryTelemetry, FactoryReservationRecords.Serialize(telemetry)));
            manifest = manifest with { TelemetryApprovalFingerprint = telemetry.ApprovalFingerprint };
        }
        manifest = manifest with { PreviewOperationId = null, PreviewApprovalFingerprint = null };
        targets.Add((ProductConfigurationKeys.FactoryManifest, FactoryReservationRecords.Serialize(manifest)));
        return targets;
    }

    private async Task<IReadOnlyList<(string Key, string Value)>> RebuildTargetsAsync(
        FactoryReservationResult admission, OwnerReservationStore store,
        FactoryReconfigurationOperation operation, FactoryReconfigurationInputs input, CancellationToken token)
    {
        var targets = new List<(string Key, string Value)>();
        for (var index = 0; index < operation.SnapshotRecords; index++)
        {
            var snapshot = await ReadSnapshotAsync(store, operation, index, token);
            targets.Add((snapshot.Snapshot.Key, snapshot.Desired));
        }
        return targets;
    }

    private static async Task EnsureSnapshotAsync(OwnerReservationStore store,
        FactoryReconfigurationOperation operation, FactoryReconfigurationSnapshot snapshot,
        string value, string desired, CancellationToken token)
    {
        var descriptor = Serialize(snapshot);
        var descriptorKey = FactoryReconfigurationOperationRecords.SnapshotKey(operation.OperationId, snapshot.Index);
        var existing = await store.ReadAsync(descriptorKey, token);
        if (existing is null) await store.WriteAsync(descriptorKey, descriptor, null, token);
        else if (existing.Value != descriptor) throw new JsonException();
        var bytes = Encoding.UTF8.GetBytes(value);
        for (var index = 0; index < snapshot.Chunks; index++)
        {
            var chunk = Serialize(new FactoryReconfigurationSnapshotChunk(1, operation.OperationId, snapshot.Index, index, false,
                Convert.ToBase64String(bytes.AsSpan(index * SnapshotChunkBytes,
                    Math.Min(SnapshotChunkBytes, bytes.Length - index * SnapshotChunkBytes)))));
            var key = FactoryReconfigurationOperationRecords.SnapshotChunkKey(operation.OperationId, snapshot.Index, index);
            existing = await store.ReadAsync(key, token);
            if (existing is null) await store.WriteAsync(key, chunk, null, token);
            else if (existing.Value != chunk) throw new JsonException();
        }
        bytes = Encoding.UTF8.GetBytes(desired);
        for (var index = 0; index < snapshot.DesiredChunks; index++)
        {
            var chunk = Serialize(new FactoryReconfigurationSnapshotChunk(1, operation.OperationId, snapshot.Index, index, true,
                Convert.ToBase64String(bytes.AsSpan(index * SnapshotChunkBytes,
                    Math.Min(SnapshotChunkBytes, bytes.Length - index * SnapshotChunkBytes)))));
            var key = FactoryReconfigurationOperationRecords.SnapshotDesiredChunkKey(operation.OperationId, snapshot.Index, index);
            existing = await store.ReadAsync(key, token);
            if (existing is null) await store.WriteAsync(key, chunk, null, token);
            else if (existing.Value != chunk) throw new JsonException();
        }
    }

    private static async Task<(FactoryReconfigurationSnapshot Snapshot, string Value, string Desired)> ReadSnapshotAsync(
        OwnerReservationStore store, FactoryReconfigurationOperation operation, int index, CancellationToken token)
    {
        var record = await store.ReadAsync(FactoryReconfigurationOperationRecords.SnapshotKey(operation.OperationId, index), token)
            ?? throw new JsonException();
        var snapshot = JsonSerializer.Deserialize<FactoryReconfigurationSnapshot>(record.Value, FactorySourceValidation.RecordJson)
            ?? throw new JsonException();
        if (snapshot.Protocol != 1 || snapshot.OperationId != operation.OperationId || snapshot.Index != index
            || snapshot.Bytes <= 0 || snapshot.Chunks != (snapshot.Bytes + SnapshotChunkBytes - 1) / SnapshotChunkBytes
            || snapshot.DesiredBytes <= 0
            || snapshot.DesiredChunks != (snapshot.DesiredBytes + SnapshotChunkBytes - 1) / SnapshotChunkBytes)
            throw new JsonException();
        using var content = new MemoryStream();
        for (var chunkIndex = 0; chunkIndex < snapshot.Chunks; chunkIndex++)
        {
            var chunkRecord = await store.ReadAsync(
                FactoryReconfigurationOperationRecords.SnapshotChunkKey(operation.OperationId, index, chunkIndex), token)
                ?? throw new JsonException();
            var chunk = JsonSerializer.Deserialize<FactoryReconfigurationSnapshotChunk>(
                chunkRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (chunk.Protocol != 1 || chunk.OperationId != operation.OperationId
                || chunk.Snapshot != index || chunk.Index != chunkIndex || chunk.Desired) throw new JsonException();
            content.Write(Convert.FromBase64String(chunk.Data));
        }
        var bytes = content.ToArray();
        if (bytes.Length != snapshot.Bytes || FactoryReservationRecords.Hash(bytes) != snapshot.Sha256) throw new JsonException();
        var value = Encoding.UTF8.GetString(bytes);
        content.SetLength(0);
        for (var chunkIndex = 0; chunkIndex < snapshot.DesiredChunks; chunkIndex++)
        {
            var chunkRecord = await store.ReadAsync(
                FactoryReconfigurationOperationRecords.SnapshotDesiredChunkKey(operation.OperationId, index, chunkIndex), token)
                ?? throw new JsonException();
            var chunk = JsonSerializer.Deserialize<FactoryReconfigurationSnapshotChunk>(
                chunkRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (chunk.Protocol != 1 || chunk.OperationId != operation.OperationId
                || chunk.Snapshot != index || chunk.Index != chunkIndex || !chunk.Desired) throw new JsonException();
            content.Write(Convert.FromBase64String(chunk.Data));
        }
        bytes = content.ToArray();
        if (bytes.Length != snapshot.DesiredBytes || FactoryReservationRecords.Hash(bytes) != snapshot.DesiredSha256)
            throw new JsonException();
        return (snapshot, value, Encoding.UTF8.GetString(bytes));
    }
}
