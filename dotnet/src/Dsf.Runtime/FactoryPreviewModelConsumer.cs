using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    public static Task<IFactoryPreviewModelWorkloads> OpenPreviewModelsAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.OpenPreviewModelsAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token);
}

internal static partial class RuntimeWorkflow
{
    internal static async Task<IFactoryPreviewModelWorkloads> OpenPreviewModelsAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PreviewCapabilityRequest request, HttpClient http, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.PreviewClock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { return await OpenAsync(settings, dependencies, request, http, pending.Token); }
        catch (OperationCanceledException)
        { throw new PreviewCapabilityException(token.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "PreviewModelAdmissionCanceledOrUnconfirmed"); }
    }

    private static async Task<IFactoryPreviewModelWorkloads> OpenAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PreviewCapabilityRequest request, HttpClient http, CancellationToken token)
    {
        var credential = dependencies.PreviewCredential
            ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
        var validator = new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, dependencies.PreviewClock);
        var snapshot = await validator.CheckAsync(request, token);
        if (snapshot.Capability.Configuration.ModelEvaluation is null || snapshot.ModelEndpoint is null || snapshot.Models is null)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewModelConsumerRequired");
        var consumer = new FactoryPreviewModelConsumer(validator, snapshot, request, credential, http, dependencies.PreviewClock);
        await consumer.ReadAuthorityAsync(token);
        return consumer;
    }
}

internal sealed class FactoryPreviewModelConsumer(IFactoryPreviewCapability validator, PreviewCapabilitySnapshot snapshot,
    PreviewCapabilityRequest request, TokenCredential credential, HttpClient http, TimeProvider clock) : IFactoryPreviewModelWorkloads
{
    private readonly List<PreviewModelObservation> observations = [];
    private bool unconfirmed;
    private IPreviewModelDispatchJournal? journal;
    public IReadOnlyList<PreviewModelObservation> Observations => observations.ToArray();
    public void AttachJournal(IPreviewModelDispatchJournal value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (journal is not null || observations.Any(o => o.Output is not null) || unconfirmed)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewDispatchJournalMustPrecedeEvaluation");
        journal = value;
    }

    public async Task<ModelAuthorityResult> ReadAuthorityAsync(CancellationToken token) =>
        (await InvokeAsync("authority", null, null, token)).Authority;
    public async Task<ModelTextResult> SynthesizeAsync(string input, CancellationToken token) =>
        (await InvokeAsync("synthesis", input, null, token)).Completion!;
    public async Task<ModelTextResult> JudgeAsync(string juror, string input, CancellationToken token)
    {
        if (!FactoryModelsValidation.RouteNames.Skip(2).Contains(juror))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ExactPreviewJurorRouteRequired");
        return (await InvokeAsync(juror, input, null, token)).Completion!;
    }
    public async Task<ModelDeduplicationResult> DeduplicateAsync(IReadOnlyList<ModelText> input, CancellationToken token) =>
        (await InvokeAsync("embedding", null, input, token)).Deduplication!;

    private async Task<PreviewModelResult> InvokeAsync(string route, string? text, IReadOnlyList<ModelText>? items, CancellationToken token)
    {
        if (unconfirmed) throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PriorModelDispatchUnconfirmedNoAutomaticRetry");
        if (observations.Count >= 64) throw new PreviewCapabilityException(FactoryCharterState.Partial, "PreviewModelObservationLimit");
        var input = new PreviewModelRequest(request with { RequestId = Guid.NewGuid() }, route, text, items);
        RuntimeVerbs.ValidatePreviewModelRequest(input);
        var dispatched = false;
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var bounded = pending.Token;
        try
        {
            await RevalidateAsync(bounded);
            var result = journal is not null && route != "authority"
                ? await journal.ExecuteAsync(input, DispatchAsync, bounded) : await DispatchAsync(input, bounded);
            observations.Add(new(result.RequestId, result.InputSha256, result.CapabilityRevision, result.Authority,
                result.Completion?.Observation ?? result.Deduplication?.Observation));
            return result;
        }
        catch (PreviewCapabilityException e)
        {
            unconfirmed |= dispatched && e.State is FactoryCharterState.Unknown or FactoryCharterState.Canceled;
            throw;
        }
        catch (OperationCanceledException)
        {
            unconfirmed |= dispatched;
            throw new PreviewCapabilityException(token.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "PreviewModelDispatchCanceledOrUnconfirmed");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException)
        {
            unconfirmed |= dispatched;
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewModelDispatchUnconfirmed");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or SupplementalSourceException or ModelCapabilityException)
        {
            unconfirmed |= dispatched;
            throw new PreviewCapabilityException(FactoryCharterState.Failed, "InvalidPreviewModelResult");
        }

        async Task<PreviewModelResult> DispatchAsync(PreviewModelRequest input, CancellationToken bounded)
        {
            await RevalidateAsync(bounded);
            var workload = new FactoryWorkloadCredential(credential, snapshot.Manifest.Identity.TenantId, snapshot.Council, clock);
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, snapshot.ModelEndpoint + "/evaluate")
            { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
            outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await workload.GetTokenAsync(
                new TokenRequestContext(["api://" + snapshot.Capability.Configuration.ModelEvaluation!.AudienceClientId + "/.default"]), bounded)).Token);
            dispatched = true;
            using var response = await http.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, bounded);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.Forbidden))
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewModelDispatchUnconfirmed");
            using var body = await EvidencePayload.ReadAsync(response, bounded, RuntimeVerbs.PreviewModelResponseBytes);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var failure = body.RootElement.Deserialize<PreviewContextFailure>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (failure.State is not (FactoryCharterState.Partial or FactoryCharterState.Blocked or FactoryCharterState.Failed
                    or FactoryCharterState.Canceled or FactoryCharterState.Unknown)) throw new JsonException();
                throw new PreviewCapabilityException(failure.State, "PreviewModelEvaluationNotConfirmed");
            }
            var result = body.RootElement.Deserialize<PreviewModelResult>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (result.RequestId != input.Preview.RequestId || result.InputSha256 != FactoryModelsValidation.Hash(input)
                || result.CapabilityRevision != snapshot.Revision) throw new JsonException();
            FactoryModelsValidation.ValidateNativeAuthority(snapshot.Models!, input.Preview.RequestId, result.Authority);
            if (route == "authority")
            {
                if (result.Completion is not null || result.Deduplication is not null) throw new JsonException();
            }
            else if (route == "embedding")
            {
                if (result.Completion is not null || result.Deduplication is null) throw new JsonException();
                FactoryModelsValidation.ValidateDeduplicationResult(snapshot.Models!, items!, result.Deduplication);
            }
            else
            {
                if (result.Completion is null || result.Deduplication is not null) throw new JsonException();
                FactoryModelsValidation.ValidateTextResult(snapshot.Models!, route, result.Completion);
            }
            await RevalidateAsync(bounded);
            return result;
        }
    }

    private async Task RevalidateAsync(CancellationToken token)
    {
        var current = await validator.CheckAsync(request, token);
        if (current.Revision != snapshot.Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CapturedPreviewModelCapabilityChanged");
    }
}
