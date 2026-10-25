using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    internal async Task<FactoryLiveObservation> ExerciseLiveAsync(FactoryReservationResult admission, FactoryLiveRequest input,
        bool readOnly, CancellationToken token)
    {
        var prepared = await LiveAsync(admission, null, null, token, input);
        if (prepared.State != FactoryLiveState.PreparedAwaitingPublication || prepared.Plan is not { } plan || plan.Fingerprint != input.RolloutFingerprint)
            return new(1, admission.Identity?.FactoryId ?? Guid.Empty, input.OperationId, input.RolloutFingerprint, clock.GetUtcNow(),
                PreviewReadiness.Blocked, "CurrentOriginalNativeLiveRolloutRequired", null);
        var records = await Store(admission).ReadNamespaceAsync(FactoryLive.Prefix, token, FactoryLive.MaximumRecords);
        var live = FactoryLive.ReadConfirmed(records.ToDictionary(p => p.Key, p => p.Value.Value), clock.GetUtcNow());
        var identity = plan.Deployment.Manifest.Identity;
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "api://" + plan.Selection.AudienceClientId);
        var route = readOnly ? "/execution" : "/exercise";
        try
        {
            while (true)
            {
                using var hop = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, hop.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, live.Endpoint + route)
                { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext(["api://" + plan.Selection.AudienceClientId + "/.default"]), pending.Token)).Token);
                using var response = await (http ?? throw new InvalidOperationException()).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
                await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, pending.Token);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(pending.Token), new JsonDocumentOptions { AllowDuplicateProperties = false });
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new JsonException();
                var observed = document.RootElement.Deserialize<FactoryLiveObservation>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (observed.Protocol != 1 || observed.FactoryId != identity.FactoryId || observed.InvocationId != input.OperationId
                    || observed.RolloutFingerprint != input.RolloutFingerprint || !Enum.IsDefined(observed.Readiness)
                    || observed.ObservedAt > clock.GetUtcNow() || observed.ObservedAt < clock.GetUtcNow() - FactoryPreviewValidation.RequestBudget
                    || (response.StatusCode == HttpStatusCode.Accepted) != (observed.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running or PreviewReadiness.Checking))
                    throw new JsonException();
                if (observed.Readiness == PreviewReadiness.ReadyAwaitingActivation)
                {
                    var proof = observed.Evaluation?.GetProperty("proof").Deserialize<PreviewReadinessProof>(FactorySourceValidation.RecordJson)
                        ?? throw new JsonException();
                    if (proof.PreviewId != FactoryPromotion.DecisionId(identity.FactoryId, input.OperationId)
                        && (!readOnly || proof.PreviewId != "live-" + FactoryModelsValidation.Hash(new { identity.FactoryId, input.OperationId }))
                        || proof.Candidates.Count == 0 || proof.FactoryId != identity.FactoryId) throw new JsonException();
                }
                if (observed.Readiness is not (PreviewReadiness.Queued or PreviewReadiness.Running or PreviewReadiness.Checking)
                    || readOnly && observed.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running) return observed;
                route = "/execution";
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or JsonException
            or InvalidOperationException or ArgumentException or KeyNotFoundException or OwnerReservationStore.AuthenticationFailedException)
        {
            return new(1, identity.FactoryId, input.OperationId, input.RolloutFingerprint, clock.GetUtcNow(), PreviewReadiness.Unknown,
                "OriginalNativeExerciseOrResponseUnconfirmed; inspect exact invocation, never replay an uncertain admission.", null);
        }
    }
}
