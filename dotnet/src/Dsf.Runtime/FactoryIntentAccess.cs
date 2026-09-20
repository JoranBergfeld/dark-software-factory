using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class IntentException(IntentState state, string reason) : Exception(reason)
{
    internal IntentState State { get; } = state;
    internal ModelCapabilityReason? ModelRejection { get; init; }
    internal ModelInferenceRejection? ModelRejectionProof { get; init; }
}

internal sealed class FactoryIntentAccess(FactorySourceAccess source, HttpClient http, TimeProvider clock)
{
    internal FactoryModels Models { get; private set; } = null!;
    internal ModelCapabilityReference Capability { get; private set; } = null!;
    internal ModelAuthorityResult CurrentAuthority { get; private set; } = null!;
    private string? pin;

    internal async Task CheckAsync(CancellationToken token)
    {
        try
        {
            await CheckHostAsync(token);
            var request = new ModelAuthorityRequest(Capability, Guid.NewGuid());
            var result = await RequestAsync<ModelAuthorityResult>("/authority", request, token);
            FactoryModelsValidation.ValidateNativeAuthority(Models, request.CheckId, result);
            FactoryRuntimeModels.RequireProtocol(Models, result, clock.GetUtcNow());
            await CheckHostAsync(token);
            CurrentAuthority = result;
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !token.IsCancellationRequested)
        { throw new IntentException(IntentState.Unknown, "NativeModelAuthorityUnconfirmed; no probe, fallback or draft release."); }
    }

    private async Task CheckHostAsync(CancellationToken token)
    {
        await source.CheckAsync(token);
        var models = FactoryModelsValidation.ReadPreparation(source.Manifest, source.Values);
        var reference = new ModelCapabilityReference(models.OperationId, models.ApprovalFingerprint,
            models.LastProofReference ?? "", models.LastProofSha256 ?? "");
        models = await FactoryRuntimeModels.ReadAsync(source.Manifest, source.Values, reference, clock.GetUtcNow(),
            source.Credential, http, token);
        if (models.Bindings![1] != source.Code || models.HostEndpoint is null)
            throw new IntentException(IntentState.Blocked, "CurrentCodeModelBindingRequired");
        var target = FactoryModelsValidation.HostTarget(models);
        using var host = await source.ArmAsync(target, "2025-01-01", token);
        using var auth = await source.ArmAsync(target + "/authconfigs/current", "2025-01-01", token);
        foreach (var (resource, id) in new[] { (host.RootElement, target), (auth.RootElement, target + "/authconfigs/current") })
        {
            var observed = FactoryHostedWorkloadPolicy.ObserveModelHost(resource, id);
            var effect = models.Effects!.Single(e => e.Target == id);
            if (observed.Incarnation != effect.Incarnation || observed.Revision != effect.NativeRevision)
                throw new IntentException(IntentState.Blocked, "ModelHostReceiptChanged");
        }
        var endpoint = FactoryHostedWorkloadPolicy.Validate(host.RootElement, auth.RootElement, models.Bindings[0], models.Bindings[2],
            models.Configuration.RuntimeImage, "serve-models", source.Manifest.Identity.ProductKey, models.Configuration.ProductEndpoint,
            source.Manifest.ProfilePin, source.Manifest.Identity.TenantId, models.Configuration.AudienceClientId,
            [source.Manifest.Identity.AzureActorId, source.Code.PrincipalId!, models.Bindings[0].PrincipalId!]);
        if (models.HostEndpoint != endpoint) throw new IntentException(IntentState.Blocked, "ModelHostEndpointChanged");
        var current = FactoryModelsValidation.Hash(new { source.Manifest, source.Source, Models = models });
        if (pin is not null && pin != current) throw new IntentException(IntentState.Blocked, "AnalysisAuthorityChanged");
        pin = current;
        Models = models;
        Capability = reference;
    }

