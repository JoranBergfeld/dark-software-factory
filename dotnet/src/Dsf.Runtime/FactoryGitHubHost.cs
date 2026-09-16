using Dsf.Runtime.GitHubApp;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Azure.Identity;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    private static readonly HttpClient GitHubAuthorityHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    public static WebApplication BuildGitHubAuthenticationHost(
        FactoryGitHubHostSettings settings, RuntimeDependencies dependencies,
        string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();
        Func<HttpContext, Task<IResult>> verify = context => HandleAsync(context, issueReadToken: false);
        Func<HttpContext, Task<IResult>> readToken = context => HandleAsync(context, issueReadToken: true);
        app.MapPost("/verify", verify);
        app.MapPost("/tokens/contents", readToken);
        app.MapPost("/tokens/issues", () => Results.Json(
            new GitHubAuthorityProof(GitHubAuthorityState.Blocked, "issue-write-token", "factory governance operator",
                "No publication caller is eligible while inactive; activation and filing authorization are not implemented by this capability."),
            FactoryGitHubJson.Options, statusCode: 403));
        return app;

        async Task<IResult> HandleAsync(HttpContext context, bool issueReadToken)
        {
            try
            {
                if (settings.ProfilePin is not { Length: 64 } || settings.ProfilePin.Any(c => !char.IsAsciiHexDigit(c))
                    || !Guid.TryParse(settings.ManagedIdentityClientId, out var clientId) || clientId == Guid.Empty)
                    throw new JsonException();
                var credential = dependencies.GitHubAuthorityCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var configuration = new AzureConfigurationSettingsGateway(credential, dependencies.GitHubAuthorityHttp);
                var (manifest, authority, values) = await ReadPreparationAsync(configuration, context.RequestAborted);
                if (issueReadToken)
                {
                    FactoryGitHubAuthorityValidation.ReadCompletedEffects(authority, values);
                    FactoryGitHubAuthorityValidation.ValidateVerified(authority);
                }
                if (!NativeCaller(context, issueReadToken ? authority.CodePrincipalId : manifest.Identity.AzureActorId,
                    manifest.Identity.TenantId, issueReadToken ? authority.CodeClientId : null))
                    return Results.Json(new GitHubAuthorityProof(GitHubAuthorityState.Blocked,
                        issueReadToken ? "contents-read-token" : "deployed-github-authentication",
                        "factory identity administrator", "Native authenticated caller is not the approved "
                            + (issueReadToken ? "code-intent workload." : "verification operator."))
                        { FactoryId = authority.FactoryId, OperationId = authority.OperationId,
                            ObservedAt = dependencies.GitHubAuthorityClock.GetUtcNow() },
                        FactoryGitHubJson.Options, statusCode: 403);
                var verifier = new FactoryGitHubVerifier(dependencies.GitHubAuthorityHttp ?? GitHubAuthorityHttp,
                    credential, dependencies.GitHubAuthorityClock);
                var (proof, access) = await verifier.AcquireReadTokenAsync(authority, context.RequestAborted);
                if (issueReadToken && access is not null)
                {
                    var (currentManifest, current, currentValues) = await ReadPreparationAsync(configuration, context.RequestAborted);
                    FactoryGitHubAuthorityValidation.ReadCompletedEffects(current, currentValues);
                    FactoryGitHubAuthorityValidation.ValidateVerified(current);
                    if (JsonSerializer.Serialize(current, FactoryGitHubJson.Options) != JsonSerializer.Serialize(authority, FactoryGitHubJson.Options)
                        || JsonSerializer.Serialize(currentManifest, FactoryGitHubJson.Options) != JsonSerializer.Serialize(manifest, FactoryGitHubJson.Options))
                        throw new JsonException("Authority changed while acquiring access.");
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Json(new { token = access, expires_at = proof.TokenExpiresAt,
                        repository_ids = new[] { authority.RepositoryId }, permissions = proof.EffectivePermissions });
                }
                return Results.Json(proof, FactoryGitHubJson.Options,
                    statusCode: proof.State == GitHubAuthorityState.Verified ? 200 : 403);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or Azure.RequestFailedException or HttpRequestException
                or Azure.Identity.AuthenticationFailedException or ArgumentException)
            {
                return Results.Json(new { state = "Blocked", operation = "product-governance", responsibleAdmin = "factory operator",
                    detail = "Current pinned inactive governance is unavailable or invalid; no token released." }, statusCode: 403);
            }
        }

        async Task<(FactoryManifest Manifest, FactoryGitHubAuthority Authority, Dictionary<string, string> Values)> ReadPreparationAsync(
            AzureConfigurationSettingsGateway configuration, CancellationToken token)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var (key, value) in configuration.ListAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
                if (!values.TryAdd(key, value)) throw new JsonException();
            var manifest = FactoryGovernance.ReadInactiveManifest(
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile),
                settings.Product, settings.ProductEndpoint, settings.ProfilePin);
            var authority = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
            if (authority.AuthClientId != settings.ManagedIdentityClientId) throw new JsonException();
            FactoryGitHubAuthorityValidation.ValidateObservation(authority);
            return (manifest, authority, values);
        }
    }

    private static bool NativeCaller(HttpContext context, string objectId, string tenant, string? clientId)
    {
        // Only the dedicated ACA ingress with its pinned native Entra authConfig may expose this host.
        // ACA removes caller-supplied principal headers and injects these after validating the bearer token.
        if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad"
            || context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] != objectId
            || !context.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) || encoded.Count != 1
            || encoded[0] is not { Length: > 0 and <= 16384 } value)
            return false;
        try
        {
            var principal = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(value), FactoryGitHubJson.Options);
            if (principal.GetProperty("auth_typ").GetString() != "aad") return false;
            var claims = principal.GetProperty("claims").EnumerateArray().ToArray();
            return Claim("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier") == objectId
                && Claim("tid", "http://schemas.microsoft.com/identity/claims/tenantid") == tenant
                && (clientId is null || Claim("appid", "azp") == clientId);

            string? Claim(string first, string second)
            {
                var matching = claims.Where(c => c.GetProperty("typ").GetString() is { } name && (name == first || name == second)).ToArray();
                return matching.Length == 1 ? matching[0].GetProperty("val").GetString() : null;
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }
}
