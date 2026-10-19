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
        app.MapPost("/decide", (Func<HttpContext, Task<IResult>>)(context => Decide(context, true)));
        app.MapPost("/decision", (Func<HttpContext, Task<IResult>>)(context => Decide(context, false)));
        app.MapPost("/activation-capability", (Func<HttpContext, Task<IResult>>)(context => Handle(context, false)));
        return app;

        async Task<IResult> Decide(HttpContext context, bool execute)
        {
            context.Response.Headers.CacheControl = "no-store";
            using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.PreviewClock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
            try
            {
                if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad")
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "NativeCallerRequired");
                var request = await context.Request.ReadFromJsonAsync<FactoryDecisionRequest>(FactorySourceValidation.RecordJson, pending.Token) ?? throw new JsonException();
                var credential = dependencies.PreviewCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                if (!execute)
                {
                    var inspection = await FactoryDecisionInspection.OpenAsync(settings, dependencies, request, credential, http, pending.Token);
                    var original = inspection.Live.Authority.Preview;
                    if (!NativeCaller(context, original.Manifest.Identity.AzureActorId, original.Manifest.Identity.TenantId, null)
                        && !NativeCaller(context, original.Council.PrincipalId!, original.Manifest.Identity.TenantId, original.Council.ClientId))
                        throw new PreviewCapabilityException(FactoryCharterState.Blocked, "OriginalGovernanceOperatorOrCouncilCallerRequired");
                    var issued = await app.Services.GetRequiredService<FactoryPreviewWorker>().InspectIssuedAsync(inspection, pending.Token);
                    if (issued is not null)
                        return Results.Json(issued, OutputJson,
                            statusCode: issued.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running ? 202 : 200);
                }
                var decision = await FactoryDecisionExecution.AdmitAuthorityAsync(settings, dependencies, request, credential, http, pending.Token);
                var live = await decision.LiveAsync(settings, dependencies, credential, pending.Token);
                var factory = live.Authority.Preview.Manifest.Identity;
                if (!NativeCaller(context, factory.AzureActorId, factory.TenantId, null)
                    && !NativeCaller(context, live.Authority.Preview.Council.PrincipalId!, factory.TenantId, live.Authority.Preview.Council.ClientId))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "OriginalGovernanceOperatorOrCouncilCallerRequired");
                var observed = await app.Services.GetRequiredService<FactoryPreviewWorker>().ObserveAsync(
                    live.Authority.Preview, live.Authority.Request, execute, false, pending.Token, live);
                return Results.Json(observed, OutputJson, statusCode: observed.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running
                    or PreviewReadiness.Checking ? 202 : 200);
            }
            catch (PreviewCapabilityException e) { return Failure(e.State, e.Message, 403); }
            catch (Exception e) when (e is OperationCanceledException or JsonException or InvalidOperationException or ArgumentException
                or KeyNotFoundException or HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException
                or BadHttpRequestException or CosmosCheckpointException or FormatException or ModelCapabilityException or SupplementalSourceException
                or SourceAcquisitionException or IntentException or CharterAdoptionEvidenceException)
            { return Failure(FactoryCharterState.Unknown, "OriginalDecisionAdmissionUnconfirmedNoAutomaticReplay", 409); }
        }

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
                if (context.Request.Path == "/execution")
                {
                    var inspection = await FactoryDecisionInspection.OpenAsync(settings, dependencies, new(request.OperationId, true),
                        credential, http, pending.Token, request.RolloutFingerprint, requireCurrentReadiness: true);
                    var original = inspection.Live.Authority;
                    if (!NativeCaller(context, original.Preview.Manifest.Identity.AzureActorId, original.Preview.Manifest.Identity.TenantId, null))
                        throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ExplicitGovernanceOperatorExerciseRequired");
                    var issued = await app.Services.GetRequiredService<FactoryPreviewWorker>().InspectIssuedAsync(inspection, pending.Token);
                    if (issued is not null)
                        return Results.Json(new FactoryLiveObservation(1, issued.FactoryId, request.OperationId, original.Live.Plan.Fingerprint,
                            issued.ObservedAt, issued.Readiness, issued.Reason, issued.Evaluation), OutputJson,
                            statusCode: issued.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running ? 202 : 200);
                }
                var capability = context.Request.Path == "/activation-capability";
                var authority = new FactoryLiveAuthority(settings, dependencies, credential, http,
                    capability ? new FactoryInspectionAuthority() : null);
                var current = await authority.CheckAsync(request, false, pending.Token);
                if (!NativeCaller(context, current.Preview.Manifest.Identity.AzureActorId, current.Preview.Manifest.Identity.TenantId, null)
                    && !(capability && NativeCaller(context, current.Preview.Council.PrincipalId!, current.Preview.Manifest.Identity.TenantId, current.Preview.Council.ClientId)))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ExplicitGovernanceOperatorExerciseRequired");
                if (capability)
                    return Results.Json(new FactoryDecisionCapability(1, current.Preview.Manifest.Identity.FactoryId, current.Live.Plan.Fingerprint,
                        FactoryPromotion.Workflow, FactoryPreviewExecution.Policy), OutputJson);
                FactoryLiveExecution live;
                var gateway = new AzureCosmosDocumentGateway(new FactoryWorkloadCredential(credential, current.Preview.Manifest.Identity.TenantId,
                    current.Preview.Council, dependencies.PreviewClock), http);
                var identity = current.Preview.Manifest.Identity;
                var indexed = execute || await gateway.ReadCheckpointAsync("https://" + FactoryPreviewValidation.Prefix(identity.FactoryId)
                    + "-cosmos.documents.azure.com", identity.ProductKey, FactoryPreviewValidation.PreviewContainer, identity.ProductKey,
                    FactoryPromotion.DecisionId(identity.FactoryId, request.OperationId), pending.Token) is not null;
                if (indexed)
                {
                    var decision = await FactoryDecisionExecution.AdmitAuthorityAsync(settings, dependencies, new(request.OperationId, true),
                        credential, http, pending.Token);
                    live = await decision.LiveAsync(settings, dependencies, credential, pending.Token);
                }
                else live = new FactoryLiveExecution(current, request, authority);
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
