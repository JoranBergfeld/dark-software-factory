using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class PreviewExecutionStore
{
    public async Task<PreviewModelResult> ExecuteAsync(PreviewModelRequest request,
        Func<PreviewModelRequest, CancellationToken, Task<PreviewModelResult>> dispatch, CancellationToken token)
    {
        var identity = FactoryModelsValidation.Hash(request with { Preview = request.Preview with { RequestId = Guid.Empty } });
        var existing = Document.Dispatches.SingleOrDefault(d => d.Identity == identity);
        if (existing is not null)
        {
            ValidateDispatch(existing);
            if (existing.State == PreviewModelDispatchState.Confirmed) return existing.Result!;
            if (existing.State != PreviewModelDispatchState.Intended)
                throw new PreviewCapabilityException(existing.State == PreviewModelDispatchState.KnownFailed ? FactoryCharterState.Failed : FactoryCharterState.Unknown,
                    "PriorPreviewModelDispatchCannotReplay");
            request = existing.Request;
        }
        else
        {
            if (Document.Dispatches.Count >= FactoryPreviewExecution.Policy.MaximumDispatches)
                throw new PreviewCapabilityException(FactoryCharterState.Partial, "PreviewDispatchCapacityExceeded");
            existing = new(identity, request, PreviewModelDispatchState.Intended, null, null);
            var intended = Document with { Dispatches = [.. Document.Dispatches, existing] };
            if (JsonSerializer.SerializeToUtf8Bytes(intended, FactoryGitHubJson.Options).Length + RuntimeVerbs.PreviewModelResponseBytes + AdmissionLifecycleBytes
                > AzureCosmosDocumentGateway.MaximumCheckpointBytes)
                throw new PreviewCapabilityException(FactoryCharterState.Partial, "PreviewDispatchResultReservationExceeded");
            await WriteAsync(intended, token);
        }
        await SaveDispatchAsync(existing with { State = PreviewModelDispatchState.Dispatched }, token);
        try
        {
            var result = await dispatch(request, token);
            var confirmed = existing with { State = PreviewModelDispatchState.Confirmed, Result = result };
            ValidateDispatch(confirmed);
            await SaveDispatchAsync(confirmed, token);
            return result;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException
            or InvalidOperationException or JsonException or Azure.RequestFailedException or PreviewCapabilityException
            or ModelCapabilityException or SupplementalSourceException or ArgumentException or KeyNotFoundException
            or Azure.Identity.AuthenticationFailedException)
        {
            if (!poisoned)
            {
                var known = exception is PreviewCapabilityException { State: FactoryCharterState.Failed or FactoryCharterState.Blocked,
                    Message: "PreviewModelEvaluationNotConfirmed" };
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                await SaveDispatchAsync(existing with { State = known ? PreviewModelDispatchState.KnownFailed : PreviewModelDispatchState.Unknown,
                    Failure = known ? "NativeModelRejectedEvaluation" : "NativeModelDispatchUnconfirmed" }, budget.Token);
            }
            throw;
        }
    }

    private Task SaveDispatchAsync(PreviewModelDispatch dispatch, CancellationToken token) =>
        WriteAsync(Document with { Dispatches = Document.Dispatches.Select(d => d.Identity == dispatch.Identity ? dispatch : d).ToArray() }, token);

    private void ValidateDispatch(PreviewModelDispatch dispatch)
    {
        RuntimeVerbs.ValidatePreviewModelRequest(dispatch.Request);
        if (dispatch.Request.Route == "authority" || dispatch.Identity != FactoryModelsValidation.Hash(
                dispatch.Request with { Preview = dispatch.Request.Preview with { RequestId = Guid.Empty } })
            || dispatch.Request.Preview.OperationId != authority.Capability.OperationId
            || dispatch.Request.Preview.ApprovalFingerprint != authority.Capability.ApprovalFingerprint || !Enum.IsDefined(dispatch.State))
            throw new JsonException("Unbound preview model dispatch.");
        if (dispatch.State != PreviewModelDispatchState.Confirmed)
        {
            if (dispatch.Result is not null) throw new JsonException();
            return;
        }
        var result = dispatch.Result ?? throw new JsonException();
        if (result.RequestId != dispatch.Request.Preview.RequestId || result.InputSha256 != FactoryModelsValidation.Hash(dispatch.Request)
            || result.CapabilityRevision != authority.Revision || dispatch.Failure is not null)
            throw new JsonException();
        FactoryModelsValidation.ValidateNativeAuthority(authority.Models!, result.RequestId, result.Authority);
        if (dispatch.Request.Route == "embedding")
        {
            if (result.Completion is not null || result.Deduplication is null) throw new JsonException();
            FactoryModelsValidation.ValidateDeduplicationResult(authority.Models!, dispatch.Request.Items!, result.Deduplication);
        }
        else
        {
            if (result.Completion is null || result.Deduplication is not null) throw new JsonException();
            FactoryModelsValidation.ValidateTextResult(authority.Models!, dispatch.Request.Route, result.Completion);
        }
    }
}
