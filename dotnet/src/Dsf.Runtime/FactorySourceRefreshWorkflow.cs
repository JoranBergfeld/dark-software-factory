using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

/// <summary>CODE-owned descriptive work. Only the authenticated promoted council admits advances; reads never advance.</summary>
internal sealed partial class FactorySourceRefreshWorkflow(FactoryHostSettings settings, FactorySourceAccess source,
    HttpClient http, TimeProvider clock, Func<CancellationToken, Task<string>> readGovernanceRevision)
{
    private readonly SourceProgressStore store = new(source, http, "intent");
    private readonly GitHubSourceSnapshot github = new(source, clock, charterReview: true);
    private ConfigurationSetting? root;
    private SourceRefreshProgress progress = null!;
    private RetainedCharterAdoption adoption = null!;
    private SourceRefreshBinding binding = null!;

    internal async Task<SourceRefreshObservation> InspectAsync(Guid request, CancellationToken token)
    {
        await ReadBasisAsync(token);
        var records = await store.ReadNamespaceAsync(token);
        root = records.GetValueOrDefault(FactorySourceRefresh.CurrentKey);
        if (root is null && records.Keys.Any(k => k.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal)))
            throw Denied(SourceRefreshState.Unknown, "MissingOriginalRefreshCoordinator");
        if (root is not null)
        {
            progress = SourceProgressStore.Parse<SourceRefreshProgress>(root);
            ValidateProgress(records);
            if (!SameGovernance(progress.Binding, binding))
                return Observation(SourceRefreshState.ReviewRequired, "GoverningConfigurationChanged; explicit review required.");
            if (progress.Work is not null || progress.Dispatch is not null || progress.State == SourceRefreshState.Unknown)
                return Observation(SourceRefreshState.Unknown, "OriginalSourceRefreshWorkUnconfirmedNoTakeover");
            if (progress.Binding.Snapshot.Id == binding.Snapshot.Id)
            {
                binding = progress.Binding;
                return Observation(FactorySourceRefreshValidation.HasConfirmedRecovery(progress)
                    ? SourceRefreshState.Pending : progress.State, progress.Reason);
            }
            if (progress.State != SourceRefreshState.Compatible)
                return Observation(SourceRefreshState.ReviewRequired, "SourceMovedDuringRefresh; original partial evidence is historical.");
        }
        var analysis = SourceProgressStore.Parse<IntentProgress>(records.GetValueOrDefault("intent/current")
            ?? throw Denied(SourceRefreshState.Unknown, "OriginalAnalysisCheckpointRequired"));
        if (analysis.Dispatch is not null || analysis.PendingSelection is not null)
            return Observation(SourceRefreshState.Unknown, "OriginalAnalysisDispatchOrSelectionUnconfirmed");
        var adoptedSnapshot = analysis.Snapshot with { Commit = adoption.Adoption.SourceCommit,
            RootTree = adoption.Adoption.SourceTree, ObservedAt = binding.Snapshot.ObservedAt, Id = binding.Snapshot.Id };
        if (analysis.Revision == adoption.Adoption.AnalysisRevision && binding.Snapshot == adoptedSnapshot)
        {
            await github.CheckSnapshotAsync(analysis.Snapshot, token);
            return Observation(SourceRefreshState.Current, "OriginalSourceOrExactlyReconciledCharterOnlyMerge");
        }
        return Observation(SourceRefreshState.Pending, "CompleteCurrentSourceRefreshRequired");

        SourceRefreshObservation Observation(SourceRefreshState state, string reason) =>
            new(FactorySourceRefresh.Protocol, request, state, reason, binding, root is null ? null : Identity(root));
    }

    internal async Task<SourceRefreshReceipt> AdvanceAsync(SourceRefreshRequest request, CancellationToken token)
    {
        var observed = await InspectAsync(request.Preview.RequestId, token);
        if (request.Invocation == Guid.Empty || request.Admission == Guid.Empty || request.Worker == Guid.Empty
            || request.Preview.RequestId == Guid.Empty || request.Evaluation.FactoryId != source.Manifest.Identity.FactoryId
            || request.Evaluation.Store != AuditStoreKind.Preview
            || request.Evaluation.Key != $"dbs/{source.Manifest.Identity.ProductKey}/colls/{FactoryPreviewValidation.PreviewContainer}/docs/"
                + FactoryPromotion.DecisionId(source.Manifest.Identity.FactoryId, request.Invocation)
            || string.IsNullOrEmpty(request.Evaluation.NativeResourceId) || string.IsNullOrEmpty(request.Evaluation.NativeRevision)
            || !FactorySourceValidation.Hash(request.Evaluation.Sha256) || !SameBinding(request.Binding, binding))
            throw Denied(SourceRefreshState.Blocked, "ExactPromotedSourceWorkBindingRequired");
        _ = Dsf.Core.Runtime.NativeCosmosResourceId.RequireDocument(request.Evaluation.NativeResourceId);
        if (request.Continuation && root is null)
            throw Denied(SourceRefreshState.Unknown, "OriginalSourceRefreshRootMissingBeforeContinuation");
        var prior = await store.ReadAsync(FactorySourceRefresh.ReceiptKey(request.Preview.RequestId), token);
        if (prior is not null)
        {
            var receipt = SourceProgressStore.Parse<SourceRefreshReceipt>(prior);
            if (receipt.RequestSha256 != FactoryModelsValidation.Hash(request))
                throw Denied(SourceRefreshState.Unknown, "OriginalRefreshRequestChanged");
            return receipt;
        }
        if (observed.State != SourceRefreshState.Pending)
            throw Denied(observed.State, observed.Reason);
        source.BeforeWork = FenceWorkAsync;
        source.IsRefreshWork = true;
        var modelResponseReceived = false;
        var validatingModelOutput = false;
        try
        {
            if (root is null || progress.Binding.Snapshot.Id != binding.Snapshot.Id)
            {
                Guid? previous = null;
                if (root is not null)
                {
                    previous = progress.Id;
                    await store.WriteAsync(FactorySourceRefresh.HistoryKey(progress.Id), progress, null, token);
                }
                progress = new(1, Guid.NewGuid(), binding) { PreviousRefresh = previous };
                await store.WriteAsync(FactorySourceRefresh.InitialKey(progress.Id), progress, null, token);
                root = await store.WriteAsync(FactorySourceRefresh.CurrentKey, progress, root?.ETag, token);
            }
            progress = progress with { Work = request, State = SourceRefreshState.Pending, Recovery = null, ReadFailure = null };
            root = await store.WriteAsync(FactorySourceRefresh.CurrentKey, progress, root!.ETag, token);
            await github.CheckExactSnapshotAsync(progress.Binding.Snapshot, token);
            if (progress.Phase == SourceRefreshPhase.Acquisition)
            {
                var acquired = await new FactorySourceInventory(source, http, clock)
                    .AdvanceAsync(new(StartNewSnapshot: true), token);
                if (acquired.Progress?.Snapshot.Id != binding.Snapshot.Id)
                    throw Denied(SourceRefreshState.ReviewRequired, "SourceMovedDuringRefresh");
                if (acquired.State == SourceState.Acquired)
                    progress = progress with { Phase = SourceRefreshPhase.Analysis, Reason = "CompleteTreeAcquired; full analysis required." };
                else if (acquired.Progress is { NecessaryGaps: > 0 } || acquired.State is SourceState.Blocked or SourceState.Failed)
                    progress = progress with { State = SourceRefreshState.ReviewRequired, Reason = "NecessaryCurrentSourceUnavailable:" + acquired.Detail };
                else if (acquired.ReadFailure?.IsTransient() == true)
                    progress = progress with { State = SourceRefreshState.Blocked, Reason = "KnownTransientSourceRead; " + acquired.Detail,
                        Recovery = SourceRefreshRecovery.TransientSourceRead, ReadFailure = acquired.ReadFailure };
                else if (acquired.Progress?.Reason is not ("Pending" or "BoundedProgress"))
                    progress = progress with { State = SourceRefreshState.Partial, Reason = acquired.Detail };
                return await FinishAsync(request, token);
            }
            var workflow = new FactoryIntentWorkflow(source, http, clock);
            var current = SourceProgressStore.Parse<IntentProgress>(
                await store.ReadAsync("intent/current", token) ?? throw new JsonException());
            if (progress.Phase == SourceRefreshPhase.Analysis)
            {
                var result = await workflow.RunAsync(new(NewAnalysis: current.Snapshot.Id != binding.Snapshot.Id
                    && current.PendingSelection is null), false, token);
                if (result.Progress is null)
                {
                    progress = progress with { State = SourceRefreshState.ReviewRequired, Reason = "CompleteAnalysisImpactUnbounded:" + result.Detail };
                    return await FinishAsync(request, token);
                }
                if (result.Progress?.Snapshot.Id != binding.Snapshot.Id)
                    throw Denied(SourceRefreshState.ReviewRequired, "AnalysisSnapshotChangedDuringRefresh");
                if (result.State == IntentState.DraftReady)
                {
                    await RetainAsync(result.Progress, request, token);
                    progress = progress with { Phase = SourceRefreshPhase.Compatibility, AnalysisRevision = result.Progress.Revision,
                        AnalysisChain = result.Progress.ChainSha256, SourceIntegrity = result.Progress.SourceIntegritySha256,
                        Chunks = result.Progress.AvailableChunks, Reason = "CompleteAnalysisChecked; governing compatibility required." };
                }
                else if (result.State == IntentState.Unknown)
                    progress = progress with { State = SourceRefreshState.Unknown, Reason = "OriginalAnalysisModelDispatchUnconfirmedNoReplay" };
                else if (result.State == IntentState.Blocked
                    && result.Progress.LastFailure?.Reason.StartsWith("KnownNoModelDispatch;", StringComparison.Ordinal) == true
                    && result.Progress.ModelOutcome == IntentModelOutcome.KnownFailure && result.Progress.Dispatch is null
                    && result.Progress.Reason == result.Progress.LastFailure.Reason)
                    progress = progress with { State = SourceRefreshState.Blocked, Reason = result.Progress.LastFailure.Reason,
                        Recovery = SourceRefreshRecovery.KnownNoModelDispatch };
                else if (result.State == IntentState.Blocked
                    && result.Progress.ModelOutcome == IntentModelOutcome.KnownFailure && result.Progress.Dispatch is null
                    && result.Progress.LastFailure is { } rejected && FactorySourceRefreshValidation.HasConfirmedModelRejection(rejected)
                    && result.Progress.Reason == rejected.Reason)
                    progress = progress with { State = SourceRefreshState.Blocked, Reason = "KnownTransientModelRejection; " + rejected.Reason,
                        Recovery = SourceRefreshRecovery.RejectedModel, LastFailure = rejected };
                else if (result.State is IntentState.Blocked or IntentState.Failed || result.Progress?.ModelOutcome == IntentModelOutcome.KnownFailure)
                    progress = progress with { State = SourceRefreshState.ReviewRequired, Reason = "AnalysisRequiresReview:" + result.Detail };
                return await FinishAsync(request, token);
            }
            var review = await workflow.ReviewCharterAsync(token);
            var charter = await workflow.ReadReviewedCharterAsync(token);
            FactoryCharterAdoptionReader.RequireCurrent(adoption.Adoption, new(review, charter), source.Manifest.Identity.ProductKey);
            if (review.Progress!.Revision != progress.AnalysisRevision || review.Progress.ChainSha256 != progress.AnalysisChain
                || review.Progress.SourceIntegritySha256 != progress.SourceIntegrity || review.Progress.AvailableChunks != progress.Chunks)
                throw Denied(SourceRefreshState.ReviewRequired, "RefreshedAnalysisChangedBeforeCompatibility");
            await RetainAsync(review.Progress, request, token);
            var targets = FactorySourceCompatibility.Targets(review, adoption.Original.Evidence.Analysis);
            await ReadCompatibilityChainAsync(targets, token);
            var attempts = progress.CompatibilityAttempts ?? progress.CheckedChunks;
            if (attempts >= progress.Chunks + FactoryIntentLimits.MaximumAdditionalAttempts)
            {
                progress = progress with { State = SourceRefreshState.ReviewRequired, Reason = "CompatibilityAttemptLimit" };
                return await FinishAsync(request, token);
            }
            var chunk = await workflow.ReadReviewedChunkAsync(progress.CheckedChunks, token);
            var input = FactorySourceCompatibility.Prompt(adoption, review, chunk.Source, chunk.Content, chunk.Dependencies);
            if (input.Length > 32768)
            {
                progress = progress with { State = SourceRefreshState.ReviewRequired, Reason = "CompleteGoverningImpactExceedsAnalysisBudget" };
                return await FinishAsync(request, token);
            }
            var models = new FactoryIntentAccess(source, http, clock);
            await models.CheckAsync(token);
            var dispatch = new IntentDispatch(Guid.NewGuid(), progress.CheckedChunks, true, FactoryModelsValidation.Hash(input), models.Capability);
            progress = progress with { Dispatch = dispatch, CompatibilityAttempts = attempts + 1 };
            await SaveAsync(token);
            var response = await models.CompleteAsync(input, token);
            modelResponseReceived = true;
            await workflow.RevalidateCharterReviewAsync(review, token);
            validatingModelOutput = true;
            var resultCheck = FactoryIntentPrompts.Parse<SourceCompatibilityResult>(response.Text);
            FactorySourceCompatibility.Validate(resultCheck, targets, chunk.Content);
            validatingModelOutput = false;
            var step = new SourceCompatibilityStep(progress.Id, progress.AnalysisRevision.Value, dispatch,
                chunk.Source, chunk.Dependencies, progress.CompatibilityChain, response.Observation, resultCheck);
            var saved = await store.WriteAsync(FactorySourceRefresh.StepKey(progress.Id, progress.CheckedChunks), step, null, token);
            progress = progress with { Dispatch = null, CheckedChunks = progress.CheckedChunks + 1,
                CompatibilityChain = Commitment(saved), AddressedTargets = progress.AddressedTargets
                    .Concat(resultCheck.Judgments.Where(j => j.Verdict == SourceCompatibilityVerdict.Compatible).Select(j => j.Target)).Distinct().Order().ToArray() };
            if (!resultCheck.Complete || resultCheck.Judgments.Any(j => j.Verdict is SourceCompatibilityVerdict.Conflict or SourceCompatibilityVerdict.Unknown))
                progress = progress with { State = SourceRefreshState.ReviewRequired, Reason = "MaterialOrUnboundedGoverningIntentImpact" };
            else if (progress.CheckedChunks == progress.Chunks)
                progress = progress.AddressedTargets.Order().SequenceEqual(targets.Order())
                    ? progress with { State = SourceRefreshState.Compatible, Phase = SourceRefreshPhase.Complete,
                        Reason = "CompleteSourceCompatibleWithUnchangedAdoption" }
                    : progress with { State = SourceRefreshState.ReviewRequired, Reason = "NecessaryGoverningImpactRemainsUnresolved" };
            await github.CheckExactSnapshotAsync(binding.Snapshot, token);
            return await FinishAsync(request, token);
        }
        catch (IntentException e) when (modelResponseReceived && !validatingModelOutput)
        {
            throw new IntentException(IntentState.Unknown, "AcceptedCompatibilityResultCloseoutUnconfirmed; " + e.Message);
        }
        catch (IntentException e) when (source.ModelRequestIssued && progress.Dispatch is not null && e.State != IntentState.Unknown)
        {
            var failure = new IntentFailure(progress.Dispatch, e.State, e.Message)
            { ModelRejection = e.ModelRejection, ModelRejectionProof = e.ModelRejectionProof };
            var retryable = FactorySourceRefreshValidation.HasConfirmedModelRejection(failure);
            progress = progress with { State = retryable ? SourceRefreshState.Blocked
                    : e.State == IntentState.Failed ? SourceRefreshState.Failed : SourceRefreshState.ReviewRequired,
                Reason = retryable ? "KnownTransientModelRejection; " + e.Message : e.Message,
                LastFailure = failure, Dispatch = null,
                Recovery = retryable ? SourceRefreshRecovery.RejectedModel : null };
            return await FinishAsync(request, token);
        }
        catch (IntentException e) when (!source.ModelRequestIssued)
        {
            var analysis = await store.ReadAsync("intent/current", token);
            if (analysis is not null && SourceProgressStore.Parse<IntentProgress>(analysis).Dispatch is not null) throw;
            if (e.State is IntentState.Partial or IntentState.Failed)
            {
                progress = progress with { State = e.State == IntentState.Failed ? SourceRefreshState.Failed : SourceRefreshState.ReviewRequired,
                    Reason = e.Message, Dispatch = null };
                return await FinishAsync(request, token);
            }
            progress = progress with { State = SourceRefreshState.Blocked,
                Reason = "KnownNoModelDispatch; " + e.Message,
                Recovery = SourceRefreshRecovery.KnownNoModelDispatch,
                LastFailure = progress.Dispatch is { } unsent
                    ? new(unsent, IntentState.Blocked, "KnownNoModelDispatch") : progress.LastFailure, Dispatch = null };
            return await FinishAsync(request, token);
        }
        catch (SourceAcquisitionException e) when (e is not SourceProgressException && e.ReadFailure?.IsTransient() == true
            && !source.ModelRequestIssued && progress.Work == request && progress.Dispatch is null)
        {
            var analysis = await store.ReadAsync("intent/current", token);
            if (analysis is null || SourceProgressStore.Parse<IntentProgress>(analysis) is { Dispatch: not null } or { PendingSelection: not null })
                throw;
            progress = progress with { State = SourceRefreshState.Blocked, Reason = "KnownPreModelSourceRead; " + e.Message,
                Recovery = SourceRefreshRecovery.PreModelSourceRead, ReadFailure = e.ReadFailure };
            return await FinishAsync(request, token);
        }
        catch (SourceAcquisitionException e) when (e is not SourceProgressException
            && e.Message is "SourceIdentityChanged" or "RepositoryMetadataRace" or "CurrentCharterChanged"
                or "SourceChangedDuringCharterReconciliation")
        {
            // Preserve every original model dispatch. A moving snapshot is never a retry or a new compatible selection.
            var analysis = await store.ReadAsync("intent/current", token);
            var issued = progress.Dispatch is not null || analysis is not null && SourceProgressStore.Parse<IntentProgress>(analysis).Dispatch is not null;
            progress = progress with { State = issued ? SourceRefreshState.Unknown : SourceRefreshState.ReviewRequired,
                Reason = "SourceMovedDuringRefreshReviewRequired; original issued work is not replayed." };
            return await FinishAsync(request, token);
        }
        finally { source.BeforeWork = null; source.IsRefreshWork = false; }
    }

    private async Task ReadBasisAsync(CancellationToken token)
    {
        await source.CheckAsync(token);
        var records = await FactoryRuntimeModels.ReadPromotionRecordsAsync(
            settings.ProductEndpoint, source.Credential, http, clock.GetUtcNow(), token);
        _ = FactoryRuntimeModels.RequirePromotion(source.Manifest, settings.ProductEndpoint, records, clock.GetUtcNow());
        var governance = await readGovernanceRevision(token);
        if (!FactorySourceValidation.Hash(governance)) throw new JsonException();
        adoption = await FactoryCharterAdoptionReader.ReadAsync(source.Manifest.Identity.FactoryId,
            new AzureCharterEvidenceReader(settings.ProductEndpoint, source.Credential, http), token)
            ?? throw Denied(SourceRefreshState.Blocked, "CurrentAdoptedHumanCharterRequired");
        var snapshot = await github.PinAsync(token);
        var charter = await github.CurrentCharterAsync(snapshot, token);
        if (charter.Content is null || charter.Blob != adoption.Adoption.CharterBlob
            || FactoryModelsValidation.Hash(charter.Content) != adoption.Adoption.CharterSha256)
            throw Denied(SourceRefreshState.ReviewRequired, "HumanCharterChanged; exact explicit adoption required.");
        _ = OnboardingCharterMarkdown.Parse(charter.Content, source.Manifest.Identity.ProductKey);
        var model = FactoryModelsValidation.ReadPreparation(source.Manifest, source.Values);
        if (model.ApprovalFingerprint != adoption.Adoption.ModelRevision)
            throw Denied(SourceRefreshState.ReviewRequired, "ApprovedModelConfigurationChanged");
        binding = new(snapshot, adoption.Adoption.Revision, adoption.Revision, adoption.OperationRevision,
            adoption.Adoption.CharterSha256, governance, model.ApprovalFingerprint, adoption.Adoption.AnalysisRevision);
    }

    private async Task FenceWorkAsync(CancellationToken token)
    {
        var currentGovernance = await readGovernanceRevision(token);
        if (currentGovernance != binding.GovernanceRevision)
            throw Denied(SourceRefreshState.Blocked, "CurrentSourceRefreshAdmissionClosedOrChanged");
        var current = await store.ReadAsync(FactorySourceRefresh.CurrentKey, token);
        if (root is null ? current is not null : current?.ETag != root.ETag || current.Value != root.Value)
            throw Denied(SourceRefreshState.Unknown, "OriginalSourceRefreshWriterChangedNoTakeover");
    }

    private void ValidateProgress(IReadOnlyDictionary<string, ConfigurationSetting> records)
    {
        if (progress.Recovery == SourceRefreshRecovery.RejectedModel
            && !FactorySourceRefreshValidation.HasConfirmedModelRejection(progress.LastFailure))
            throw Denied(SourceRefreshState.Unknown, "OriginalModelRejectionProvenanceUnconfirmedNoReplay");
        if (progress.Protocol != 1 || progress.Id == Guid.Empty || !Enum.IsDefined(progress.State) || !Enum.IsDefined(progress.Phase)
            || progress.Recovery is not null && !FactorySourceRefreshValidation.HasConfirmedRecovery(progress)
            || !FactorySourceRefreshValidation.HasBoundedCompatibilityAttempts(progress)
            || progress.CheckedChunks < 0 || progress.CheckedChunks > progress.Chunks || progress.Chunks > FactoryIntentLimits.MaximumChunks
            || !records.TryGetValue(FactorySourceRefresh.InitialKey(progress.Id), out var initial)
            || SourceProgressStore.Parse<SourceRefreshProgress>(initial) is not { } original
            || original.Id != progress.Id || original.PreviousRefresh != progress.PreviousRefresh || !SameBinding(original.Binding, progress.Binding)
            || progress.PreviousRefresh is { } previous && !records.ContainsKey(FactorySourceRefresh.HistoryKey(previous)))
            throw Denied(SourceRefreshState.Unknown, "OriginalSourceRefreshLineageUnconfirmed");
    }

    private async Task RetainAsync(IntentProgress analysis, SourceRefreshRequest request, CancellationToken token)
    {
        var key = FactoryAuditRetention.EvidenceKey(analysis.Revision);
        var retained = await store.ReadAsync(key, token);
        if (retained is null)
            retained = await store.WriteAsync(key, new AnalysisEvidenceClaim(1, source.Manifest.Identity.FactoryId, analysis.Revision,
                analysis.ChainSha256!, AnalysisRetentionKind.Protected, progress.Id, FactoryModelsValidation.Hash(request),
                2 * analysis.AvailableChunks), null, token);
        var claim = SourceProgressStore.Parse<AnalysisEvidenceClaim>(retained);
        FactoryAuditRetention.RequireEvidence(claim, source.Manifest.Identity.FactoryId, analysis.Revision, analysis.ChainSha256!, 2 * analysis.AvailableChunks);
        if (claim.Kind != AnalysisRetentionKind.Protected)
            throw Denied(SourceRefreshState.Blocked, "RefreshedAnalysisExpiryAlreadyAdmitted");
    }

    private async Task ReadCompatibilityChainAsync(IReadOnlyList<string> targets, CancellationToken token)
    {
        string? chain = null;
        var addressed = new HashSet<string>();
        for (var i = 0; i < progress.CheckedChunks; i++)
        {
            var record = await store.ReadAsync(FactorySourceRefresh.StepKey(progress.Id, i), token) ?? throw new JsonException();
            var step = SourceProgressStore.Parse<SourceCompatibilityStep>(record);
            if (step.Refresh != progress.Id || step.Analysis != progress.AnalysisRevision || step.PreviousSha256 != chain
                || step.Source.SnapshotId != binding.Snapshot.Id || step.Dispatch.Index != i || !step.Result.Complete
                || step.Result.Judgments.Any(j => j.Verdict is SourceCompatibilityVerdict.Conflict or SourceCompatibilityVerdict.Unknown)
                || !step.Result.Judgments.Select(j => j.Target).Order().SequenceEqual(targets.Order()))
                throw Denied(SourceRefreshState.Unknown, "OriginalCompatibilityChainUnconfirmed");
            addressed.UnionWith(step.Result.Judgments.Where(j => j.Verdict == SourceCompatibilityVerdict.Compatible).Select(j => j.Target));
            chain = Commitment(record);
        }
        if (chain != progress.CompatibilityChain || !addressed.SetEquals(progress.AddressedTargets))
            throw Denied(SourceRefreshState.Unknown, "OriginalCompatibilityCheckpointUnconfirmed");
    }

    private async Task<SourceRefreshReceipt> FinishAsync(SourceRefreshRequest request, CancellationToken token)
    {
        if (progress.State is SourceRefreshState.Pending or SourceRefreshState.Compatible)
            await github.CheckExactSnapshotAsync(binding.Snapshot, token);
        progress = progress with { Work = null, LastRequest = request.Preview.RequestId };
        var checkpoint = await store.WriteAsync(FactorySourceRefresh.CheckpointKey(request.Preview.RequestId), progress, null, token);
        await SaveAsync(token);
        var receipt = new SourceRefreshReceipt(request.Preview.RequestId, FactoryModelsValidation.Hash(request), progress.State, progress.Reason, Identity(checkpoint));
        await store.WriteAsync(FactorySourceRefresh.ReceiptKey(request.Preview.RequestId), receipt, null, token);
        return receipt;
    }
    private async Task SaveAsync(CancellationToken token) =>
        root = await store.WriteAsync(FactorySourceRefresh.CurrentKey, progress, root!.ETag, token);
    private AuditRecordIdentity Identity(ConfigurationSetting record) =>
        new(source.Manifest.Identity.FactoryId, AuditStoreKind.Analysis, record.Key, record.ETag.ToString(), FactoryAudit.TextHash(record.Value));
    private static string Commitment(ConfigurationSetting record) =>
        FactoryModelsValidation.Hash(new { record.Key, record.Value, ETag = record.ETag.ToString() });
    internal static bool SameBinding(SourceRefreshBinding first, SourceRefreshBinding second) =>
        SameGovernance(first, second) && first.Snapshot with { ObservedAt = second.Snapshot.ObservedAt } == second.Snapshot;
    private static bool SameGovernance(SourceRefreshBinding first, SourceRefreshBinding second) =>
        first with { Snapshot = second.Snapshot } == second;
    private static PreviewCapabilityException Denied(SourceRefreshState state, string reason) =>
        new(state switch { SourceRefreshState.Unknown => FactoryCharterState.Unknown, SourceRefreshState.Partial => FactoryCharterState.Partial,
            SourceRefreshState.Failed => FactoryCharterState.Failed, _ => FactoryCharterState.Blocked }, reason);
}
