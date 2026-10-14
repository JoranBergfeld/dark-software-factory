using System.Text;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    internal PreviewPreparationResult ReadRetiredPreparation(IReadOnlyDictionary<string, string> values, Guid factory)
    {
        var coordinator = FactoryReservationRecords.Parse<PreviewPreparationCoordinator>(
            values.GetValueOrDefault(FactoryPreviewPreparation.RootKey) ?? throw new JsonException());
        if (coordinator.Protocol != 1 || coordinator.FactoryId != factory || coordinator.OperationId == Guid.Empty
            || coordinator.State != PreviewPreparationState.ConfirmedNotReady || coordinator.Owner != Guid.Empty)
            throw new JsonException();
        using var planBytes = new MemoryStream();
        for (var index = 0; index < coordinator.PlanChunks; index++)
        {
            var encoded = FactoryReservationRecords.Parse<string>(
                values.GetValueOrDefault(FactoryPreviewPreparation.ChunkKey(coordinator.OperationId, index))
                ?? throw new JsonException());
            planBytes.Write(Convert.FromBase64String(encoded));
        }
        var plan = System.Text.Json.JsonSerializer.Deserialize<PreviewPreparationPlan>(
            planBytes.ToArray(), FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (plan.Fingerprint != coordinator.PlanFingerprint
            || FactoryReservationRecords.Hash(planBytes.ToArray()) != coordinator.PlanSha256) throw new JsonException();
        var effects = Enumerable.Range(0, plan.Steps.Count).Select(index =>
            FactoryReservationRecords.Parse<PreviewPreparationEffect>(
                values.GetValueOrDefault(FactoryPreviewPreparation.EffectKey(coordinator.OperationId, index))
                ?? throw new JsonException())).ToArray();
        var receipts = values.Where(pair => pair.Key.StartsWith(
                FactoryPreviewPreparation.Namespace + "/" + coordinator.OperationId.ToString("D") + "/effects/",
                StringComparison.Ordinal) && pair.Key.Contains("/receipts/", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => FactoryReservationRecords.Parse<PreviewPreparationReceipt>(pair.Value)).ToArray();
        if (effects.Any(effect => effect.OperationId != coordinator.OperationId
            || effect.PlanFingerprint != coordinator.PlanFingerprint
            || effect.State != PreviewPreparationEffectState.Confirmed
            || effect.ReceiptHashes.Any(hash => !receipts.Any(receipt => FactoryModelsValidation.Hash(receipt) == hash))))
            throw new JsonException();
        return new(coordinator.State, "Retired exact native preparation.", plan, coordinator, effects, receipts);
    }

    internal async Task SeedReconfiguredAsync(FactoryReservationResult admission,
        PreviewPreparationResult previous, PreviewPreparationSelection selection,
        Func<Task> beforeDispatch, CancellationToken token)
    {
        if (previous.Plan is not { } oldPlan || previous.Preparation is not { State: PreviewPreparationState.ConfirmedNotReady } oldCoordinator
            || previous.Effects is null || previous.Receipts is null || oldCoordinator.OperationId != oldPlan.Selection.OperationId)
            throw Failure("CompleteConfirmedPriorPreparationRequired");
        var store = Store(admission);
        await store.RequirePristineAsync(FactoryPreviewPreparation.Namespace, token);
        var plan = await ReadPlanAsync(admission, selection, store, token);
        if (plan.Steps.Count != oldPlan.Steps.Count)
            throw Failure("ReconfiguredPreparationTopologyChanged");
        plan = plan with
        {
            Configuration = plan.Configuration with { PreparationOperationId = oldCoordinator.OperationId },
            Steps = plan.Steps.Select((step, index) => step with
            {
                Target = oldPlan.Steps[index].Target,
                ResourceTargets = oldPlan.Steps[index].ResourceTargets,
                Grant = oldPlan.Steps[index].Grant,
            }).ToArray(),
        };
        var oldEffects = previous.Effects.OrderBy(effect => effect.Index).ToArray();
        var receipts = previous.Receipts.ToArray();
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var oldStep = oldPlan.Steps[index];
            var oldEffect = oldEffects[index];
            if (step.Index != index || oldStep.Index != index || step.Action != oldStep.Action
                || step.Target != oldStep.Target || step.ApiVersion != oldStep.ApiVersion
                || step.BodySha256 != oldStep.BodySha256 || !step.ResourceTargets.SequenceEqual(oldStep.ResourceTargets)
                || oldEffect.State != PreviewPreparationEffectState.Confirmed)
                throw Failure("ReconfiguredPreparationRequiresDifferentNativeAssets");
            foreach (var hash in oldEffect.ReceiptHashes)
                if (!receipts.Any(receipt => FactoryModelsValidation.Hash(receipt) == hash))
                    throw Failure("PriorPreparationReceiptMissing");
        }
        var bytes = Encoding.UTF8.GetBytes(Serialize(plan));
        var coordinator = new PreviewPreparationCoordinator(1, FactoryPreviewPreparation.Kind,
            plan.Manifest.Identity.FactoryId, selection.OperationId, plan.Fingerprint,
            FactoryReservationRecords.Hash(bytes), bytes.Length,
            (bytes.Length + plan.Policy.ChunkBytes - 1) / plan.Policy.ChunkBytes,
            plan.Manifest.Identity.AzureActorId, clock.GetUtcNow(), Guid.Empty,
            PreviewPreparationState.ConfirmedNotReady, plan.Steps.Count, null);
        await beforeDispatch();
        await WriteAsync(store, FactoryPreviewPreparation.RootKey, coordinator, null, token);
        for (var index = 0; index < coordinator.PlanChunks; index++)
        {
            await beforeDispatch();
            await WriteAsync(store, FactoryPreviewPreparation.ChunkKey(selection.OperationId, index),
                Convert.ToBase64String(bytes.AsSpan(index * plan.Policy.ChunkBytes,
                    Math.Min(plan.Policy.ChunkBytes, bytes.Length - index * plan.Policy.ChunkBytes))), null, token);
        }
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var oldEffect = oldEffects[index];
            var effect = oldEffect with
            {
                OperationId = selection.OperationId,
                PlanFingerprint = plan.Fingerprint,
                ReusedFromOperationId = oldCoordinator.OperationId,
            };
            await beforeDispatch();
            await WriteAsync(store, FactoryPreviewPreparation.EffectKey(selection.OperationId, index), effect, null, token);
            var reused = receipts.Where(receipt => oldEffect.ReceiptHashes.Contains(FactoryModelsValidation.Hash(receipt))).ToArray();
            for (var receipt = 0; receipt < reused.Length; receipt++)
            {
                await beforeDispatch();
                await WriteAsync(store, FactoryPreviewPreparation.ReceiptKey(selection.OperationId, index, receipt),
                    reused[receipt], null, token);
            }
        }
        var journal = await ReadJournalAsync(admission, store, token) ?? throw Unknown("ReconfiguredPreparationJournalUnconfirmed");
        await VerifyConfirmedAsync(journal, token);
    }
}
