using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    private static readonly HttpClient PreviewHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static WebApplication BuildPreviewHost(FactoryHostSettings settings, RuntimeDependencies dependencies, PreviewHostKind kind,
        string host = DefaultHost, int port = DefaultPort)
    {
        if (kind is not (PreviewHostKind.Council or PreviewHostKind.SourceContext)) throw new ArgumentOutOfRangeException(nameof(kind));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
        var app = builder.Build();
        Func<HttpContext, Task<IResult>> handler = context => HandleAsync(context);
        app.MapPost(kind == PreviewHostKind.SourceContext ? "/council-context" : "/source-context", handler);
        if (kind == PreviewHostKind.Council)
        {
            Func<HttpContext, Task<IResult>> modelAuthority = context => HandleAsync(context, modelAuthority: true);
            app.MapPost("/model-authority", modelAuthority);
            app.MapPost("/telemetry-evidence", (Func<HttpContext, Task<IResult>>)(context => HandleAsync(context, telemetryEvidence: true)));
            app.MapPost("/supplemental-evidence", (Func<HttpContext, Task<IResult>>)(context => HandleAsync(context, supplementalEvidence: true)));
            app.MapPost("/supplemental-authority", (Func<HttpContext, Task<IResult>>)(context => HandleAsync(context, supplementalAuthority: true)));
            app.MapPost("/inputs", (Func<HttpContext, Task<IResult>>)(context => HandleAsync(context, inputs: true)));
            app.MapPost("/prepare-evaluation", (Func<HttpContext, Task<IResult>>)(context => HandleAsync(context, prepareEvaluation: true)));
            app.MapPost("/evaluate", (Func<HttpContext, Task<IResult>>)(context => HandleAsync(context, evaluate: true)));
            app.MapPost("/evaluation", (Func<HttpContext, Task<IResult>>)(context => HandleAsync(context, evaluationStatus: true)));
        }
        return app;

        async Task<IResult> HandleAsync(HttpContext context, bool modelAuthority = false, bool telemetryEvidence = false,
            bool supplementalEvidence = false, bool supplementalAuthority = false, bool inputs = false, bool prepareEvaluation = false, bool evaluate = false,
            bool evaluationStatus = false)
        {
            context.Response.Headers.CacheControl = "no-store";
            var clock = kind == PreviewHostKind.SourceContext ? dependencies.SourceClock : dependencies.PreviewClock;
            using var deadline = new CancellationTokenSource(evaluate ? TimeSpan.FromSeconds(FactoryPreviewExecution.Policy.RequestSeconds)
                : inputs || evaluationStatus ? TimeSpan.FromSeconds(FactoryPreviewInputValidation.Policy.RequestSeconds)
                : FactoryPreviewValidation.RequestBudget, clock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
            try
            {
                if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad") throw new PreviewCapabilityException(FactoryCharterState.Blocked, "NativeCallerRequired");
                var token = pending.Token;
                var optional = supplementalAuthority ? await context.Request.ReadFromJsonAsync<PreviewSupplementalRequest>(FactorySourceValidation.RecordJson, token)
                    ?? throw new JsonException() : null;
                var request = optional?.Preview ?? await context.Request.ReadFromJsonAsync<PreviewCapabilityRequest>(FactorySourceValidation.RecordJson, token) ?? throw new JsonException();
                var credential = (kind == PreviewHostKind.SourceContext ? dependencies.SourceCredential : dependencies.PreviewCredential)
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var http = (kind == PreviewHostKind.SourceContext ? dependencies.SourceHttp : dependencies.PreviewHttp) ?? PreviewHttp;
                IFactoryPreviewCapability authority = new AzureFactoryPreviewCapability(settings, kind, credential, http, clock);
                var capability = await authority.CheckAsync(request, token);
                if (optional is not null)
                {
                    var source = SupplementalAuthority(capability, optional);
                    if (!NativeCaller(context, source.Workload.SourcePrincipalId, source.Workload.TenantId, source.Workload.SourceClientId))
                        throw new PreviewCapabilityException(FactoryCharterState.Blocked, "SelectedSupplementalCallerRequired");
                    return Results.Json(source, FactoryGitHubJson.Options);
                }
                if (!NativeCaller(context, kind == PreviewHostKind.SourceContext ? capability.Council.PrincipalId! : capability.Manifest.Identity.AzureActorId,
                    capability.Manifest.Identity.TenantId, kind == PreviewHostKind.SourceContext ? capability.Council.ClientId : null))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewCallerRequired");
                if (prepareEvaluation)
                    return Results.Json(await PreparePreviewExecutionAsync(settings, dependencies, request, token), FactoryGitHubJson.Options);
                if (evaluate || evaluationStatus)
                {
                    var evaluation = evaluate ? await ExecutePreviewAsync(settings, dependencies, request, token)
                        : await InspectPreviewAsync(settings, dependencies, request, token);
                    var evaluationBytes = JsonSerializer.SerializeToUtf8Bytes(evaluation, new JsonSerializerOptions(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false });
                    return evaluationBytes.Length > 4 * 1024 * 1024 ? Error(FactoryCharterState.Partial, "PreviewEvaluationResponseCapacityExceeded", 409)
                        : Results.Content(System.Text.Encoding.UTF8.GetString(evaluationBytes), "application/json");
                }
                if (inputs)
                {
                    var acquired = await AcquirePreviewInputsAsync(settings, dependencies, request, token);
                    if (acquired.Snapshot.CapabilityRevision != capability.Revision)
                        throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringInputRead");
                    return Results.Json(acquired.Snapshot, FactoryGitHubJson.Options);
                }
                if (modelAuthority)
                {
                    var models = await OpenPreviewModelsAsync(settings, dependencies, request, token);
                    var observation = models.Observations.Single();
                    if (observation.CapabilityRevision != capability.Revision)
                        throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringModelRead");
                    return Results.Json(new PreviewModelAuthorityContext(request.RequestId, capability.Revision, observation.Authority), FactoryGitHubJson.Options);
                }
                if (telemetryEvidence)
                {
                    var evidence = await ReadPreviewTelemetryAsync(settings, dependencies, request, token);
                    if (evidence.GovernanceRevision != capability.GovernanceRevision)
                        throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringTelemetryRead");
                    return Results.Json(evidence, FactoryGitHubJson.Options,
                        statusCode: evidence.Capture.Proof.State == TelemetryState.Ready ? 200 : 409);
                }
                if (supplementalEvidence)
                {
                    var evidence = await ReadPreviewSupplementalAsync(settings, dependencies, request, token);
                    if (evidence.GovernanceRevision != capability.GovernanceRevision)
                        throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringSupplementalRead");
                    var supplementalBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, FactoryGitHubJson.Options);
                    if (supplementalBytes.Length > FactoryPreviewSupplementalValidation.ResponseBytes)
                        return Error(FactoryCharterState.Partial, "PreviewSupplementalCapacityExceeded", 409);
                    return Results.Content(System.Text.Encoding.UTF8.GetString(supplementalBytes), "application/json",
                        statusCode: evidence.State is SupplementalState.NotSelected or SupplementalState.ReadySupplemental ? 200 : 409);
                }
                SourceCharterContextResult result;
                if (kind == PreviewHostKind.SourceContext)
                    result = await ReadSourceCharterContextAsync(settings, dependencies with { SourceHttp = http }, token);
                else
                {
                    var workload = new FactoryWorkloadCredential(credential, capability.Manifest.Identity.TenantId, capability.Council, clock);
                    var remote = await ReadPreviewSourceAsync(capability, request, workload, http, token);
                    result = remote.Context;
                }
                if (result.State == FactoryCharterState.Adopted)
                {
                    ValidatePreviewContext(capability, result);
                    var current = await authority.CheckAsync(request, token);
                    if (current.Revision != capability.Revision) throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringContextRead");
                }
                else if (result.Adoption is not null || result.Current is not null)
                    throw new PreviewCapabilityException(FactoryCharterState.Unknown, "UnconfirmedSourceContextEnvelope");
                var response = new PreviewSourceContext(request.RequestId, request.OperationId, request.ApprovalFingerprint, capability.Revision, result);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response, FactoryGitHubJson.Options);
                if (bytes.Length > FactoryPreviewValidation.ResponseBytes)
                    return Error(FactoryCharterState.Partial, "CompletePreviewContextExceedsResponseBudget", 409);
                return Results.Content(System.Text.Encoding.UTF8.GetString(bytes), "application/json",
                    statusCode: result.State == FactoryCharterState.Adopted ? 200 : 409);
            }
            catch (Exception e) when ((inputs || evaluate || evaluationStatus) && e is PreviewCapabilityException or OperationCanceledException
                && deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            { return Error(FactoryCharterState.Unknown, evaluate ? "PreviewEvaluationDeadlineExceeded" : "PreviewInputCollectionDeadlineExceeded", 409); }
            catch (CosmosCheckpointException exception)
            { return Error(exception.Failure == CosmosCheckpointFailure.Capacity ? FactoryCharterState.Partial : FactoryCharterState.Unknown,
                "NativePreviewState" + exception.Failure, 409); }
            catch (PreviewCapabilityException e) { return Error(e.State, e.Message, 403); }
            catch (SupplementalSourceException e) when (e.Reason == SupplementalReason.CapacityExceeded)
            { return Error(FactoryCharterState.Partial, "PreviewContextCapacityExceeded", 409); }
            catch (OperationCanceledException)
            { return Error(context.RequestAborted.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Partial, "PreviewContextCanceledOrTimedOut", 409); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException or BadHttpRequestException or ModelCapabilityException)
            { return Error(FactoryCharterState.Unknown, "PreviewContextUnconfirmed", 403); }
        }

        static IResult Error(FactoryCharterState state, string reason, int status) =>
            Results.Json(new PreviewContextFailure(state, reason), FactoryGitHubJson.Options, statusCode: status);
    }

    internal static async Task<PreviewSourceContext> ReadPreviewSourceAsync(PreviewCapabilitySnapshot capability, PreviewCapabilityRequest input,
        TokenCredential credential, HttpClient http, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, capability.SourceContextEndpoint + "/council-context")
        { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["api://" + capability.Capability.Configuration.AudienceClientId + "/.default"]), token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.Forbidden))
            throw new PreviewCapabilityException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
                ? FactoryCharterState.Blocked : FactoryCharterState.Unknown, "CurrentSourceContextAccessRejected");
        using var document = await EvidencePayload.ReadAsync(response, token,
            response.StatusCode == HttpStatusCode.Forbidden ? 4096 : FactoryPreviewValidation.ResponseBytes);
        if (response.StatusCode != HttpStatusCode.OK && !document.RootElement.TryGetProperty("context", out _))
        {
            var error = document.RootElement.Deserialize<PreviewContextFailure>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (!Unconfirmed(error.State)) throw new JsonException();
            throw new PreviewCapabilityException(error.State, "CurrentSourceContextAccessRejected");
        }
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new JsonException();
        var result = document.RootElement.Deserialize<PreviewSourceContext>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (result.RequestId != input.RequestId || result.OperationId != input.OperationId
            || result.ApprovalFingerprint != input.ApprovalFingerprint || result.CapabilityRevision != capability.Revision
            || result.Context.State != FactoryCharterState.Adopted && !Unconfirmed(result.Context.State)
            || response.StatusCode == HttpStatusCode.OK != (result.Context.State == FactoryCharterState.Adopted))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "SourceContextRequestBindingChanged");
        return result;
    }

    private static bool Unconfirmed(FactoryCharterState state) =>
        state is FactoryCharterState.Partial or FactoryCharterState.Blocked or FactoryCharterState.Failed or FactoryCharterState.Canceled or FactoryCharterState.Unknown;

    internal static void ValidatePreviewContext(PreviewCapabilitySnapshot capability, SourceCharterContextResult result)
    {
        if (result.Adoption is null || result.Current is null
            || result.Adoption.Adoption.Revision != capability.Capability.Configuration.AdoptedCharterRevision
            || result.Adoption.Adoption.FactoryId != capability.Manifest.Identity.FactoryId
            || result.Adoption.Original.Review.RepositoryId != capability.Manifest.Identity.RepositoryId
            || result.Current.Source.Snapshot.RepositoryId != capability.Manifest.Identity.RepositoryId)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewAdoptionChanged");
        FactoryCharterAdoptionReader.RequireCurrent(result.Adoption.Adoption, result.Current, capability.Manifest.Identity.ProductKey);
    }
}
