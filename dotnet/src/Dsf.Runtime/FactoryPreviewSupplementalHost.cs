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
    public static WebApplication BuildPreviewSupplementalHost(FactoryHostSettings settings, SupplementalHostConfiguration configuration,
        PreviewSupplementalCoordinator coordinator, RuntimeDependencies dependencies, string host = DefaultHost, int port = DefaultPort)
    {
        FactorySupplementalValidation.ValidateHostConfiguration(configuration);
        if (!Uri.TryCreate(coordinator.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || !endpoint.IsDefaultPort
            || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !endpoint.Host.EndsWith(".azurecontainerapps.io", StringComparison.Ordinal)
            || new[] { coordinator.AudienceClientId, coordinator.PrincipalId, coordinator.ClientId }
                .Any(value => !Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty))
            throw new JsonException("Exact approved council authority endpoint and caller required.");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
        var app = builder.Build();
        app.MapPost("/evidence", (Func<HttpContext, Task<IResult>>)ReadAsync);
        return app;

        async Task<IResult> ReadAsync(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            var clock = dependencies.SourceClock;
            using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
            var token = pending.Token;
            try
            {
                if (settings.ManagedIdentityClientId != configuration.SourceClientId
                    || settings.ProductEndpoint != "https://dsfo-" + configuration.FactoryId.ToString("N") + ".azconfig.io"
                    || !NativeCaller(context, coordinator.PrincipalId, configuration.TenantId, coordinator.ClientId))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedCouncilSupplementalCallerRequired");
                var input = await context.Request.ReadFromJsonAsync<PreviewSupplementalRequest>(FactorySourceValidation.RecordJson, token) ?? throw new JsonException();
                if (input.Kind != configuration.Source.Kind) throw new JsonException();
                var credential = dependencies.SourceCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var workload = new FactoryWorkloadCredential(credential, configuration.TenantId, configuration.SourcePrincipalId,
                    configuration.SourceClientId, configuration.SourceIdentityId, clock, requireGuidV2Audience: true);
                var http = dependencies.SourceHttp ?? PreviewHttp;
                var authority = await AuthorityAsync();
                var native = new PreviewSupplementalNative(authority, workload, http);
                await native.CheckAsync(token);
                var evidence = await RuntimeWorkflow.ObserveSupplementalAsync(new FactorySupplementalEvidence(configuration, credential, http, clock),
                    new(input.Preview.RequestId, configuration.ConfigurationRevision), token);
                await native.CheckAsync(token);
                if (FactoryModelsValidation.Hash(await AuthorityAsync()) != FactoryModelsValidation.Hash(authority))
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "SupplementalCapabilityChangedDuringRead");
                token.ThrowIfCancellationRequested();
                FactoryPreviewSupplementalValidation.ValidateEvidence(authority, evidence, clock.GetUtcNow());
                var result = new PreviewSupplementalSourceEvidence(authority, evidence, native.Revisions);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result, FactoryGitHubJson.Options);
                if (bytes.Length > FactoryPreviewSupplementalValidation.SourceBytes)
                    throw new PreviewCapabilityException(FactoryCharterState.Partial, "PreviewSupplementalCapacityExceeded");
                return Results.Content(Encoding.UTF8.GetString(bytes), "application/json",
                    statusCode: evidence.Proof.State == SupplementalState.ReadySupplemental ? 200 : 409);

                async Task<PreviewSupplementalAuthority> AuthorityAsync()
                {
                    var current = await PreviewSupplementalTransport.PostAsync<PreviewSupplementalAuthority>(http,
                        coordinator.Endpoint.TrimEnd('/') + "/supplemental-authority", coordinator.AudienceClientId, workload, input, 32 * 1024, token);
                    if (FactoryModelsValidation.Hash(current.Request) != FactoryModelsValidation.Hash(input)
                        || FactoryModelsValidation.Hash(current.Workload) != FactoryModelsValidation.Hash(configuration)
                        || current.Grants.Any(g => g is null) || current.Effects.Any(e => e is null)
                        || configuration.Source.Kind == "webiq" && current.Vault is null
                        || !FactorySourceValidation.Hash(current.GovernanceRevision) || !FactorySourceValidation.Hash(current.CapabilityRevision))
                        throw new JsonException("Current source-specific authority required.");
                    return current;
                }
            }
            catch (PreviewCapabilityException e) { return Error(e.State, e.Message); }
            catch (OperationCanceledException)
            { return Error(context.RequestAborted.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "SupplementalConsumptionCanceledOrUnconfirmed"); }
            catch (SupplementalSourceException e) when (e.Reason == SupplementalReason.CapacityExceeded)
            { return Error(FactoryCharterState.Partial, "PreviewSupplementalCapacityExceeded"); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException or HttpRequestException
                or IOException or Azure.RequestFailedException or AuthenticationFailedException or ModelCapabilityException or BadHttpRequestException)
            { return Error(FactoryCharterState.Unknown, "PreviewSupplementalEvidenceUnconfirmed"); }
        }
        static IResult Error(FactoryCharterState state, string reason) =>
            Results.Json(new PreviewContextFailure(state, reason), FactoryGitHubJson.Options, statusCode: state == FactoryCharterState.Blocked ? 403 : 409);
    }
}
