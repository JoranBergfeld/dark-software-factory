using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

internal sealed class FactoryDecisionClient(RuntimeSettings settings, RuntimeDependencies dependencies, FactoryManifest manifest)
{
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private HttpClient Http => dependencies.PreviewHttp ?? DefaultHttp;
    private TimeProvider Clock => dependencies.PreviewClock;
    internal async Task<(FactoryLiveCapability Live, PreviewHttpObservation Observation)> ObserveAsync(FactoryDecisionRequest input,
        bool execute, CancellationToken token, string? forwardedBearer = null)
    {
        var values = new Dictionary<string, string>();
        await foreach (var (key, value) in dependencies.ConfigurationSettingsGateway.ListAsync(settings.AppConfigEndpoint, ProductConfigurationKeys.NoLabel, token))
        {
            if (execute) FactoryPause.RequireAdmissionOpen([key]);
            if (values.Count >= FactoryAudit.MaximumRecords || !values.TryAdd(key, value)) throw new JsonException();
        }
        var live = FactoryLive.ReadConfirmed(values.Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(), Clock.GetUtcNow());
        if (live.Plan.Deployment.Manifest.Identity != manifest.Identity || live.Plan.Deployment.Manifest.ProfilePin != manifest.ProfilePin)
            throw new RuntimeVerbException("Exact original reviewed factory decision host required.");
        var credential = dependencies.PreviewCredential ?? new DefaultAzureCredential();
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, Clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, live.Endpoint + (execute ? "/decide" : "/decision"))
        { Content = JsonContent.Create(input, options: FactorySourceValidation.RecordJson) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forwardedBearer ?? (await credential.GetTokenAsync(
            new TokenRequestContext(["api://" + live.Plan.Selection.AudienceClientId + "/.default"]), pending.Token)).Token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
            throw new RuntimeVerbException("Current authenticated onboarding decision admission unavailable; retain original invocation, no fallback or automatic new invocation.");
        using var body = await EvidencePayload.ReadAsync(response, pending.Token, FactoryPublicationWire.MaximumDecisionBytes);
        var observed = body.RootElement.Deserialize<PreviewHttpObservation>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (observed.Protocol != 2 || observed.FactoryId != manifest.Identity.FactoryId || observed.RequestId != input.InvocationId
            || observed.OperationId != manifest.PreviewOperationId || observed.ApprovalFingerprint != manifest.PreviewApprovalFingerprint)
            throw new RuntimeVerbException("Original authenticated decision response required.");
        return (live, observed);
    }

    internal async Task<ConveyorRun> RunAsync(FactoryDecisionRequest request, CancellationToken token)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FactoryPreviewExecution.Policy.RequestSeconds), Clock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var current = (await ObserveAsync(request, true, pending.Token)).Observation;
            while (current.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running or PreviewReadiness.Checking)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Clock, pending.Token);
                current = (await ObserveAsync(request, false, pending.Token)).Observation;
            }
            if (current.Evaluation is not { } evaluation)
                throw new RuntimeVerbException($"Onboarding decision {request.InvocationId:D}: {current.Readiness}; {current.Reason}. No replay.");
            var report = evaluation.Deserialize<PreviewExecutionResult>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            var run = report.Run;
            if (run.Profile != ConveyorProfile.OnboardingPreview || run.Id != FactoryPromotion.DecisionId(manifest.Identity.FactoryId, request.InvocationId) + "-run"
                || run.FiledIssues.Count != 0 || !run.DryRun) throw new JsonException();
            // The returned summary is a projection. Native run/proof bytes remain unchanged after publication.
            run.DryRun = request.DryRun;
            foreach (var publication in report.Publications ?? [])
            {
                if (publication.State == FactoryPublicationState.Confirmed)
                    run.FiledIssues.Add(publication.Receipt!.Url);
                run.Record("publication", publication.State + ": " + publication.Journal.Id);
            }
            if (run.FiledIssues.Count > 0) { run.Status = RunStatus.Filed; run.PreviewedIssues.Clear(); }
            if (current.Readiness != PreviewReadiness.ReadyAwaitingActivation)
            { run.Status = RunStatus.Error; run.FailureReason = current.Readiness + ": " + current.Reason; }
            run.Record("decision", $"Original invocation {request.InvocationId:D}; publication identities are stable factory/repository problems, not invocation IDs.");
            return run;
        }
        catch (RuntimeVerbException) { throw; }
        catch (Exception e) when (e is PreviewCapabilityException or JsonException or InvalidOperationException or ArgumentException
            or KeyNotFoundException or HttpRequestException or IOException or OperationCanceledException or Azure.RequestFailedException or AuthenticationFailedException
            or SupplementalSourceException)
        {
            throw new RuntimeVerbException($"Original onboarding decision {request.InvocationId:D} unconfirmed. Inspect/resume that invocation only; cancellation is not rollback.");
        }
    }
}
