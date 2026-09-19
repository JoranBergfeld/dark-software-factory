using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    private async Task<FactoryPublisherObservation> ObservePublisherAsync(FactoryPublisherCapability capability, CancellationToken token)
    {
        var plan = capability.Plan;
        var identity = plan.Deployment.Manifest.Identity;
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "api://" + plan.Selection.AudienceClientId);
        using var hop = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, hop.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, capability.ContextEndpoint + "/readiness")
        { Content = JsonContent.Create(new FactoryLiveRequest(plan.Selection.OperationId, plan.Fingerprint), options: FactoryGitHubJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(["api://" + plan.Selection.AudienceClientId + "/.default"]), pending.Token)).Token);
        using var response = await (http ?? throw new InvalidOperationException()).SendAsync(request, pending.Token);
        if (response.StatusCode != HttpStatusCode.OK) throw Failure("CurrentAuthenticatedPublicationProviderCapabilityRequired");
        await response.Content.LoadIntoBufferAsync(32 * 1024, pending.Token);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(pending.Token), new() { AllowDuplicateProperties = false });
        var observed = body.RootElement.Deserialize<FactoryPublisherObservation>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (observed.RecoveryProtocol != FactoryPublicationRecovery.Protocol)
            throw Failure("ReviewedOriginalPublicationOutcomeImplementationRequiredNoLegacyUpgrade");
        FactoryPublisher.ValidateObservation(observed, plan, clock.GetUtcNow() - FactoryPreviewValidation.RequestBudget, clock.GetUtcNow());
        return observed;
    }
}
