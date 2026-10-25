using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal static class FactorySourceRefreshClient
{
    internal static async Task<SourceRefreshObservation> InspectAsync(PreviewCapabilitySnapshot authority, PreviewCapabilityRequest request,
        TokenCredential credential, HttpClient http, TimeProvider clock, CancellationToken token)
    {
        var result = await SendAsync<SourceRefreshObservation>(authority, "/source-refresh-status", request, credential, http, clock, token);
        if (result.Protocol != FactorySourceRefresh.Protocol || result.RequestId != request.RequestId || !Enum.IsDefined(result.State)
            || result.Binding.AdoptionRevision != authority.Capability.Configuration.AdoptedCharterRevision
            || result.Binding.Snapshot.FactoryId != authority.Manifest.Identity.FactoryId
            || result.Binding.Snapshot.RepositoryId != authority.Manifest.Identity.RepositoryId)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "NativeSourceRefreshProtocolOrBindingUnconfirmed");
        return result;
    }

    internal static Task<SourceRefreshReceipt> AdvanceAsync(PreviewCapabilitySnapshot authority, SourceRefreshRequest request,
        TokenCredential credential, HttpClient http, TimeProvider clock, CancellationToken token) =>
        SendAsync<SourceRefreshReceipt>(authority, "/source-refresh-advance", request, credential, http, clock, token);

    private static async Task<T> SendAsync<T>(PreviewCapabilitySnapshot authority, string path, object input,
        TokenCredential credential, HttpClient http, TimeProvider clock, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(FactoryIntentLimits.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, authority.SourceContextEndpoint + path)
        { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["api://" + authority.Capability.Configuration.AudienceClientId + "/.default"]), pending.Token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "NativeSourceRefreshProtocolRequired; explicitly upgrade the reviewed CODE context host.");
        using var body = await EvidencePayload.ReadAsync(response, pending.Token, 16384);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var failure = body.RootElement.Deserialize<PreviewContextFailure>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            throw new PreviewCapabilityException(failure.State, failure.Reason);
        }
        return body.RootElement.Deserialize<T>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
    }
}
