using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    private const string ContentType = "application/vnd.dsf.preview-preparation+json;version=1";
    private static readonly JsonSerializerOptions RecordJson = new(FactorySourceValidation.RecordJson) { IgnoreReadOnlyProperties = false };
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, RecordJson);
    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, RecordJson) ?? throw new JsonException();

    private static void CheckCapacity(PreviewPreparationPlan plan, FactoryLivePlan? live = null)
    {
        var bytes = Encoding.UTF8.GetBytes(live is null ? Serialize(plan) : Serialize(live));
        var policy = FactoryPreviewPreparation.Policy;
        if (bytes.Length > policy.PlanBytes || plan.Steps.Count is < 1 || plan.Steps.Count > policy.MaximumEffects)
            throw Failure("CompletePreviewPreparationLifecycleCapacityExceeded");
        var chunks = (bytes.Length + policy.ChunkBytes - 1) / policy.ChunkBytes;
        if (chunks > policy.MaximumChunks) throw Failure("CompletePreviewPreparationPlanCapacityExceeded");
        var operation = live?.Selection.OperationId ?? plan.Selection.OperationId;
        var id = plan.Manifest.Identity.FactoryId;
        var hash = new string('f', 64);
        Check(live is null ? FactoryPreviewPreparation.RootKey : FactoryLive.RootKey, new PreviewPreparationCoordinator(1, FactoryPreviewPreparation.Kind, id, operation,
            hash, hash, bytes.Length, chunks, plan.Manifest.Identity.AzureActorId, DateTimeOffset.MaxValue, Guid.Empty,
            PreviewPreparationState.Unknown, plan.Steps.Count, new string('x', 1024)));
        for (var i = 0; i < chunks; i++)
            Check(live is null ? FactoryPreviewPreparation.ChunkKey(operation, i) : FactoryLive.ChunkKey(operation, i),
                Convert.ToBase64String(bytes.AsSpan(i * policy.ChunkBytes, Math.Min(policy.ChunkBytes, bytes.Length - i * policy.ChunkBytes))));
        foreach (var step in plan.Steps)
        {
            Check(live is null ? FactoryPreviewPreparation.EffectKey(operation, step.Index) : FactoryLive.EffectKey(operation, step.Index),
                new PreviewPreparationEffect(1, id, operation, hash, step.Index, step.Target, hash, Guid.Empty,
                    DateTimeOffset.MaxValue, PreviewPreparationEffectState.Unknown,
                    Enumerable.Repeat(hash, step.ResourceTargets.Count).ToArray(), new string('x', 1024))
                { CouncilEndpoint = "https://" + new string('a', 253), DeploymentOutputsSha256 = hash,
                    Acknowledgment = new(599, Guid.Empty.ToString(), Guid.Empty.ToString(), DateTimeOffset.MaxValue, hash, Guid.Empty.ToString(), "Succeeded") });
            for (var i = 0; i < step.ResourceTargets.Count; i++)
                Check(live is null ? FactoryPreviewPreparation.ReceiptKey(operation, step.Index, i) : FactoryLive.ReceiptKey(operation, step.Index, i),
                    new PreviewPreparationReceipt(step.ResourceTargets[i], TargetApi(step.ResourceTargets[i]), new string('x', 256),
                        hash, 201, new string('f', 128), new string('f', 128),
                        step.Action == PreviewPreparationAction.WorkloadDeployment ? step.Target : null,
                        step.Action == PreviewPreparationAction.WorkloadDeployment ? new string('f', 128) : null)
                    { Endpoint = "https://" + new string('a', 253) });
        }
        static void Check<T>(string key, T value) => OwnerReservationStore.CheckSize(key, "", Serialize(value), ContentType);
    }

    private sealed record Journal(PreviewPreparationPlan Plan, PreviewPreparationCoordinator Coordinator,
        ConfigurationSetting Root, IReadOnlyList<PreviewPreparationEffect> Effects,
        IReadOnlyList<ConfigurationSetting> EffectRecords, IReadOnlyList<PreviewPreparationReceipt> Receipts);

    private async Task<Journal?> ReadJournalAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var root = await store.ReadAsync(FactoryPreviewPreparation.RootKey, token);
        if (root is null)
        {
            await store.RequirePristineAsync(FactoryPreviewPreparation.Namespace, token);
            return null;
        }
        var phase = "coordinator";
        try
        {
            var coordinator = Record<PreviewPreparationCoordinator>(root);
            var identity = admission.Identity!;
            var policy = FactoryPreviewPreparation.Policy;
            if (coordinator.Protocol != 1 || coordinator.Kind != FactoryPreviewPreparation.Kind || coordinator.FactoryId != identity.FactoryId
                || coordinator.OperationId == Guid.Empty || coordinator.ApprovedBy != identity.AzureActorId
                || coordinator.ApprovedAt < identity.CreatedAt || coordinator.ApprovedAt > clock.GetUtcNow()
                || coordinator.ApprovedAt.Offset != TimeSpan.Zero || !FactorySourceValidation.Hash(coordinator.PlanFingerprint)
                || !FactorySourceValidation.Hash(coordinator.PlanSha256) || coordinator.PlanBytes is < 1 || coordinator.PlanBytes > policy.PlanBytes
                || coordinator.PlanChunks != (coordinator.PlanBytes + policy.ChunkBytes - 1) / policy.ChunkBytes
                || coordinator.PlanChunks > policy.MaximumChunks || !Enum.IsDefined(coordinator.State))
                throw new JsonException();
            var records = await store.ReadNamespaceAsync(FactoryPreviewPreparation.Namespace + "/", token);
            var keys = new HashSet<string>(StringComparer.Ordinal) { FactoryPreviewPreparation.RootKey };
            if (!records.TryGetValue(root.Key, out var captured) || captured.ETag != root.ETag || captured.Value != root.Value)
                throw new JsonException();
            using var bytes = new MemoryStream(coordinator.PlanBytes);
            phase = "plan-chunks";
            for (var i = 0; i < coordinator.PlanChunks; i++)
            {
                var key = FactoryPreviewPreparation.ChunkKey(coordinator.OperationId, i);
                keys.Add(key);
                var chunk = Convert.FromBase64String(Record<string>(records[key]));
                if (chunk.Length != Math.Min(policy.ChunkBytes, coordinator.PlanBytes - i * policy.ChunkBytes)) throw new JsonException();
                bytes.Write(chunk);
            }
            if (bytes.Length != coordinator.PlanBytes || FactoryReservationRecords.Hash(bytes.ToArray()) != coordinator.PlanSha256) throw new JsonException();
            var plan = Parse<PreviewPreparationPlan>(Encoding.UTF8.GetString(bytes.ToArray()));
            phase = "plan-integrity";
            FactoryPreviewPreparation.Validate(plan.Selection);
            CheckCapacity(plan);
            if (plan.Protocol != 1 || plan.Policy != policy || plan.Selection.OperationId != coordinator.OperationId
                || plan.Manifest.Identity != identity || plan.Fingerprint != coordinator.PlanFingerprint
                || plan.WorkloadTemplateSha256 != FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-preview-workloads.json")))
                || plan.Steps.Where((s, i) => s.Index != i).Any())
                throw new JsonException();
            var effects = new List<PreviewPreparationEffect>();
            var effectRecords = new List<ConfigurationSetting>();
            var receipts = new List<PreviewPreparationReceipt>();
            foreach (var step in plan.Steps)
            {
                phase = "effect-" + step.Index;
                var key = FactoryPreviewPreparation.EffectKey(coordinator.OperationId, step.Index);
                keys.Add(key);
                var record = records[key];
                var effect = Record<PreviewPreparationEffect>(record);
                if (effect.Protocol != 1 || effect.FactoryId != identity.FactoryId || effect.OperationId != coordinator.OperationId
                    || effect.PlanFingerprint != plan.Fingerprint || effect.Index != step.Index || effect.Target != step.Target
                    || effect.BodySha256 != step.BodySha256 || effect.RequestId == Guid.Empty
                    || effect.IntendedAt < coordinator.ApprovedAt || effect.IntendedAt > clock.GetUtcNow() || !Enum.IsDefined(effect.State)
                    || effect.ReceiptHashes.Count > step.ResourceTargets.Count
                    || effect.State == PreviewPreparationEffectState.Confirmed && effect.ReceiptHashes.Count != step.ResourceTargets.Count
                    || effect.State == PreviewPreparationEffectState.Intended && effect.ReceiptHashes.Count != 0)
                    throw new JsonException();
                var acknowledgment = effect.Acknowledgment;
                if (effect.State == PreviewPreparationEffectState.Confirmed
                    && (acknowledgment is not { StatusCode: 201 } || !FactorySourceValidation.Hash(acknowledgment.BodySha256)
                        || acknowledgment.RequestId is null || acknowledgment.CorrelationId is null
                        || step.Action == PreviewPreparationAction.WorkloadDeployment && acknowledgment.ProvisioningState != "Succeeded")
                    || acknowledgment is not null && (acknowledgment.StatusCode is < 100 or > 599 || acknowledgment.ObservedAt < effect.IntendedAt
                        || acknowledgment.ObservedAt > clock.GetUtcNow()
                        || acknowledgment.RequestId is not null && (!Guid.TryParse(acknowledgment.RequestId, out var acknowledgedRequest) || acknowledgedRequest == Guid.Empty)
                        || acknowledgment.CorrelationId is not null && (!Guid.TryParse(acknowledgment.CorrelationId, out var acknowledgedCorrelation) || acknowledgedCorrelation == Guid.Empty)
                        || acknowledgment.BodySha256 is not null && !FactorySourceValidation.Hash(acknowledgment.BodySha256)))
                    throw new JsonException();
                for (var i = 0; i < effect.ReceiptHashes.Count; i++)
                {
                    phase = "receipt-" + step.Index + "-" + i;
                    var receiptKey = FactoryPreviewPreparation.ReceiptKey(coordinator.OperationId, step.Index, i);
                    keys.Add(receiptKey);
                    var receipt = Record<PreviewPreparationReceipt>(records[receiptKey]);
                    if (FactoryModelsValidation.Hash(receipt) != effect.ReceiptHashes[i] || receipt.Target != step.ResourceTargets[i]
                        || receipt.ApiVersion != TargetApi(receipt.Target) || string.IsNullOrEmpty(receipt.Incarnation)
                        || !FactorySourceValidation.Hash(receipt.NativeRevision) || receipt.StatusCode is not (200 or 201)
                        || !Guid.TryParse(receipt.RequestId, out var request) || request == Guid.Empty
                        || !Guid.TryParse(receipt.CorrelationId, out var correlation) || correlation == Guid.Empty
                        || (step.Action == PreviewPreparationAction.WorkloadDeployment
                            ? receipt.DeploymentTarget != step.Target || !FactoryTelemetryValidation.IsProviderOperationId(receipt.OperationId)
                                || acknowledgment?.DeploymentCorrelationId != receipt.CorrelationId
                            : receipt.DeploymentTarget is not null || receipt.OperationId is not null
                                || acknowledgment?.RequestId != receipt.RequestId || acknowledgment.CorrelationId != receipt.CorrelationId))
                        throw new JsonException();
                    receipts.Add(receipt);
                }
                effects.Add(effect);
                effectRecords.Add(record);
            }
            phase = "complete-record-set";
            if (!keys.SetEquals(records.Keys) || effects.Select(e => e.RequestId).Distinct().Count() != effects.Count
                || coordinator.ConfirmedEffects > effects.Count(e => e.State == PreviewPreparationEffectState.Confirmed)
                || coordinator.State == PreviewPreparationState.ConfirmedNotReady
                    && (coordinator.Owner != Guid.Empty || coordinator.ConfirmedEffects != effects.Count
                        || effects.Any(e => e.State != PreviewPreparationEffectState.Confirmed)))
                throw new JsonException();
            var final = await store.ReadAsync(root.Key, token);
            phase = "stable-coordinator";
            if (final?.ETag != root.ETag || final.Value != root.Value) throw new JsonException();
            return new(plan, coordinator, root, effects, effectRecords, receipts);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or ArgumentException or InvalidOperationException)
        { throw Unknown("PreviewPreparationHistoryIncompleteOrIncompatible:" + phase + "; no recreation or replay."); }
    }

    private static T Record<T>(ConfigurationSetting record)
    {
        if (record.ContentType != ContentType) throw new JsonException();
        return Parse<T>(record.Value);
    }

    private static ReservationStoreException Unknown(string reason) => new(FactoryReservationState.Unknown, reason);
    private static Task<ConfigurationSetting> WriteAsync<T>(OwnerReservationStore store, string key, T value,
        ConfigurationSetting? previous, CancellationToken token) =>
        store.WriteAsync(key, Serialize(value), previous?.ETag, token, contentType: ContentType);
}
