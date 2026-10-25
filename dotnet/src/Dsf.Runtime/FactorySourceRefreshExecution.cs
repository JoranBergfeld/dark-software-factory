using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class PreviewExecutionStore
{
    internal async Task<SourceRefreshReceipt> AdvanceSourceAsync(PreviewWorkAdmission admission, SourceRefreshBinding binding,
        Func<SourceRefreshRequest, CancellationToken, Task<SourceRefreshReceipt>> dispatch, CancellationToken token)
    {
        RequireWorker(admission);
        if (live?.Decision is null || HasUnconfirmedSourceWork(Document))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalPromotedRefreshWorkerRequiredNoReplay");
        var previous = Document.SourceRefresh ?? [];
        if (previous.Count >= FactorySourceRefresh.AdvancesPerWorker
            || previous.Count + Document.Dispatches.Count >= FactoryPreviewExecution.Policy.MaximumDispatches)
            throw new PreviewCapabilityException(FactoryCharterState.Partial, "SourceRefreshWorkerBudgetReached; next scheduled invocation may continue confirmed progress.");
        await live.RequireCurrentAsync(token);
        var request = new SourceRefreshRequest(admission.Request with { RequestId = Guid.NewGuid() },
            live.Decision.Scope.InvocationId, admission.Id, admission.WorkerId, NativeIdentity!, binding)
        { Continuation = previous.Count != 0 || live.RefreshHistoryRequired };
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, FactoryGitHubJson.Options).Length > 4096)
            throw new PreviewCapabilityException(FactoryCharterState.Partial, "CompleteSourceRefreshRequestExceedsNativeIngressBudget");
        var work = new SourceRefreshDispatch(request, PreviewModelDispatchState.Dispatched, null);
        owner = admission.WorkerId;
        await WriteAsync(Document with { SourceRefresh = [.. previous, work] }, token);
        await live.RequireCurrentAsync(token);
        var receipt = await dispatch(request, token);
        if (receipt.RequestId != request.Preview.RequestId || receipt.RequestSha256 != FactoryModelsValidation.Hash(request)
            || !Enum.IsDefined(receipt.State) || receipt.Progress.FactoryId != Document.FactoryId
            || receipt.Progress.Store != AuditStoreKind.Analysis || receipt.Progress.Key != FactorySourceRefresh.CheckpointKey(request.Preview.RequestId)
            || string.IsNullOrEmpty(receipt.Progress.NativeRevision) || !FactorySourceValidation.Hash(receipt.Progress.Sha256))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalRefreshAdvanceReceiptUnconfirmed");
        work = work with { State = receipt.State == SourceRefreshState.Unknown
            ? PreviewModelDispatchState.Unknown : PreviewModelDispatchState.Confirmed, Receipt = receipt };
        await WriteAsync(Document with { SourceRefresh = [.. previous, work] }, token);
        return receipt;
    }

    internal static bool HasUnconfirmedSourceWork(PreviewEvaluationDocument document) =>
        document.SourceRefresh?.Any(d => d.State is not PreviewModelDispatchState.Confirmed) == true;

    private void ValidateSourceWork(PreviewEvaluationDocument document)
    {
        if (document.SourceRefresh is not { } steps) return;
        if (document.DecisionScope is null || steps.Count is < 1 or > FactorySourceRefresh.AdvancesPerWorker || steps.Any(s => s is null)
            || steps.Count + document.Dispatches.Count > FactoryPreviewExecution.Policy.MaximumDispatches
            || steps.Select(s => s.Request.Preview.RequestId).Distinct().Count() != steps.Count)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalSourceWorkInventoryUnconfirmed");
        foreach (var step in steps)
            if (step.Request.Invocation != document.DecisionScope.InvocationId || step.Request.Admission != document.Admission?.Id
                || step.Request.Preview.OperationId != document.OperationId || step.Request.Preview.RequestId == Guid.Empty
                || step.Request.Worker == Guid.Empty || step.Request.Evaluation.FactoryId != document.FactoryId
                || step.Request.Evaluation.Store != AuditStoreKind.Preview
                || step.Request.Evaluation.Key != $"dbs/{document.Product}/colls/{FactoryPreviewValidation.PreviewContainer}/docs/{document.Id}"
                || step.Request.Binding.Snapshot.FactoryId != document.FactoryId
                || step.State is not (PreviewModelDispatchState.Dispatched or PreviewModelDispatchState.Confirmed or PreviewModelDispatchState.Unknown)
                || step.State == PreviewModelDispatchState.Confirmed && (step.Receipt is null
                    || step.Receipt.RequestSha256 != FactoryModelsValidation.Hash(step.Request)
                    || step.Receipt.RequestId != step.Request.Preview.RequestId || step.Receipt.State == SourceRefreshState.Unknown))
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalSourceWorkReceiptUnconfirmed");
    }
}
