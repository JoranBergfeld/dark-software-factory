using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactorySourceService
{
    internal async Task<IntentResult> InferAsync(FactoryReservationResult admission, IntentRequest request, bool status, CancellationToken token)
    {
        var store = Store(admission);
        var (_, manifest, github, values) = await ReadAsync(admission, store, token);
        var (source, effects) = FactorySourceValidation.Read(manifest, github, values);
        await CheckNativeAsync(admission, source, manifest, github, values, effects, token);
        var root = await store.ReadAsync(ProductConfigurationKeys.FactorySource, token) ?? throw new JsonException();
        var model = await store.ReadAsync(ProductConfigurationKeys.FactoryModels, token) ?? throw new JsonException();
        var credential = new OwnerReservationStore.OperatorCredential(azure, manifest.Identity.TenantId, manifest.Identity.SubscriptionId,
            manifest.Identity.AzureActorId, "api://" + source.Configuration.Selection.AudienceClientId);
        var access = await credential.GetTokenAsync(new TokenRequestContext(["api://" + source.Configuration.Selection.AudienceClientId + "/.default"]), token);
        using var call = new HttpRequestMessage(HttpMethod.Post, source.HostEndpoint + (status ? "/intent-status" : "/infer"))
        { Content = JsonContent.Create(request, options: FactoryGitHubJson.Options) };
        call.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        await CurrentAsync(manifest, github, store, root, token);
        using var deadline = new CancellationTokenSource(FactoryIntentLimits.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var response = await (http ?? IntentHttp).SendAsync(call, HttpCompletionOption.ResponseHeadersRead, pending.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.Forbidden)) throw new JsonException();
            await using var stream = await response.Content.ReadAsStreamAsync(pending.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, pending.Token);
                if (count == 0) break;
                if (buffer.Length + count > 512 * 1024) throw new JsonException();
                buffer.Write(bytes, 0, count);
            }
            var result = JsonSerializer.Deserialize<IntentResult>(buffer.ToArray(), FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (!Enum.IsDefined(result.State) || result.Governs || result.DeployedSourceCorrelation != "Unknown"
                || result.Progress is { } progress && (progress.Protocol != FactoryIntentLimits.Protocol
                    || progress.Snapshot.FactoryId != manifest.Identity.FactoryId || progress.Snapshot.RepositoryId != github.RepositoryId
                    || progress.Snapshot.OperationId != source.OperationId || progress.Snapshot.ConfigurationRevision != source.ApprovalFingerprint)
                || result.State == IntentState.DraftReady && (response.StatusCode != HttpStatusCode.OK
                    || result.Progress is not { Dispatch: null, State: IntentState.DraftReady } complete
                    || complete.AvailableChunks < 1 || complete.AnalyzedChunks != complete.AvailableChunks || complete.CheckedChunks != complete.AvailableChunks
                    || result.Draft is null || result.ModelAuthority is null || result.Claims.Count == 0 || result.Claims.Any(c => !c.Grounded || c.Conflicted)))
                throw new JsonException();
            result.Draft?.Validate();
            await CheckNativeAsync(admission, source, manifest, github, values, effects, pending.Token);
            await CurrentAsync(manifest, github, store, root, pending.Token);
            var currentModel = await store.ReadAsync(ProductConfigurationKeys.FactoryModels, pending.Token);
            if (currentModel?.ETag != model.ETag || currentModel.Value != model.Value) throw new JsonException();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(IntentState.Canceled, "Canceled; model dispatch/progress may remain Unknown. Inspect private status.", null, null, null, [], [], [], null); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException or CharterParseException
            or InvalidOperationException or ArgumentException or KeyNotFoundException or ReservationStoreException)
        { return new(IntentState.Unknown, "Analysis response or final authority unconfirmed. Inspect private status; no publication or forced retry.", null, null, null, [], [], [], null); }
    }

    private static readonly HttpClient IntentHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = FactoryIntentLimits.RequestBudget };
}
