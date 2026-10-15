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
    public static Task<PreviewSupplementalEvidence> ReadPreviewSupplementalAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.ReadPreviewSupplementalAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token);

    internal static PreviewSupplementalAuthority SupplementalAuthority(PreviewCapabilitySnapshot snapshot, PreviewSupplementalRequest request)
    {
        var selected = snapshot.Supplemental ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked, "SelectedSupplementalRequired");
        var config = selected.Configuration!;
        var host = snapshot.Capability.Configuration.Supplemental!.Hosts.Single(h => h.Kind == request.Kind);
        var source = config.Selection.Sources.Single(s => s.Kind == request.Kind);
        var target = FactorySupplementalValidation.HostTarget(config, request.Kind);
        var grants = FactorySupplementalValidation.Grants(config, source);
        var identity = snapshot.SupplementalEffects.Single(e => e.Target == FactorySupplementalValidation.IdentityTarget(config, request.Kind));
        var metadata = FactoryModelsValidation.Grant(host.MetadataScope, host.MetadataRoleDefinitionId, host.PrincipalId, "");
        return new(request, snapshot.GovernanceRevision, snapshot.Revision, host, identity,
            FactorySupplementalValidation.HostConfiguration(snapshot.Manifest, selected, identity, request.Kind), config.Hosting,
            request.Kind == "webiq" ? config.Vault : null,
            config.PrerequisiteRevisions.Where(p => p.Key == source.SearchResourceId).ToDictionary(),
            grants, snapshot.SupplementalEffects.Where(e => e.Target == identity.Target || e.Target == target
                || e.Target == target + "/authconfigs/current" || grants.Any(g => g.Target == e.Target)).ToArray(),
            snapshot.Effects.Single(e => e.Target == metadata.Target));
    }
}

internal static partial class RuntimeWorkflow
{
    internal static async Task<PreviewSupplementalEvidence> ReadPreviewSupplementalAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PreviewCapabilityRequest input, HttpClient http, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.PreviewClock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = pending.Token;
        try
        {
            var credential = dependencies.PreviewCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
            var validator = new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, dependencies.PreviewClock);
            var snapshot = await validator.CheckAsync(input, token);
            var sources = new List<PreviewSupplementalSourceEvidence>();
            foreach (var selected in snapshot.Capability.Configuration.Supplemental?.Hosts ?? [])
            {
                var request = new PreviewSupplementalRequest(input, selected.Kind);
                var expected = RuntimeVerbs.SupplementalAuthority(snapshot, request);
                var workload = new FactoryWorkloadCredential(credential, snapshot.Manifest.Identity.TenantId, snapshot.Council, dependencies.PreviewClock);
                var result = await PreviewSupplementalTransport.PostAsync<PreviewSupplementalSourceEvidence>(http,
                    snapshot.SupplementalEndpoints[selected.Kind] + "/evidence", selected.AudienceClientId, workload, request,
                    FactoryPreviewSupplementalValidation.SourceBytes, token);
                if (FactoryModelsValidation.Hash(result.Authority) != FactoryModelsValidation.Hash(expected))
                    throw new JsonException("Supplemental request/current authority changed.");
                FactoryPreviewSupplementalValidation.ValidateEvidence(expected, result.Evidence, dependencies.PreviewClock.GetUtcNow());
                if (result.Evidence.Proof.State == SupplementalState.ReadySupplemental)
                {
                    var targets = PreviewSupplementalNative.Targets(expected);
                    if (!result.NativeRevisions.Keys.Order(StringComparer.Ordinal).SequenceEqual(targets.Order(StringComparer.Ordinal))
                        || result.NativeRevisions.Values.Any(v => !FactorySourceValidation.Hash(v))
                        || result.Evidence.Items.Any(i => SourceContentPolicy.Sensitive(i.Content))) throw new JsonException();
                }
                sources.Add(result);
            }
            var current = await validator.CheckAsync(input, token);
            if (current.Revision != snapshot.Revision) throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringSupplementalRead");
            token.ThrowIfCancellationRequested();
            foreach (var source in sources)
                FactoryPreviewSupplementalValidation.ValidateEvidence(source.Authority, source.Evidence, dependencies.PreviewClock.GetUtcNow());
            var state = sources.Count == 0 ? SupplementalState.NotSelected
                : sources.Any(s => s.Evidence.Proof.State == SupplementalState.Unknown) ? SupplementalState.Unknown
                : sources.Any(s => s.Evidence.Proof.State == SupplementalState.Canceled) ? SupplementalState.Canceled
                : sources.FirstOrDefault(s => s.Evidence.Proof.State != SupplementalState.ReadySupplemental)?.Evidence.Proof.State ?? SupplementalState.ReadySupplemental;
            return new(input.RequestId, snapshot.GovernanceRevision, state, sources);
        }
        catch (PreviewCapabilityException) { throw; }
        catch (OperationCanceledException)
        { throw new PreviewCapabilityException(cancellationToken.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "SupplementalConsumptionCanceledOrUnconfirmed"); }
        catch (SupplementalSourceException e) when (e.Reason == SupplementalReason.CapacityExceeded)
        { throw new PreviewCapabilityException(FactoryCharterState.Partial, "PreviewSupplementalCapacityExceeded"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or HttpRequestException
            or IOException or Azure.RequestFailedException or AuthenticationFailedException or ModelCapabilityException)
        { throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewSupplementalConsumptionUnconfirmed"); }
    }
}

internal static class PreviewSupplementalTransport
{
    internal static async Task<T> PostAsync<T>(HttpClient http, string endpoint, string audience, TokenCredential credential,
        object input, int limit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["api://" + audience + "/.default"]), token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.Forbidden))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "SupplementalHostResponseUnconfirmed");
        using var body = await EvidencePayload.ReadAsync(response, token, limit);
        if (response.StatusCode != HttpStatusCode.OK && body.RootElement.TryGetProperty("reason", out _))
        {
            var failure = body.RootElement.Deserialize<PreviewContextFailure>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (failure.State is not (FactoryCharterState.Blocked or FactoryCharterState.Partial or FactoryCharterState.Failed
                or FactoryCharterState.Canceled or FactoryCharterState.Unknown)) throw new JsonException();
            throw new PreviewCapabilityException(failure.State, "CurrentSupplementalAuthorityOrEvidenceUnavailable");
        }
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new JsonException();
        var result = body.RootElement.Deserialize<T>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (response.StatusCode != HttpStatusCode.OK && result is not PreviewSupplementalSourceEvidence) throw new JsonException();
        if (result is PreviewSupplementalSourceEvidence evidence
            && (response.StatusCode == HttpStatusCode.OK) != (evidence.Evidence.Proof.State == SupplementalState.ReadySupplemental)) throw new JsonException();
        return result;
    }
}
