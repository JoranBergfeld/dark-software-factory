using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Azure.Identity;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    private static readonly HttpClient SourceHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    public static WebApplication BuildSourceHost(FactoryHostSettings settings, RuntimeDependencies dependencies,
        string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();
        Func<HttpContext, Task<IResult>> acquire = context => HandleAsync(context, false);
        Func<HttpContext, Task<IResult>> status = context => HandleAsync(context, true);
        Func<HttpContext, Task<IResult>> materialize = context => HandleAsync(context, false, true);
        app.MapPost("/acquire", acquire);
        app.MapPost("/status", status);
        app.MapPost("/materialize", materialize);
        Func<HttpContext, Task<IResult>> infer = context => IntentAsync(context, false);
        Func<HttpContext, Task<IResult>> intentStatus = context => IntentAsync(context, true);
        app.MapPost("/infer", infer);
        app.MapPost("/intent-status", intentStatus);
        return app;

        async Task<IResult> IntentAsync(HttpContext context, bool status)
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                using var deadline = new CancellationTokenSource(FactoryIntentLimits.AdvanceBudget, dependencies.SourceClock);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
                var http = dependencies.SourceHttp ?? SourceHttp;
                var credential = dependencies.SourceCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var access = new FactorySourceAccess(settings, credential, http, dependencies.SourceClock);
                await access.CheckAsync(pending.Token);
                if (!NativeCaller(context, access.Manifest.Identity.AzureActorId, access.Manifest.Identity.TenantId, null))
                    throw new JsonException();
                var request = await context.Request.ReadFromJsonAsync<IntentRequest>(FactorySourceValidation.RecordJson, pending.Token) ?? throw new JsonException();
                var result = await RuntimeWorkflow.InferCharterAsync(access, http, dependencies.SourceClock, request, status, pending.Token);
                return Results.Json(result, FactoryGitHubJson.Options, statusCode: result.State == IntentState.DraftReady ? 200 : 409);
            }
            catch (IntentException e) { return Error(e.State, e.Message); }
            catch (SourceAcquisitionException e) { return Error(FactoryIntentWorkflow.Map(e.State), e.Message); }
            catch (OperationCanceledException)
            { return Error(context.RequestAborted.IsCancellationRequested ? IntentState.Canceled : IntentState.Partial,
                "Canceled or time budget exhausted; issued model dispatch may remain Unknown. Inspect private intent status; no forced retry."); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException
                or HttpRequestException or Azure.RequestFailedException or AuthenticationFailedException or ModelCapabilityException
                or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { return Error(IntentState.Blocked, "Current verified model/source authority and native operator required. No draft released; provider details withheld."); }

            static IResult Error(IntentState state, string detail) => Results.Json(
                new IntentResult(state, detail, null, null, null, [], [], [], null), FactoryGitHubJson.Options, statusCode: 409);
        }

        async Task<IResult> HandleAsync(HttpContext context, bool status, bool materialize = false)
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                using var deadline = new CancellationTokenSource(FactorySourceValidation.AdvanceBudget, dependencies.SourceClock);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
                if (!Guid.TryParse(settings.ManagedIdentityClientId, out var client) || client == Guid.Empty
                    || !FactorySourceValidation.Hash(settings.ProfilePin)) throw new JsonException();
                var http = dependencies.SourceHttp ?? SourceHttp;
                var credential = dependencies.SourceCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var access = new FactorySourceAccess(settings, credential, http, dependencies.SourceClock);
                await access.CheckAsync(pending.Token);
                if (!NativeCaller(context, materialize ? access.Code.PrincipalId! : access.Manifest.Identity.AzureActorId,
                    access.Manifest.Identity.TenantId, materialize ? access.Code.ClientId : null))
                    throw new JsonException();
                var inventory = new FactorySourceInventory(access, http, dependencies.SourceClock);
                if (materialize)
                {
                    var reference = await context.Request.ReadFromJsonAsync<SourceChunkReference>(FactoryGitHubJson.Options, pending.Token) ?? throw new JsonException();
                    var chunk = await inventory.MaterializeAsync(reference, pending.Token);
                    context.Response.RegisterForDispose(chunk);
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Bytes(chunk.Content, "application/octet-stream");
                }
                var request = await context.Request.ReadFromJsonAsync<SourceAcquireRequest>(FactoryGitHubJson.Options, pending.Token) ?? throw new JsonException();
                if (request.Page < 0 || request.Page > FactorySourceValidation.MaximumNodes / FactorySourceValidation.PageSize
                    || status && request.StartNewSnapshot) throw new JsonException("Invalid coverage request.");
                var result = status ? await inventory.StatusAsync(request.Page, pending.Token) : await inventory.AdvanceAsync(request, pending.Token);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(result, FactoryGitHubJson.Options, statusCode: result.State == SourceState.Acquired ? 200 : 409);
            }
            catch (SourceAcquisitionException e)
            { return Results.Json(new SourceInventoryResult(e.State, e.Message, null, [], null), FactoryGitHubJson.Options, statusCode: 409); }
            catch (OperationCanceledException)
            { return Results.Json(new SourceInventoryResult(context.RequestAborted.IsCancellationRequested ? SourceState.Canceled : SourceState.Partial,
                "Canceled or TimeBudget exhausted; final progress/hold unconfirmed. Read trusted remote status before resuming.", null, [], null), FactoryGitHubJson.Options, statusCode: 409); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            { return Results.Json(new SourceInventoryResult(SourceState.Partial,
                "Sensitive-content screening time limit; no source released, inspect remote checkpoint.", null, [], null), FactoryGitHubJson.Options, statusCode: 409); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or HttpRequestException or Azure.RequestFailedException or AuthenticationFailedException or ModelCapabilityException or FormatException)
            { return Results.Json(new SourceInventoryResult(SourceState.Blocked, "Current approved source authority, native operator, identity, grants and progress required; no source result released. Provider bodies withheld.", null, [], null), FactoryGitHubJson.Options, statusCode: 403); }
        }
    }
}
