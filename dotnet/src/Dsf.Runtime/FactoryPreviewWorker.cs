using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

internal sealed class FactoryPreviewWorker(FactoryHostSettings settings, RuntimeDependencies dependencies, HttpClient http,
    ILogger<FactoryPreviewWorker> logger) : BackgroundService
{
    private sealed record Work(PreviewCapabilitySnapshot Authority, PreviewCapabilityRequest Request, PreviewWorkAdmission? Admission, bool Inputs);
    private sealed record Completion(PreviewHttpObservation Observation, bool Inputs, string AuthorityRevision, string? ExecutionRevision, DateTimeOffset ExpiresAt);
    private readonly Guid workerId = Guid.NewGuid();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(1) { SingleReader = true });
    private Work? active;
    private Completion? completed;
    private bool stopping;
    private readonly TokenCredential credential = dependencies.PreviewCredential
        ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
    private static readonly JsonSerializerOptions Json = new(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false };

    internal async Task<PreviewHttpObservation> ObserveAsync(PreviewCapabilitySnapshot authority, PreviewCapabilityRequest request,
        bool execute, bool inputs, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token))
            return Observation(authority, request, PreviewReadiness.Checking, "BoundedPreviewAdmissionBusy");
        try
        {
            if (stopping) return Observation(authority, request, PreviewReadiness.Unknown, "PreviewWorkerStopping");
            PreviewExecutionStore? store = null;
            var unconfirmedAdmission = false;
            if (!inputs)
            {
                store = Store(authority);
                await store.ReadDocumentAsync(token);
                if (store.Document.Admission is { State: PreviewWorkState.Queued or PreviewWorkState.Running } admission)
                    return Observation(authority, request, active?.Admission?.Id == admission.Id && admission.WorkerId == workerId
                        ? admission.State == PreviewWorkState.Queued ? PreviewReadiness.Queued : PreviewReadiness.Running : PreviewReadiness.Unknown,
                        active?.Admission?.Id == admission.Id ? "OriginalPreviewWorkContinues" : "OriginalPreviewWorkerUnconfirmedNoTakeover");
                if (store.Document.Admission is { State: PreviewWorkState.Unknown } unknown)
                {
                    if (store.Document.Run is null)
                        return Observation(authority, request, PreviewReadiness.Unknown, unknown.Reason ?? "OriginalPreviewWorkUnconfirmedNoReplay");
                    unconfirmedAdmission = true;
                    execute = false;
                }
                if (!execute && store.Document.Run is null)
                    return Observation(authority, request, store.Document.Admission?.Readiness ?? PreviewReadiness.NotReady,
                        store.Document.Admission?.Reason ?? "PreviewEvaluationNotStarted");
            }
            if (active is not null)
                return Observation(authority, request, PreviewReadiness.Checking, "CurrentReadOnlyInspectionInProgress");
            if (completed is { } prior && prior.Inputs == inputs && prior.Observation.RequestId == request.RequestId
                && prior.Observation.FactoryId == authority.Manifest.Identity.FactoryId && prior.Observation.OperationId == request.OperationId
                && prior.Observation.ApprovalFingerprint == request.ApprovalFingerprint && prior.AuthorityRevision != authority.Revision)
                return Observation(authority, request, PreviewReadiness.Unknown, "PreviewGovernanceChangedDuringObservation");
            if (completed is { } cached && cached.Inputs == inputs && cached.AuthorityRevision == authority.Revision
                && cached.Observation is { } previous
                && previous.FactoryId == authority.Manifest.Identity.FactoryId && previous.OperationId == request.OperationId
                && previous.ApprovalFingerprint == request.ApprovalFingerprint)
            {
                if (!inputs && !unconfirmedAdmission && previous.Readiness == PreviewReadiness.ReadyAwaitingActivation)
                {
                    if (cached.ExecutionRevision != store!.Revision)
                        return Observation(authority, request, PreviewReadiness.Unknown, "PreviewEvaluationObservationChanged");
                    _ = await store.ReadAsync(token);
                    if (cached.ExecutionRevision != store.Revision)
                        return Observation(authority, request, PreviewReadiness.Unknown, "PreviewEvaluationObservationChanged");
                }
                if (cached.ExpiresAt > dependencies.PreviewClock.GetUtcNow() && previous.RequestId == request.RequestId
                    && (!unconfirmedAdmission || previous.Readiness == PreviewReadiness.Unknown
                        && (cached.ExecutionRevision is null || previous.Evaluation is not null && cached.ExecutionRevision == store!.Revision)))
                    return previous;
            }
            if (execute && store is not null && store.Document.Proof is null
                && store.Document.Phase != PreviewExecutionPhase.Failed)
            {
                await RuntimeWorkflow.RequireCurrentPreviewAdmissionAsync(settings, authority, request, credential,
                    http, dependencies.PreviewClock, token);
                var admission = await store.AdmitAsync(workerId, request, token);
                try
                {
                    await RuntimeWorkflow.RequireCurrentPreviewAdmissionAsync(settings, authority, request, credential,
                        http, dependencies.PreviewClock, token);
                }
                catch (Exception exception) when (exception is PreviewCapabilityException || Unconfirmed(exception))
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), dependencies.PreviewClock);
                    await store.QuarantineWorkAsync(admission, "PreviewAdmissionAuthorityUnconfirmedNoDispatch", cleanup.Token);
                    return Observation(authority, request, PreviewReadiness.Unknown, "PreviewAdmissionAuthorityUnconfirmedNoDispatch");
                }
                // The CAS and governance reads are not a cross-store transaction. Later stop requests may drain admitted work.
                active = new(authority, admission.Request, admission, false);
                if (!queue.Writer.TryWrite(active)) throw new InvalidOperationException("Bounded preview worker unavailable.");
                return Observation(authority, request, PreviewReadiness.Queued, "DurablyAdmittedAwaitingOriginalWorker");
            }
            active = new(authority, request, null, inputs);
            if (!queue.Writer.TryWrite(active)) throw new InvalidOperationException("Bounded preview reader unavailable.");
            return Observation(authority, request, PreviewReadiness.Checking, "ReadOnlyCurrentInspectionPending");
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var outcome = await RunAsync(work, stoppingToken);
                await gate.WaitAsync(CancellationToken.None);
                try { completed = outcome; active = null; }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try { stopping = true; }
            finally { gate.Release(); }
        }
    }

    private async Task<Completion> RunAsync(Work work, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(work.Admission is null
            ? FactoryPreviewInputValidation.Policy.RequestSeconds : FactoryPreviewExecution.Policy.RequestSeconds), dependencies.PreviewClock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        token = pending.Token;
        var began = false;
        string? executionRevision = null;
        DateTimeOffset? evidenceExpires = null;
        PreviewHttpObservation outcome;
        try
        {
            if (work.Admission is not null)
            {
                await RuntimeWorkflow.RequireCurrentPreviewAdmissionAsync(settings, work.Authority, work.Request, credential,
                    http, dependencies.PreviewClock, token);
                var store = Store(work.Authority);
                await store.ReadDocumentAsync(token);
                await store.BeginWorkAsync(work.Admission, token);
                began = true;
            }
            if (work.Inputs)
            {
                var inputs = await RuntimeWorkflow.AcquirePreviewInputsAsync(settings, dependencies, work.Request, http, token);
                evidenceExpires = EvidenceExpires(inputs.Snapshot);
                outcome = Observation(work.Authority, work.Request, PreviewReadiness.NotReady, "CurrentInputsCapturedNotReadiness",
                    JsonSerializer.SerializeToElement(inputs.Snapshot, Json));
            }
            else
            {
                var report = work.Admission is null
                    ? await RuntimeWorkflow.InspectPreviewAsync(settings, dependencies, work.Request, http, token)
                    : await RuntimeWorkflow.ExecutePreviewAsync(settings, dependencies, work.Request, http, token, work.Admission);
                evidenceExpires = EvidenceExpires(report.Inputs);
                executionRevision = report.ExecutionRevision;
                outcome = Observation(work.Authority, work.Request, report.Readiness, report.Reason, JsonSerializer.SerializeToElement(report, Json));
            }
        }
        catch (Exception exception) when (exception is PreviewCapabilityException or OperationCanceledException && deadline.IsCancellationRequested)
        {
            outcome = Observation(work.Authority, work.Request, PreviewReadiness.Unknown,
                work.Admission is null ? "PreviewInputCollectionDeadlineExceeded" : "PreviewEvaluationDeadlineExceeded");
        }
        catch (PreviewCapabilityException exception)
        {
            outcome = Observation(work.Authority, work.Request, exception.State switch
            {
                FactoryCharterState.Blocked => PreviewReadiness.Blocked,
                FactoryCharterState.Partial => PreviewReadiness.Partial,
                FactoryCharterState.Failed => PreviewReadiness.Failed,
                _ => PreviewReadiness.Unknown
            }, exception.Message);
        }
        catch (Exception exception) when (Unconfirmed(exception))
        { outcome = Observation(work.Authority, work.Request, PreviewReadiness.Unknown, "PreviewWorkerOutcomeUnconfirmedNoReplay"); }
        if (work.Admission is not null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), dependencies.PreviewClock);
            try
            {
                var store = Store(work.Authority);
                await store.ReadDocumentAsync(cleanup.Token);
                var release = await store.EndWorkAsync(work.Admission, outcome.Readiness, outcome.Reason, executionRevision, began, token, cleanup.Token);
                if (release.Readiness != outcome.Readiness || release.Reason != outcome.Reason)
                    outcome = Observation(work.Authority, work.Request, release.Readiness, release.Reason);
                executionRevision = store.Revision;
            }
            catch (Exception exception) when (Unconfirmed(exception) || exception is PreviewCapabilityException)
            {
                logger.LogWarning("Original preview worker release could not be confirmed; operation {OperationId} must not be replayed.", work.Request.OperationId);
                outcome = Observation(work.Authority, work.Request, PreviewReadiness.Unknown, "PreviewWorkerReleaseUnconfirmedNoTakeover");
                using var quarantine = new CancellationTokenSource(TimeSpan.FromSeconds(30), dependencies.PreviewClock);
                try
                {
                    var current = Store(work.Authority);
                    await current.ReadDocumentAsync(quarantine.Token);
                    await current.QuarantineWorkAsync(work.Admission, outcome.Reason!, quarantine.Token);
                    executionRevision = current.Revision;
                }
                catch (Exception failure) when (Unconfirmed(failure) || failure is PreviewCapabilityException)
                {
                    logger.LogWarning("Native preview quarantine could not be confirmed; operation {OperationId} requires its original authority.", work.Request.OperationId);
                }
            }
        }
        var expires = outcome.ObservedAt + FactoryPreviewValidation.RequestBudget;
        return new(outcome, work.Inputs, work.Authority.Revision, executionRevision,
            evidenceExpires is { } until && outcome.Readiness == PreviewReadiness.ReadyAwaitingActivation && until < expires ? until : expires);
    }

    private PreviewExecutionStore Store(PreviewCapabilitySnapshot authority) => new(authority, credential, http, dependencies.PreviewClock);
    private PreviewHttpObservation Observation(PreviewCapabilitySnapshot authority, PreviewCapabilityRequest request,
        PreviewReadiness readiness, string? reason, JsonElement? evaluation = null) =>
        new(2, authority.Manifest.Identity.FactoryId, request.OperationId, request.RequestId, request.ApprovalFingerprint,
            dependencies.PreviewClock.GetUtcNow(), readiness, reason, evaluation);
    private static DateTimeOffset EvidenceExpires(PreviewInputSnapshot inputs) =>
        inputs.Telemetry.Capture.Rows.Min(row => row.Reference.RecordTime.AddSeconds(inputs.TelemetryConfiguration.FreshnessSeconds));
    private static bool Unconfirmed(Exception exception) => exception is OperationCanceledException or HttpRequestException or IOException
        or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or Azure.RequestFailedException
        or AuthenticationFailedException or ModelCapabilityException or SupplementalSourceException or CosmosCheckpointException;
}