    internal async Task<ModelTextResult> CompleteAsync(string input, CancellationToken token)
    {
        try { return await RequestCompletionAsync(input, token); }
        catch (Exception e) when (source.IsRefreshWork && !source.ModelRequestIssued
            && (e is IntentException or HttpRequestException or IOException || e is OperationCanceledException && !token.IsCancellationRequested))
        { throw new IntentException(IntentState.Blocked, "KnownNoModelDispatch; original process failed before the model POST."); }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !token.IsCancellationRequested)
        { throw new IntentException(IntentState.Unknown, "ModelDispatchUnconfirmed; provider details withheld, no automatic retry."); }
    }

    private async Task<ModelTextResult> RequestCompletionAsync(string input, CancellationToken token)
    {
        await CheckAsync(token);
        var result = await RequestAsync<ModelTextResult>("/synthesis", new ModelWorkloadRequest(Capability, input), token);
        try { FactoryModelsValidation.ValidateTextResult(Models, "synthesis", result); }
        catch (ModelCapabilityException) { throw new IntentException(IntentState.Failed, "InvalidModelObservation"); }
        await CheckAsync(token);
        return result;
    }

    private async Task<T> RequestAsync<T>(string path, object body, CancellationToken token)
    {
        var access = await source.Credential.GetTokenAsync(
            new TokenRequestContext(["api://" + Models.Configuration.AudienceClientId + "/.default"]), token);
        using var request = new HttpRequestMessage(HttpMethod.Post, Models.HostEndpoint + path)
        {
            Content = JsonContent.Create(body, options: FactoryGitHubJson.Options),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        await CheckHostAsync(token);
        token.ThrowIfCancellationRequested();
        if (path == "/synthesis") source.MarkModelRequestIssued();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (path == "/authority" && response.StatusCode == HttpStatusCode.NotFound)
            throw new IntentException(IntentState.Blocked, "NativeModelAuthorityUnavailable; compatible model-host image required.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new IntentException(IntentState.Blocked, "ModelAccessOrCapabilityRejected");
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
            throw new IntentException(IntentState.Unknown, "ModelDispatchUnconfirmed");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, token);
            if (count == 0) break;
            if (buffer.Length + count > 64 * 1024) throw new IntentException(IntentState.Failed, "ModelResponseLimit");
            buffer.Write(bytes, 0, count);
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            try
            {
                using var failure = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
                if (failure.RootElement.TryGetProperty("reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String && Enum.TryParse<ModelCapabilityReason>(reason.GetString(), out var known)
                    && Enum.IsDefined(known) && known != ModelCapabilityReason.None)
                {
                    ModelInferenceRejection? rejection = null;
                    if (path == "/synthesis" && known == ModelCapabilityReason.RateLimited)
                    {
                        if (failure.RootElement.TryGetProperty("rejection", out var proof))
                            rejection = proof.Deserialize<ModelInferenceRejection>(FactorySourceValidation.RecordJson);
                        if (body is not ModelWorkloadRequest completion
                            || !FactoryModelsValidation.IsConfirmedInferenceRejection(rejection, "synthesis", FactoryModelsValidation.Hash(completion.Input)))
                            throw new IntentException(IntentState.Unknown, "NativeModelRejectionProvenanceUnconfirmed; compatible model-host evidence required.");
                    }
                    throw new IntentException(known == ModelCapabilityReason.MalformedResponse ? IntentState.Failed
                        : known is ModelCapabilityReason.NetworkUnavailable or ModelCapabilityReason.InferenceOutcomeUnconfirmed
                            ? IntentState.Unknown : IntentState.Blocked, "Model" + known)
                    { ModelRejection = rejection is not null ? known : null, ModelRejectionProof = rejection };
                }
            }
            catch (JsonException) { throw new IntentException(IntentState.Unknown, "MalformedModelFailureEnvelope"); }
            throw new IntentException(IntentState.Unknown, "ModelOutcomeUnconfirmed");
        }
        try { return JsonSerializer.Deserialize<T>(buffer.ToArray(), FactorySourceValidation.RecordJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new IntentException(IntentState.Failed, "MalformedModelEnvelope"); }
    }
}
