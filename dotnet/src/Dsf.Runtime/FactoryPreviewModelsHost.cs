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
    internal const int PreviewModelRequestBytes = 128 * 1024;
    internal const int PreviewModelResponseBytes = 128 * 1024;

    public static WebApplication BuildPreviewModelsHost(FactoryHostSettings settings, RuntimeDependencies dependencies,
        string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = PreviewModelRequestBytes);
        var app = builder.Build();
        Func<HttpContext, Task<IResult>> evaluate = EvaluateAsync;
        app.MapPost("/evaluate", evaluate);
        return app;

        async Task<IResult> EvaluateAsync(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            var evaluated = false;
            try
            {
                if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad") throw new JsonException();
                using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.ModelClock);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
                var token = pending.Token;
                var input = await context.Request.ReadFromJsonAsync<PreviewModelRequest>(FactorySourceValidation.RecordJson, token) ?? throw new JsonException();
                ValidatePreviewModelRequest(input);
                var credential = dependencies.ModelCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var http = dependencies.ModelHttp ?? PreviewHttp;
                IFactoryPreviewCapability validator = new AzureFactoryPreviewCapability(settings, PreviewHostKind.ModelEvaluation, credential, http, dependencies.ModelClock);
                var current = await validator.CheckAsync(input.Preview, token);
                if (!NativeCaller(context, current.Council.PrincipalId!, current.Manifest.Identity.TenantId, current.Council.ClientId))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ExactCouncilModelCallerRequired");
                var models = current.Models ?? throw new JsonException();
                var workloads = new FactoryModelWorkloads(current.Manifest, models, credential, http, dependencies.ModelClock);
                var authority = await workloads.ReadAuthorityAsync(input.Preview.RequestId, token);
                ModelTextResult? text = null;
                ModelDeduplicationResult? groups = null;
                if (input.Route == "embedding") groups = await workloads.DeduplicateAsync(input.Items!, token);
                else if (input.Route == "synthesis") text = await workloads.SynthesizeAsync(input.Input!, token);
                else if (input.Route != "authority") text = await workloads.JudgeAsync(input.Route, input.Input!, token);
                evaluated = input.Route != "authority";
                if (input.Route != "authority") authority = await workloads.ReadAuthorityAsync(input.Preview.RequestId, token);
                var final = await validator.CheckAsync(input.Preview, token);
                if (final.Revision != current.Revision)
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewModelAuthorityChangedAfterEvaluation");
                var result = new PreviewModelResult(input.Preview.RequestId, FactoryModelsValidation.Hash(input), current.Revision, authority, text, groups);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result, FactoryGitHubJson.Options);
                if (bytes.Length > PreviewModelResponseBytes) throw new JsonException();
                return Results.Content(System.Text.Encoding.UTF8.GetString(bytes), "application/json");
            }
            catch (PreviewCapabilityException e)
            { return Failure(evaluated ? FactoryCharterState.Unknown : e.State, e.Message, 403); }
            catch (ModelCapabilityException e)
            {
                return Failure(evaluated ? FactoryCharterState.Unknown : e.Reason == ModelCapabilityReason.MalformedResponse ? FactoryCharterState.Failed
                    : e.Reason is ModelCapabilityReason.NetworkUnavailable or ModelCapabilityReason.InferenceOutcomeUnconfirmed
                        ? FactoryCharterState.Unknown : FactoryCharterState.Blocked,
                    "NativeModel" + e.Reason, 409);
            }
            catch (OperationCanceledException)
            { return Failure(context.RequestAborted.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "ModelDispatchCanceledOrUnconfirmed", 409); }
            catch (Exception e) when (e is HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException)
            { return Failure(FactoryCharterState.Unknown, "ModelDispatchUnconfirmed", 409); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or BadHttpRequestException or SupplementalSourceException)
            { return Failure(evaluated ? FactoryCharterState.Unknown : FactoryCharterState.Failed, "InvalidPreviewModelRequestOrResponse", 403); }
        }

        static IResult Failure(FactoryCharterState state, string reason, int status) =>
            Results.Json(new PreviewContextFailure(state, reason), FactoryGitHubJson.Options, statusCode: status);
    }

    internal static void ValidatePreviewModelRequest(PreviewModelRequest input)
    {
        if (input.Route == "authority")
        {
            if (input.Input is not null || input.Items is not null) throw new JsonException();
        }
        else if (input.Route == "embedding")
        {
            if (input.Input is not null || input.Items is null || input.Items.Count is < 1 or > 32) throw new JsonException();
        }
        else if (!FactoryModelsValidation.RouteNames.Contains(input.Route) || input.Items is not null
            || string.IsNullOrWhiteSpace(input.Input) || input.Input.Length > 32768) throw new JsonException();
        if (JsonSerializer.SerializeToUtf8Bytes(input, FactoryGitHubJson.Options).Length > PreviewModelRequestBytes) throw new JsonException();
    }
}
