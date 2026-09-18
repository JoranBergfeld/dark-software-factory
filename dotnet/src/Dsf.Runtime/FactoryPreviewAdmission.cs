using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class PreviewExecutionStore
{
    private const int AdmissionLifecycleBytes = 4096;

    internal async Task<PreviewWorkAdmission> AdmitAsync(Guid worker, PreviewCapabilityRequest request, CancellationToken token)
    {
        if (Document.Admission is { State: not PreviewWorkState.Released }
            || Document.Admission is null && Document.Phase != PreviewExecutionPhase.Prepared
            || Document.PendingRun is not null
            || Document.Dispatches.Any(d => d.State is PreviewModelDispatchState.Dispatched or PreviewModelDispatchState.Unknown))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalPreviewWorkerOrEffectUnconfirmedNoTakeover");
        if (JsonSerializer.SerializeToUtf8Bytes(Document, FactoryGitHubJson.Options).Length + AdmissionLifecycleBytes
            > AzureCosmosDocumentGateway.MaximumCheckpointBytes)
            throw new PreviewCapabilityException(FactoryCharterState.Partial, "PreviewAdmissionLifecycleCapacityExceeded");
        var admission = new PreviewWorkAdmission(Document.Admission?.Id ?? Guid.NewGuid(), worker, Document.Admission?.Request ?? request,
            authority.Revision, Document.Admission?.AdmittedAt ?? clock.GetUtcNow(),
            PreviewWorkState.Queued, PreviewReadiness.Queued, null);
        owner = worker;
        await WriteAsync(Document with
        {
            Owner = worker,
            Phase = Document.Phase == PreviewExecutionPhase.Prepared
            ? PreviewExecutionPhase.Executing : Document.Phase,
            Admission = admission
        }, token);
        return admission;
    }

    internal async Task BeginWorkAsync(PreviewWorkAdmission admission, CancellationToken token)
    {
        RequireWorker(admission, queued: true);
        owner = admission.WorkerId;
        await WriteAsync(Document with { Admission = Document.Admission! with { State = PreviewWorkState.Running, Readiness = PreviewReadiness.Running } }, token);
    }

    internal async Task<PreviewWorkAdmission> EndWorkAsync(PreviewWorkAdmission admission, PreviewReadiness readiness, string? reason,
        string? evaluatedRevision, bool began, CancellationToken executionToken, CancellationToken token)
    {
        var run = began && !executionToken.IsCancellationRequested ? await ReadAsync(token) : null;
        RequireWorker(admission);
        if (readiness == PreviewReadiness.ReadyAwaitingActivation && evaluatedRevision != Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "EvaluatedPreviewRevisionChangedBeforeRelease");
        owner = admission.WorkerId;
        var confirmedRun = Document.PendingRun is not null && run is not null ? await RunReceiptAsync(run, token) : null;
        var released = began && !executionToken.IsCancellationRequested
            && !Document.Dispatches.Any(d => d.State is PreviewModelDispatchState.Unknown or PreviewModelDispatchState.Dispatched);
        var next = Document with
        {
            Admission = Document.Admission! with
            {
                State = released ? PreviewWorkState.Released : PreviewWorkState.Unknown,
                Readiness = released ? readiness : PreviewReadiness.Unknown,
                Reason = !released && readiness != PreviewReadiness.Unknown || reason?.Length > 256
                    ? "PreviewWorkerOutcomeUnconfirmed" : reason
            }
        };
        if (released && Document.PendingRun is not null)
            next = next with { PendingRun = null, Run = confirmedRun ?? throw new JsonException() };
        await WriteAsync(next, token);
        if (released && executionToken.IsCancellationRequested)
            await QuarantineWorkAsync(admission, "PreviewWorkerOutcomeUnconfirmed", token);
        return Document.Admission!;
    }

    internal async Task QuarantineWorkAsync(PreviewWorkAdmission admission, string reason, CancellationToken token)
    {
        RequireAdmissionIdentity(admission);
        if (Document.Admission is { State: PreviewWorkState.Unknown, Readiness: PreviewReadiness.Unknown }) return;
        owner = admission.WorkerId;
        await WriteAsync(Document with
        {
            Admission = Document.Admission! with { State = PreviewWorkState.Unknown, Readiness = PreviewReadiness.Unknown, Reason = reason }
        }, token);
    }

    internal void RequireWorker(PreviewWorkAdmission? admission, bool queued = false)
    {
        RequireAdmissionIdentity(admission);
        if (Document.Admission!.State != (queued ? PreviewWorkState.Queued : PreviewWorkState.Running))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ExactDurablyAdmittedPreviewWorkerRequired");
    }

    private void RequireAdmissionIdentity(PreviewWorkAdmission? admission)
    {
        if (admission is null || Document.Admission is not { } current || current.Id != admission.Id
            || current.WorkerId != admission.WorkerId || current.Request != admission.Request
            || current.CapabilityRevision != admission.CapabilityRevision || current.CapabilityRevision != authority.Revision
            || Document.Owner != current.WorkerId)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ExactDurablyAdmittedPreviewWorkerRequired");
    }

    private void ValidateAdmission(PreviewEvaluationDocument document)
    {
        if (document.Admission is not { } admission) return;
        if (admission.Id == Guid.Empty || admission.WorkerId == Guid.Empty || admission.WorkerId != document.Owner
            || admission.Request.OperationId != document.OperationId || admission.Request.RequestId == Guid.Empty
            || admission.Request.ApprovalFingerprint != authority.Capability.ApprovalFingerprint
            || admission.CapabilityRevision != authority.Revision || admission.AdmittedAt < document.CreatedAt
            || admission.AdmittedAt > clock.GetUtcNow() || admission.AdmittedAt.Offset != TimeSpan.Zero
            || !Enum.IsDefined(admission.State) || !Enum.IsDefined(admission.Readiness) || admission.Reason?.Length > 256
            || admission.State == PreviewWorkState.Queued && admission.Readiness != PreviewReadiness.Queued
            || admission.State == PreviewWorkState.Running && admission.Readiness != PreviewReadiness.Running)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewWorkerAdmissionUnconfirmed");
    }
}
