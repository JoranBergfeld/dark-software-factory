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
    private sealed record Completion(PreviewHttpObservation Observation, bool Inputs, string AuthorityRevision, DateTimeOffset ExpiresAt);
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
                    execute = false;
                }
                if (!execute && store.Document.Run is null)
                    return Observation(authority, request, store.Document.Admission?.Readiness ?? PreviewReadiness.NotReady,
                        store.Document.Admission?.Reason ?? "PreviewEvaluationNotStarted");
            }
            if (active is not null)
                return Observation(authority, request, PreviewReadiness.Checking, "CurrentReadOnlyInspectionInProgress");
            if (completed is { } cached && cached.Inputs == inputs && cached.AuthorityRevision == authority.Revision
                && cached.ExpiresAt > dependencies.PreviewClock.GetUtcNow() && cached.Observation is { } previous && previous.RequestId == request.RequestId
                && previous.FactoryId == authority.Manifest.Identity.FactoryId && previous.OperationId == request.OperationId
                && previous.ApprovalFingerprint == request.ApprovalFingerprint)
                return previous;
            if (execute && store is not null && store.Document.Proof is null
                && store.Document.Phase != PreviewExecutionPhase.Failed)
            {
                var admission = await store.AdmitAsync(workerId, request, token);
                // Only a confirmed CAS permits work. Request cancellation after this point is not rollback.
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
        DateTimeOffset? evidenceExpires = null;
        PreviewHttpObservation outcome;
        try
        {
            if (work.Admission is not null)
            {
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
                await store.EndWorkAsync(work.Admission, outcome.Readiness, outcome.Reason, began && !token.IsCancellationRequested, cleanup.Token);
            }
            catch (Exception exception) when (Unconfirmed(exception) || exception is PreviewCapabilityException)
            {
                logger.LogWarning("Original preview worker outcome could not be confirmed; operation {OperationId} remains quarantined.", work.Request.OperationId);
                outcome = Observation(work.Authority, work.Request, PreviewReadiness.Unknown, "PreviewWorkerReleaseUnconfirmedNoTakeover");
            }
        }
        var expires = outcome.ObservedAt + FactoryPreviewValidation.RequestBudget;
        return new(outcome, work.Inputs, work.Authority.Revision,
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
