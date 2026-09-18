using System.Text.Json;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    public static WebApplication BuildLiveHost(FactoryHostSettings settings, RuntimeDependencies dependencies,
        string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
        var http = dependencies.PreviewHttp ?? PreviewHttp;
        builder.Services.AddSingleton(provider => new FactoryPreviewWorker(settings, dependencies, http,
            provider.GetRequiredService<ILogger<FactoryPreviewWorker>>()));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<FactoryPreviewWorker>());
        var app = builder.Build();
        app.MapPost("/exercise", (Func<HttpContext, Task<IResult>>)(context => Handle(context, true)));
        app.MapPost("/execution", (Func<HttpContext, Task<IResult>>)(context => Handle(context, false)));
        return app;

        async Task<IResult> Handle(HttpContext context, bool execute)
        {
            context.Response.Headers.CacheControl = "no-store";
            using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.PreviewClock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
            var phase = "authority";
            try
            {
                if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad")
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "NativeCallerRequired");
                var request = await context.Request.ReadFromJsonAsync<FactoryLiveRequest>(FactorySourceValidation.RecordJson, pending.Token)
                    ?? throw new JsonException();
                var credential = dependencies.PreviewCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var authority = new FactoryLiveAuthority(settings, dependencies, credential, http);
                var current = await authority.CheckAsync(request, execute, pending.Token);
                if (!NativeCaller(context, current.Preview.Manifest.Identity.AzureActorId, current.Preview.Manifest.Identity.TenantId, null))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ExplicitGovernanceOperatorExerciseRequired");
                var live = new FactoryLiveExecution(current, request, authority);
                phase = "worker-admission";
                var observation = await app.Services.GetRequiredService<FactoryPreviewWorker>()
                    .ObserveAsync(current.Preview, current.Request, execute, false, pending.Token, live);
                return Results.Json(new FactoryLiveObservation(1, observation.FactoryId, request.OperationId, current.Live.Plan.Fingerprint,
                    observation.ObservedAt, observation.Readiness, observation.Reason, observation.Evaluation),
                    new JsonSerializerOptions(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false },
                    statusCode: observation.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running or PreviewReadiness.Checking ? 202 : 200);
            }
            catch (PreviewCapabilityException e)
            { return Failure(e.State, e.Message, 403); }
            catch (OperationCanceledException)
            { return Failure(FactoryCharterState.Unknown, "OriginalLiveAdmissionOrWorkerUnconfirmedNoReplay", 409); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException or BadHttpRequestException
                or CosmosCheckpointException or FormatException or ModelCapabilityException or SupplementalSourceException
                or SourceAcquisitionException or IntentException or CharterAdoptionEvidenceException)
            { return Failure(FactoryCharterState.Unknown, "LiveExecutionUnconfirmedNoFallback:" + phase + ":"
                + (e is CosmosCheckpointException native ? native.Failure.ToString() : e.GetType().Name), 409); }
        }

        static IResult Failure(FactoryCharterState state, string reason, int status) =>
            Results.Json(new { State = state, Reason = reason, IsActivated = false, PublicationPermitted = false },
                new JsonSerializerOptions(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false }, statusCode: status);
    }
}
