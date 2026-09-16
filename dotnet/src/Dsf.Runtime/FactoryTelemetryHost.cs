using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Azure.Identity;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    public static WebApplication BuildTelemetryHost(FactoryHostSettings settings, RuntimeDependencies dependencies,
        string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();
        app.MapPost("/proof", async (HttpContext context) =>
        {
            try
            {
                if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad"
                    || settings.ProfilePin is not { Length: 64 } || !settings.ProfilePin.All(char.IsAsciiHexDigit)
                    || !Guid.TryParse(settings.ManagedIdentityClientId, out var clientId) || clientId == Guid.Empty)
                    throw new JsonException();
                var credential = dependencies.TelemetryCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var configuration = new AzureConfigurationSettingsGateway(credential, dependencies.TelemetryHttp);
                var (manifest, authority) = await ReadAsync(configuration, context.RequestAborted);
                if (authority.Configuration.SourceIdentity.ClientId != settings.ManagedIdentityClientId
                    || !NativeCaller(context, manifest.Identity.AzureActorId, manifest.Identity.TenantId, null))
                    throw new JsonException();
                var proof = await new FactoryTelemetryQuery(credential, dependencies.TelemetryHttp, dependencies.TelemetryClock)
                    .ObserveAsync(manifest, authority, context.RequestAborted);
                FactoryTelemetryLimits.ValidateProof(authority.Configuration, proof);
                var (currentManifest, currentAuthority) = await ReadAsync(configuration, context.RequestAborted);
                if (FactoryTelemetryValidation.Hash(manifest) != FactoryTelemetryValidation.Hash(currentManifest)
                    || FactoryTelemetryValidation.Hash(authority) != FactoryTelemetryValidation.Hash(currentAuthority))
                    throw new JsonException();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(proof, FactoryGitHubJson.Options, statusCode: proof.State == TelemetryState.Ready ? 200 : 409);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or Azure.RequestFailedException or HttpRequestException or AuthenticationFailedException)
            {
                return Results.Json(new { state = "Blocked", operation = "product-governance",
                    detail = "Pinned inactive telemetry authority and native authenticated operator required; no proof released. Factory identity/configuration administrator must inspect." },
                    statusCode: 403);
            }
        });
        return app;

        async Task<(FactoryManifest Manifest, FactoryTelemetry Authority)> ReadAsync(
            AzureConfigurationSettingsGateway configuration, CancellationToken token)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var (key, value) in configuration.ListAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
                if (!values.TryAdd(key, value)) throw new JsonException();
            var manifest = FactoryGovernance.ReadInactiveManifest(values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile), settings.Product, settings.ProductEndpoint, settings.ProfilePin);
            return (manifest, FactoryTelemetryValidation.ReadPreparation(manifest, values));
        }
    }
}
