using System.Text;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    internal async Task<PreviewPreparationResult> InspectAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var store = Store(admission);
        var journal = await ReadJournalAsync(admission, store, token);
        if (journal is null) return new(PreviewPreparationState.Blocked, "NativePreparationNotApproved; review an explicit preparation selection.");
        if (journal.Effects.Any(e => e.State is PreviewPreparationEffectState.Dispatched or PreviewPreparationEffectState.Unknown)
            || journal.Coordinator.State == PreviewPreparationState.Unknown)
            return Result(journal, PreviewPreparationState.Unknown, "NativePreparationOutcomeUnknown; no replay or lease-based takeover.");
        try
        {
            if (journal.Coordinator.State == PreviewPreparationState.ConfirmedNotReady)
                await RequirePublicationCurrentAsync(admission, journal, store, token);
            else
            {
                await RequireCurrentAsync(admission, journal.Plan, store, token, journal);
                await VerifyConfirmedAsync(journal, token);
            }
        }
        catch (Exception e) when (e is ReservationStoreException or PreviewCapabilityException or CharterAdoptionEvidenceException
            or CharterOperationException or ModelCapabilityException or System.Text.Json.JsonException or InvalidOperationException
            or ArgumentException or KeyNotFoundException or HttpRequestException or OperationCanceledException)
        {
            return Result(journal, FailureState(e), e is ReservationStoreException ? e.Message
                : "HistoricalNativePreparationRetainedButCurrentAuthorityUnconfirmed; no readiness or replay.");
        }
        return Result(journal, journal.Coordinator.State,
            "Native preparation only. Capability publication, execution seed, and evaluation approval are still required; currentReady=false.");
    }

    internal async Task<PreviewPreparationResult> PrepareAsync(FactoryReservationResult admission, PreviewPreparationSelection? selection,
        string approval, bool resume, CancellationToken token)
    {
        var store = Store(admission);
        Journal? journal = null;
        ConfigurationSetting? root = null;
        PreviewPreparationCoordinator? coordinator = null;
        PreviewPreparationPlan? plan = null;
        var effects = new List<PreviewPreparationEffect>();
        var effectRecords = new List<ConfigurationSetting>();
        var receipts = new List<PreviewPreparationReceipt>();
        var active = -1;
        var nativeDispatched = false;
        PreviewPreparationAcknowledgment? acknowledgment = null;
        try
        {
            if (resume)
            {
                journal = await ReadJournalAsync(admission, store, token) ?? throw Failure("OriginalPreviewPreparationRequired");
                plan = journal.Plan;
                if (approval != plan.Fingerprint) throw Failure("ExactPreviewPreparationApprovalRequired; no effects.");
                if (journal.Coordinator.State == PreviewPreparationState.ConfirmedNotReady) return await InspectAsync(admission, token);
                if (journal.Coordinator.Owner != Guid.Empty
                    || journal.Coordinator.State is PreviewPreparationState.Unknown or PreviewPreparationState.KnownFailed
                    || journal.Effects.Any(e => e.State is not (PreviewPreparationEffectState.Intended or PreviewPreparationEffectState.Confirmed)))
                    return Result(journal, PreviewPreparationState.Unknown, "HeldOrUncertainNativePreparationCannotBeReplayed");
                await RequireCurrentAsync(admission, plan, store, token, journal);
                await VerifyConfirmedAsync(journal, token);
                await new FactoryAuditService(azure, http, clock).ProtectAnalysisAsync(admission,
                    plan.Source.AnalysisRevision, plan.Source.AnalysisChain, plan.Fingerprint, token);
                effects.AddRange(journal.Effects);
                effectRecords.AddRange(journal.EffectRecords);
                receipts.AddRange(journal.Receipts);
                coordinator = journal.Coordinator with { Owner = Guid.NewGuid(), State = PreviewPreparationState.Partial, Reason = null };
                root = await WriteAsync(store, FactoryPreviewPreparation.RootKey, coordinator, journal.Root, token);
            }
            else
            {
                plan = await PlanAsync(admission, selection ?? throw Failure("ExplicitPreviewSelectionRequired"), token);
                if (approval != plan.Fingerprint) throw Failure("ExactPreviewPreparationApprovalRequired; no effects.");
                CheckCapacity(plan);
                await new FactoryAuditService(azure, http, clock).ProtectAnalysisAsync(admission,
                    plan.Source.AnalysisRevision, plan.Source.AnalysisChain, plan.Fingerprint, token);
                var bytes = Encoding.UTF8.GetBytes(Serialize(plan));
                var chunkSize = plan.Policy.ChunkBytes;
                coordinator = new(1, FactoryPreviewPreparation.Kind, plan.Manifest.Identity.FactoryId, plan.Selection.OperationId,
                    plan.Fingerprint, FactoryReservationRecords.Hash(bytes), bytes.Length, (bytes.Length + chunkSize - 1) / chunkSize,
                    plan.Manifest.Identity.AzureActorId, clock.GetUtcNow(), Guid.NewGuid(), PreviewPreparationState.Partial, 0, null);
                root = await WriteAsync(store, FactoryPreviewPreparation.RootKey, coordinator, null, token);
                for (var i = 0; i < coordinator.PlanChunks; i++)
                    await WriteAsync(store, FactoryPreviewPreparation.ChunkKey(coordinator.OperationId, i),
                        Convert.ToBase64String(bytes.AsSpan(i * chunkSize, Math.Min(chunkSize, bytes.Length - i * chunkSize))), null, token);
                foreach (var step in plan.Steps)
                {
                    var effect = new PreviewPreparationEffect(1, coordinator.FactoryId, coordinator.OperationId, plan.Fingerprint,
                        step.Index, step.Target, step.BodySha256, Guid.NewGuid(), clock.GetUtcNow(), PreviewPreparationEffectState.Intended, [], null);
                    effectRecords.Add(await WriteAsync(store, FactoryPreviewPreparation.EffectKey(coordinator.OperationId, step.Index), effect, null, token));
                    effects.Add(effect);
                }
                journal = await ReadJournalAsync(admission, store, token) ?? throw Unknown("NativePreparationIntentAcknowledgmentLost");
            }
            var arm = new FactoryPreparationArm(azure, http, plan.Manifest.Identity);
            foreach (var step in plan.Steps)
            {
                if (effects[step.Index].State == PreviewPreparationEffectState.Confirmed) continue;
                active = step.Index;
                nativeDispatched = false;
                acknowledgment = null;
                await RequireCurrentAsync(admission, plan, store, token, journal);
                await RequireHeldAsync();
                await AbsentAsync(arm, step.Target, step.ApiVersion, token);
                if (step.Action == PreviewPreparationAction.WorkloadDeployment)
                    foreach (var target in step.ResourceTargets) await AbsentAsync(arm, target, TargetApi(target), token);
                else await GrantPermissionAsync(arm, step.Grant!, token);
                await SaveEffectAsync(effects[active] with { State = PreviewPreparationEffectState.Dispatched }, token);
                await RequireHeldAsync();
                nativeDispatched = true;
                var created = await DispatchAsync(plan, step, effects[active], arm, async observed =>
                {
                    acknowledgment = observed;
                    await SaveEffectAsync(effects[active] with { Acknowledgment = observed }, token);
                }, token);
                for (var i = 0; i < created.Receipts.Count; i++)
                {
                    await WriteAsync(store, FactoryPreviewPreparation.ReceiptKey(coordinator.OperationId, active, i),
                        created.Receipts[i], null, token);
                    receipts.Add(created.Receipts[i]);
                }
                await SaveEffectAsync(effects[active] with
                {
                    State = PreviewPreparationEffectState.Confirmed,
                    ReceiptHashes = created.Receipts.Select(FactoryModelsValidation.Hash).ToArray(),
                    CouncilEndpoint = created.CouncilEndpoint, DeploymentOutputsSha256 = created.OutputsSha256,
                }, token);
                nativeDispatched = false;
                active = -1;
                coordinator = coordinator with { ConfirmedEffects = effects.Count(e => e.State == PreviewPreparationEffectState.Confirmed) };
                await SaveRootAsync(token);
            }
            await RequireCurrentAsync(admission, plan, store, token, journal);
            await RequireHeldAsync();
            journal = new(plan, coordinator, root!, effects, effectRecords, receipts);
            await VerifyConfirmedAsync(journal, token);
            coordinator = coordinator with { State = PreviewPreparationState.ConfirmedNotReady, Owner = Guid.Empty };
            await SaveRootAsync(token);
            journal = new(plan, coordinator, root!, effects, effectRecords, receipts);
            return Result(journal, PreviewPreparationState.ConfirmedNotReady,
                "Exact native creation receipts confirmed. NOT READY: capability publication and execution seed remain unapproved; no evaluation or activation.");
        }
        catch (Exception e) when (e is ReservationStoreException or NativePreparationRejectedException or OperationCanceledException
            or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or HttpRequestException or IOException or OwnerReservationStore.AuthenticationFailedException
            or CharterAdoptionEvidenceException or CharterOperationException or PreviewCapabilityException or ModelCapabilityException)
        {
            var rejected = e is NativePreparationRejectedException;
            var uncertain = (nativeDispatched || active >= 0 && effects[active].State == PreviewPreparationEffectState.Dispatched) && !rejected
                || e is ReservationStoreException { State: FactoryReservationState.Unknown };
            var state = uncertain ? PreviewPreparationState.Unknown : rejected ? PreviewPreparationState.KnownFailed : FailureState(e);
            var reason = uncertain ? "NativePreparationOutcomeUnknown; retained intent forbids replay."
                : rejected ? "NativeProviderRejectedBeforeAcceptance; no native effect acknowledged; administrator action required."
                : e is ReservationStoreException stored ? stored.Message
                : e is OperationCanceledException ? "CanceledBeforeNextNativeDispatch; confirmed effects retained."
                : "CurrentPreviewPreparationAuthorityOrNativeReceiptUnconfirmed; provider details withheld.";
            if (root is not null && coordinator is not null && plan is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(plan.Policy.CleanupSeconds));
                try
                {
                    if (active >= 0 && effects[active].State == PreviewPreparationEffectState.Dispatched)
                        await SaveEffectAsync(effects[active] with
                        {
                            State = rejected ? PreviewPreparationEffectState.KnownFailed : PreviewPreparationEffectState.Unknown,
                            Reason = reason, Acknowledgment = acknowledgment ?? effects[active].Acknowledgment,
                        }, cleanup.Token);
                    var completeIntents = effects.Count == plan.Steps.Count;
                    coordinator = coordinator with { State = state, Reason = reason,
                        Owner = !uncertain && completeIntents ? Guid.Empty : coordinator.Owner };
                    await SaveRootAsync(cleanup.Token);
                }
                catch (Exception failure) when (failure is ReservationStoreException or OperationCanceledException)
                { state = PreviewPreparationState.Unknown; reason = "FinalNativePreparationAccountingUnconfirmed; inspect retained keys and original request; no replay."; }
            }
            return new(state, reason, plan, coordinator, effects, receipts);
        }

        async Task RequireHeldAsync()
        {
            var current = await store.ReadAsync(FactoryPreviewPreparation.RootKey, token);
            if (root is null || current is null || current.ETag != root.ETag || current.Value != root.Value
                || Record<PreviewPreparationCoordinator>(current).Owner != coordinator!.Owner)
                throw Unknown("NativePreparationCoordinatorOwnershipChanged");
        }
        async Task SaveRootAsync(CancellationToken pending) =>
            root = await WriteAsync(store, FactoryPreviewPreparation.RootKey, coordinator!, root, pending);
        async Task SaveEffectAsync(PreviewPreparationEffect value, CancellationToken pending)
        {
            var index = value.Index;
            effectRecords[index] = await WriteAsync(store, FactoryPreviewPreparation.EffectKey(value.OperationId, index),
                value, effectRecords[index], pending);
            effects[index] = value;
        }
    }

    private static PreviewPreparationResult Result(Journal journal, PreviewPreparationState state, string detail) =>
        new(state, detail, journal.Plan, journal.Coordinator, journal.Effects, journal.Receipts);
    private static PreviewPreparationState FailureState(Exception e) => e switch
    {
        ReservationStoreException { State: FactoryReservationState.Unknown } or CharterAdoptionEvidenceException => PreviewPreparationState.Unknown,
        ReservationStoreException { State: FactoryReservationState.Failed } => PreviewPreparationState.KnownFailed,
        OperationCanceledException or ReservationStoreException { State: FactoryReservationState.Canceled } => PreviewPreparationState.Canceled,
        PreviewCapabilityException { State: FactoryCharterState.Unknown } => PreviewPreparationState.Unknown,
        PreviewCapabilityException { State: FactoryCharterState.Canceled } => PreviewPreparationState.Canceled,
        PreviewCapabilityException { State: FactoryCharterState.Partial } => PreviewPreparationState.Partial,
        _ => PreviewPreparationState.Blocked,
    };
    private sealed class NativePreparationRejectedException : Exception;
}
