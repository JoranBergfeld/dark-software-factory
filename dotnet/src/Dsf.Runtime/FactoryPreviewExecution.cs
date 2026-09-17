using Azure.Identity;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>Recorded evaluation, not current readiness or activation authority.</summary>
public sealed record PreviewExecutionResult(PreviewExecutionPhase State, PreviewInputSnapshot Inputs,
    ConveyorRun Run, PreviewRunReceipt Receipt, IReadOnlyList<PreviewModelDispatch> Dispatches)
{
    public PreviewReadiness Readiness { get; init; }
    public PreviewReadinessProof? Proof { get; init; }
    public string? Reason { get; init; }
}

public static partial class RuntimeVerbs
{
    public static Task<PreviewExecutionResult> ExecutePreviewAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.ExecutePreviewAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token);
    public static Task<PreviewExecutionResult> InspectPreviewAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.InspectPreviewAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token);
}

internal static partial class RuntimeWorkflow
{
    internal static async Task<PreviewExecutionResult> ExecutePreviewAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, HttpClient http, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FactoryPreviewExecution.Policy.RequestSeconds), dependencies.PreviewClock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = pending.Token;
        var credential = dependencies.PreviewCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
        var authority = await new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, dependencies.PreviewClock).CheckAsync(request, token);
        if (authority.Capability.Configuration.Execution is null)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewExecutionSeedRequired");
        var store = new PreviewExecutionStore(authority, credential, http, dependencies.PreviewClock);
        var run = await store.ReadAsync(token);
        if (run is not null && store.Document.PendingRun is null && (store.Document.Phase == PreviewExecutionPhase.Failed
            || store.Document.Phase == PreviewExecutionPhase.Evaluated && store.Document.Proof is not null))
            return await InspectPreviewAsync(settings, dependencies, request, http, token);
        var acquired = await AcquirePreviewInputsAsync(settings, dependencies, store.Document.Inputs?.Request ?? request, http, token);
        if (acquired.Snapshot.CapabilityRevision != authority.Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedBeforeExecution");
        var inputs = store.Document.Inputs ?? acquired.Snapshot;
        ValidateCapturedInputs(inputs, acquired.Snapshot, dependencies.PreviewClock.GetUtcNow());
        await store.ClaimAsync(run, token);
        if (store.Document.Inputs is null) await store.CaptureAsync(inputs, token);
        acquired.Models.AttachJournal(store);
        var sourceKinds = new[] { "azuremonitor" }.Concat(inputs.Supplemental.Sources.Select(s => s.Evidence.Proof.Kind)).ToArray();
        if (run is null)
        {
            run = new ConveyorRun { Id = store.RunId, Profile = ConveyorProfile.OnboardingPreview, DryRun = true,
                EvaluationContext = inputs.Context, EvaluationRevision = FactoryModelsValidation.Hash(inputs.Context),
                ProductHints = [inputs.Manifest.Identity.ProductKey], SourceKinds = sourceKinds };
            await store.SaveAsync(run, "evaluation:initialized", token);
        }
        var services = PreviewServices(inputs, run, acquired.Models, store);
        await ConveyorLine.RunAsync(run, services, token);
        await store.SaveAsync(run, "evaluation:complete", token);
        await store.FinishAsync(run, token);
        if (run.Status is RunStatus.Previewed or RunStatus.Killed or RunStatus.Escalated)
        {
            IReadOnlyList<PreviewCandidateProof> candidates;
            try { candidates = OnboardingReadiness.ValidateComplete(run, services, store.Document.Dispatches); }
            catch (InvalidOperationException)
            { return Report(store, run) with { Reason = "CompleteNativeEvaluationRequired" }; }
            var final = await AcquirePreviewInputsAsync(settings, dependencies, inputs.Request, http, token);
            ValidateCapturedInputs(inputs, final.Snapshot, dependencies.PreviewClock.GetUtcNow());
            await store.ProveAsync(final.Snapshot, candidates, token);
            return await InspectPreviewAsync(settings, dependencies, request, http, token);
        }
        return Report(store, run) with { Reason = "EvaluationIncompleteOrFailed" };
    }

    internal static async Task<PreviewExecutionResult> InspectPreviewAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, HttpClient http, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FactoryPreviewInputValidation.Policy.RequestSeconds), dependencies.PreviewClock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = pending.Token;
        var credential = dependencies.PreviewCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
        var authority = await new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, dependencies.PreviewClock).CheckAsync(request, token);
        var store = new PreviewExecutionStore(authority, credential, http, dependencies.PreviewClock);
        var run = await store.ReadAsync(token) ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewEvaluationNotStarted");
        var report = Report(store, run);
        if (store.Document.Dispatches.Any(d => d.State is PreviewModelDispatchState.Unknown or PreviewModelDispatchState.Dispatched)
            || store.Document.PendingRun is not null)
            return report with { Readiness = PreviewReadiness.Unknown, Reason = "PreviewDispatchOrCheckpointUnconfirmed" };
        if (store.Document.Proof is not { } proof) return report with { Reason = "CompleteDurableReadinessProofRequired" };
        try
        {
            var current = await AcquirePreviewInputsAsync(settings, dependencies, report.Inputs.Request, http, token);
            ValidateCapturedInputs(report.Inputs, current.Snapshot, dependencies.PreviewClock.GetUtcNow());
            var candidates = OnboardingReadiness.ValidateComplete(run, PreviewServices(report.Inputs, run, current.Models, store), report.Dispatches);
            if (FactoryModelsValidation.Hash(candidates) != FactoryModelsValidation.Hash(proof.Candidates))
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "DurableCandidateProofChanged");
            var finalStore = new PreviewExecutionStore(authority, credential, http, dependencies.PreviewClock);
            _ = await finalStore.ReadAsync(token);
            if (finalStore.Document.Checksum != store.Document.Checksum)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "DurablePreviewStateChangedDuringInspection");
            var now = dependencies.PreviewClock.GetUtcNow();
            ValidateCapturedInputs(report.Inputs, current.Snapshot, now);
            FactoryPreviewTelemetryValidation.ValidateCapture(authority.Telemetry!, current.Snapshot.TelemetrySelection,
                current.Snapshot.Request.RequestId, current.Snapshot.Telemetry.Capture, now);
            foreach (var selected in current.Snapshot.Supplemental.Sources)
                FactoryPreviewSupplementalValidation.ValidateEvidence(selected.Authority, selected.Evidence, now);
            token.ThrowIfCancellationRequested();
            return report with { Readiness = PreviewReadiness.ReadyAwaitingActivation };
        }
        catch (PreviewCapabilityException exception)
        {
            return report with { Readiness = exception.State switch
            {
                FactoryCharterState.Canceled => PreviewReadiness.Canceled, FactoryCharterState.Unknown => PreviewReadiness.Unknown,
                FactoryCharterState.Partial => PreviewReadiness.Partial, FactoryCharterState.Failed => PreviewReadiness.Failed,
                _ => PreviewReadiness.Blocked,
            }, Reason = exception.Message };
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException or ArgumentException or KeyNotFoundException)
        { return report with { Readiness = PreviewReadiness.Unknown, Reason = "DurablePreviewEvaluationUnconfirmed" }; }
    }

    private static PreviewExecutionResult Report(PreviewExecutionStore store, ConveyorRun run) =>
        new(store.Document.Phase, store.Document.Inputs ?? throw new InvalidOperationException("Captured inputs required."),
            run, store.Document.Run ?? throw new InvalidOperationException("Confirmed native run required."), store.Document.Dispatches)
        { Proof = store.Document.Proof, Readiness = store.Document.PendingRun is not null
            || store.Document.Dispatches.Any(d => d.State is PreviewModelDispatchState.Dispatched or PreviewModelDispatchState.Unknown)
                ? PreviewReadiness.Unknown : store.Document.Phase == PreviewExecutionPhase.Failed ? PreviewReadiness.Failed : PreviewReadiness.NotReady };

    private static ConveyorServices PreviewServices(PreviewInputSnapshot inputs, ConveyorRun run, IFactoryPreviewModelWorkloads models, PreviewExecutionStore store)
    {
        var context = new PreviewConveyorContext(inputs, run, models);
        var lenses = inputs.Context.Judgment.Lenses.Select(l => l.Name switch
        {
            "value" => ModelDeliberationLens.Value(l.Weight), "cost" => ModelDeliberationLens.Cost(l.Weight),
            "feasibility" => ModelDeliberationLens.Feasibility(l.Weight), "security" => ModelDeliberationLens.Security(l.Weight),
            "strategic-fit" => ModelDeliberationLens.StrategicFit(l.Weight), _ => throw new InvalidOperationException("Unsupported approved lens."),
        }).ToArray();
        return new ConveyorServices(inputs.Manifest.Identity.ProductKey,
            run.SourceKinds.Select(kind => (IEvidenceGatherer)new CapturedPreviewEvidence(kind, inputs)).ToArray(),
            null, store, context, context, context, DeliberationLenses: lenses, ProductMaturity: "low",
            DeliberationRounds: inputs.Context.Judgment.Rounds, JuryTimeout: TimeSpan.FromSeconds(FactoryPreviewExecution.Policy.JurorSeconds),
            ProblemIdentityResolver: context, EvaluationModels: models);
    }

    private static void ValidateCapturedInputs(PreviewInputSnapshot captured, PreviewInputSnapshot current, DateTimeOffset now)
    {
        if (captured.Revision != captured.CalculateRevision() || captured.CapabilityRevision != current.CapabilityRevision
            || SourceRevision(captured.Source) != SourceRevision(current.Source)
            || captured.Telemetry.SourceAuthorityRevision != current.Telemetry.SourceAuthorityRevision
            || !SupplementalRevisions(captured.Supplemental).OrderBy(p => p.Key).SequenceEqual(SupplementalRevisions(current.Supplemental).OrderBy(p => p.Key))
            || FactoryModelsValidation.Hash(captured.Supplemental.Sources.Select(s => s.Evidence.Items))
                != FactoryModelsValidation.Hash(current.Supplemental.Sources.Select(s => s.Evidence.Items)))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CapturedPreviewInputsChanged");
        foreach (var row in captured.Telemetry.Capture.Rows)
            if (row.Reference.RecordTime < now.AddSeconds(-captured.TelemetryConfiguration.FreshnessSeconds) || row.Reference.RecordTime > now)
                throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CapturedApplicationEvidenceStale");
    }

    private sealed class CapturedPreviewEvidence(string sourceKind, PreviewInputSnapshot inputs) : IEvidenceGatherer
    {
        public string SourceKind => sourceKind;
        public Task<IReadOnlyList<EvidenceItem>> GatherAsync(ConveyorRun run, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!run.DryRun || run.Profile != ConveyorProfile.OnboardingPreview
                || FactoryModelsValidation.Hash(run.EvaluationContext) != FactoryModelsValidation.Hash(inputs.Context))
                throw new InvalidOperationException("Exact captured preview context required.");
            return Task.FromResult<IReadOnlyList<EvidenceItem>>(inputs.Observations.Where(o => o.SourceKind == sourceKind)
                .Select(o => new EvidenceItem(o.SourceKind, o.Reference, o.Summary)).ToArray());
        }
    }

    private sealed class PreviewConveyorContext(PreviewInputSnapshot inputs, ConveyorRun run, IFactoryModelWorkloads models)
        : IModelClient, ITracer, IConfidenceThresholdReader, IProblemIdentityResolver
    {
        public async Task<string> CompleteAsync(string prompt, CancellationToken token) => (await models.SynthesizeAsync(prompt, token)).Text;
        public Task<double> ReadThresholdAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(inputs.Context.Judgment.Threshold);
        }
        public Task TraceAsync(string name, IReadOnlyDictionary<string, string?> properties, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!run.DryRun || run.Profile != ConveyorProfile.OnboardingPreview) throw new InvalidOperationException("Preview-only audit required.");
            run.Record("trace", name);
            return Task.CompletedTask;
        }
        public Task<string> ResolveAsync(string scope, EvidenceCluster cluster, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(FactoryModelsValidation.Hash(new { inputs.Manifest.Identity.FactoryId, inputs.Capability.OperationId,
                References = cluster.Evidence.Select(e => e.Reference).ToArray() }));
        }
    }
}
