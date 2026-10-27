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
    private static readonly HttpClient SupplementalHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    public static WebApplication BuildSupplementalHost(FactoryHostSettings settings, SupplementalHostConfiguration configuration,
        RuntimeDependencies dependencies, string host = DefaultHost, int port = DefaultPort)
    {
        FactorySupplementalValidation.ValidateHostConfiguration(configuration);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024);
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();
        Func<HttpContext, Task<IResult>> proof = context => ObserveAsync(context, result => result.Proof);
        Func<HttpContext, Task<IResult>> evidence = context => ObserveAsync(context, result => result);
        app.MapPost("/proof", proof);
        app.MapPost("/evidence", evidence);
        return app;

        async Task<IResult> ObserveAsync(HttpContext context, Func<SupplementalEvidence, object> project)
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (!NativeCaller(context, configuration.OperatorPrincipalId, configuration.TenantId, null)
                    || settings.ManagedIdentityClientId != configuration.SourceClientId
                    || settings.ProfilePin.Length != 64 || !settings.ProfilePin.All(char.IsAsciiHexDigit)
                    || settings.ProductEndpoint != "https://dsfo-" + configuration.FactoryId.ToString("N") + ".azconfig.io")
                    throw new JsonException();
                var request = await context.Request.ReadFromJsonAsync<SupplementalProofRequest>(FactoryGitHubJson.Options, context.RequestAborted)
                    ?? throw new JsonException();
                var credential = dependencies.SourceCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var source = new FactorySupplementalEvidence(configuration, credential, dependencies.SourceHttp ?? SupplementalHttp, dependencies.SourceClock);
                var result = await RuntimeWorkflow.ObserveSupplementalAsync(source, request, context.RequestAborted);
                return Results.Json(project(result), FactoryGitHubJson.Options,
                    statusCode: result.Proof.State == SupplementalState.ReadySupplemental ? 200 : 409);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or BadHttpRequestException)
            { return Results.Json(new { state = "Blocked", detail = "Exact native authenticated operator and pinned supplemental request required; no proof released." }, statusCode: 403); }
        }
    }
}
