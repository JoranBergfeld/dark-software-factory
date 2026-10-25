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
    public static Task<PreviewTelemetryEvidence> ReadPreviewTelemetryAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.ReadPreviewTelemetryAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token);
}

internal static partial class RuntimeWorkflow
{
    internal static async Task<PreviewTelemetryEvidence> ReadPreviewTelemetryAsync(FactoryHostSettings settings,
        RuntimeDependencies dependencies, PreviewCapabilityRequest input, HttpClient http, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.PreviewClock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = pending.Token;
        try
        {
            var credential = dependencies.PreviewCredential
                ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
            var validator = new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, dependencies.PreviewClock);
            var snapshot = await validator.CheckAsync(input, token);
            var selection = snapshot.Capability.Configuration.Telemetry ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked,
                "PreviewTelemetryProjectionRequired:dsf/factory/preview.configuration.telemetry");
            if (snapshot.Telemetry is null || snapshot.TelemetryEndpoint is null) throw new JsonException();
            var workload = new FactoryWorkloadCredential(credential, snapshot.Manifest.Identity.TenantId, snapshot.Council, dependencies.PreviewClock);
            using var request = new HttpRequestMessage(HttpMethod.Post, snapshot.TelemetryEndpoint + "/evidence")
            { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await workload.GetTokenAsync(
                new TokenRequestContext(["api://" + selection.AudienceClientId + "/.default"]), token)).Token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.Forbidden))
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "TelemetryEvidenceTransportUnconfirmed");
            using var document = await EvidencePayload.ReadAsync(response, token, FactoryPreviewTelemetryValidation.ResponseBytes);
            if (!document.RootElement.TryGetProperty("capture", out _))
            {
                var failure = document.RootElement.Deserialize<PreviewContextFailure>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (response.StatusCode == HttpStatusCode.OK || failure.State is not (FactoryCharterState.Partial or FactoryCharterState.Blocked
                    or FactoryCharterState.Failed or FactoryCharterState.Canceled or FactoryCharterState.Unknown)) throw new JsonException();
                throw new PreviewCapabilityException(failure.State, "TelemetryEvidenceNotConfirmed");
            }
            var result = document.RootElement.Deserialize<PreviewTelemetryEvidence>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (result.RequestId != input.RequestId || result.GovernanceRevision != snapshot.GovernanceRevision
                || result.SelectionRevision != FactoryModelsValidation.Hash(selection) || !FactorySourceValidation.Hash(result.SourceAuthorityRevision)
                || (response.StatusCode == HttpStatusCode.OK) != (result.Capture.Proof.State == TelemetryState.Ready)
                || response.StatusCode == HttpStatusCode.Forbidden) throw new JsonException("Unbound telemetry response.");
            FactoryPreviewTelemetryValidation.ValidateCapture(snapshot.Telemetry, selection, input.RequestId, result.Capture, dependencies.PreviewClock.GetUtcNow());
            TelemetryContentPolicy.RequireSafe(result.Capture);
            var current = await validator.CheckAsync(input, token);
            if (current.Revision != snapshot.Revision) throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringTelemetryRead");
            token.ThrowIfCancellationRequested();
            FactoryPreviewTelemetryValidation.ValidateCapture(snapshot.Telemetry, selection, input.RequestId, result.Capture, dependencies.PreviewClock.GetUtcNow());
            return result;
        }
        catch (PreviewCapabilityException) { throw; }
        catch (OperationCanceledException)
        { throw new PreviewCapabilityException(cancellationToken.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "TelemetryEvidenceCanceledOrUnconfirmed"); }
        catch (SupplementalSourceException e) when (e.Reason == SupplementalReason.CapacityExceeded)
        { throw new PreviewCapabilityException(FactoryCharterState.Partial, "TelemetryEvidenceCapacityExceeded"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException or ModelCapabilityException)
        { throw new PreviewCapabilityException(FactoryCharterState.Unknown, "TelemetryEvidenceUnconfirmed"); }
    }
}
