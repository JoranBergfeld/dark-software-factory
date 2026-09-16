using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed class FactoryPreparationArm(IAzureCliRunner azure, HttpClient? http, FactoryReservationIdentity identity)
{
    private static readonly HttpClient ProviderHttp = new(new HttpClientHandler { AllowAutoRedirect = false });

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string target, string version,
        CancellationToken token, string? body = null, Guid? correlation = null)
    {
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "https://management.azure.com/");
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
        using var request = new HttpRequestMessage(method, "https://management.azure.com" + target
            + (target.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + version);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        if (correlation is not null) request.Headers.Add("x-ms-client-request-id", correlation.Value.ToString());
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await (http ?? ProviderHttp).SendAsync(request, token);
    }
}
