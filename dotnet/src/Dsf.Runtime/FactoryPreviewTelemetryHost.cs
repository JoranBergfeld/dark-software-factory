using System.Text;
using System.Text.Json;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    public static WebApplication BuildPreviewTelemetryHost(FactoryHostSettings settings, RuntimeDependencies dependencies,
        string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
        var app = builder.Build();
        app.MapPost("/evidence", (Func<HttpContext, Task<IResult>>)HandleAsync);
        return app;

        async Task<IResult> HandleAsync(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            var clock = dependencies.TelemetryClock;
            using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
            var token = pending.Token;
            try
            {
                if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad")
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "NativeTelemetryCallerRequired");
                var input = await context.Request.ReadFromJsonAsync<PreviewCapabilityRequest>(FactorySourceValidation.RecordJson, token) ?? throw new JsonException();
                var credential = dependencies.TelemetryCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var http = dependencies.TelemetryHttp ?? PreviewHttp;
                var validator = new AzureFactoryPreviewCapability(settings, PreviewHostKind.TelemetryEvidence, credential, http, clock);
                var snapshot = await validator.CheckAsync(input, token);
                if (!NativeCaller(context, snapshot.Council.PrincipalId!, snapshot.Manifest.Identity.TenantId, snapshot.Council.ClientId))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedCouncilTelemetryCallerRequired");
                var selection = snapshot.Capability.Configuration.Telemetry!;
                var capture = await new FactoryTelemetryQuery(credential, http, clock).ObserveEvidenceAsync(snapshot.Manifest,
                    snapshot.Telemetry! with { CheckId = input.RequestId }, selection, snapshot.TelemetryEffects, token);
                FactoryPreviewTelemetryValidation.ValidateCapture(snapshot.Telemetry!, selection, input.RequestId, capture, clock.GetUtcNow());
                var current = await validator.CheckAsync(input, token);
                if (current.Revision != snapshot.Revision) throw new PreviewCapabilityException(FactoryCharterState.Blocked, "TelemetryCapabilityChangedDuringRead");
                token.ThrowIfCancellationRequested();
                FactoryPreviewTelemetryValidation.ValidateCapture(snapshot.Telemetry!, selection, input.RequestId, capture, clock.GetUtcNow());
                var result = new PreviewTelemetryEvidence(input.RequestId, snapshot.GovernanceRevision, snapshot.Revision,
                    FactoryModelsValidation.Hash(selection), capture);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result, FactoryGitHubJson.Options);
                if (bytes.Length > FactoryPreviewTelemetryValidation.ResponseBytes)
                    throw new PreviewCapabilityException(FactoryCharterState.Partial, "TelemetryEvidenceCapacityExceeded");
                return Results.Content(Encoding.UTF8.GetString(bytes), "application/json",
                    statusCode: capture.Proof.State == TelemetryState.Ready ? 200 : 409);
            }
            catch (PreviewCapabilityException e) { return Error(e.State, e.Message); }
            catch (SupplementalSourceException e) when (e.Reason == SupplementalReason.CapacityExceeded)
            { return Error(FactoryCharterState.Partial, "TelemetryEvidenceCapacityExceeded"); }
            catch (OperationCanceledException)
            { return Error(context.RequestAborted.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "TelemetryEvidenceCanceledOrUnconfirmed"); }
            catch (Azure.RequestFailedException e) when (e.Status is 401 or 403 or 404)
            { return Error(FactoryCharterState.Blocked, "CurrentTelemetryAuthorityUnavailable"); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException or BadHttpRequestException or ModelCapabilityException)
            { return Error(FactoryCharterState.Unknown, "TelemetryEvidenceUnconfirmed"); }
        }

        static IResult Error(FactoryCharterState state, string reason) =>
            Results.Json(new PreviewContextFailure(state, reason), FactoryGitHubJson.Options, statusCode: state == FactoryCharterState.Blocked ? 403 : 409);
    }
}
