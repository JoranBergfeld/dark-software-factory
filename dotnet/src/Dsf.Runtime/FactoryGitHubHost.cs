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
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var configuration = new AzureConfigurationSettingsGateway(credential, dependencies.GitHubAuthorityHttp);
                await foreach (var (key, value) in configuration.ListAsync(
                    settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, context.RequestAborted))
                    if (!values.TryAdd(key, value)) throw new JsonException();
                var manifest = FactoryGovernance.ReadInactiveManifest(
                    values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                    values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile),
                    settings.Product, settings.ProductEndpoint, settings.ProfilePin);
                var authority = JsonSerializer.Deserialize<FactoryGitHubAuthority>(
                    values.GetValueOrDefault(ProductConfigurationKeys.FactoryGitHubAuthority) ?? throw new JsonException(),
                    FactoryGitHubJson.Options) ?? throw new JsonException();
                if (authority.SchemaVersion != 1 || authority.FactoryId != manifest.Identity.FactoryId
                    || authority.OperationId == Guid.Empty || authority.OperationId != manifest.GitHubAuthorityOperationId
                    || authority.PlanFingerprint != manifest.Identity.PlanFingerprint
                    || authority.RepositoryId != manifest.Identity.RepositoryId
                    || authority.AuthClientId != settings.ManagedIdentityClientId
                    || authority.Stage is not ("host-verification" or "verified") || !authority.AcceptedSigningRisk
                    || authority.AppId <= 0 || authority.InstallationId <= 0 || authority.AccountId <= 0
                    || !Guid.TryParse(authority.AuthPrincipalId, out var principal) || principal == Guid.Empty
                    || authority.AuthPrincipalId == authority.CodePrincipalId)
                    throw new JsonException();
                if (issueReadToken && !CodeCaller(context, authority, manifest.Identity.TenantId))
                    return Results.Json(new GitHubAuthorityProof(GitHubAuthorityState.Blocked, "contents-read-token",
                        "factory identity administrator", "Native authenticated caller is not the approved code-intent workload."),
                        FactoryGitHubJson.Options, statusCode: 403);
                var verifier = new FactoryGitHubVerifier(dependencies.GitHubAuthorityHttp ?? GitHubAuthorityHttp,
                    credential, dependencies.GitHubAuthorityClock);
                var (proof, access) = await verifier.AcquireReadTokenAsync(authority, context.RequestAborted);
                if (issueReadToken && access is not null)
                {
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
                    detail = "Current pinned inactive governance is unavailable or invalid; no GitHub access." }, statusCode: 403);
            }
        }
    }

    private static bool CodeCaller(HttpContext context, FactoryGitHubAuthority authority, string tenant)
    {
        // Only the dedicated ACA ingress with its pinned native Entra authConfig may expose this host.
        // ACA removes caller-supplied principal headers and injects these after validating the bearer token.
        if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad"
            || context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] != authority.CodePrincipalId
            || !context.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) || encoded.Count != 1
            || encoded[0] is not { Length: > 0 and <= 16384 } value)
            return false;
        try
        {
            var principal = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(value), FactoryGitHubJson.Options);
            if (principal.GetProperty("auth_typ").GetString() != "aad") return false;
            var claims = principal.GetProperty("claims").EnumerateArray().ToArray();
            return Claim("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier") == authority.CodePrincipalId
                && Claim("tid", "http://schemas.microsoft.com/identity/claims/tenantid") == tenant
                && Claim("appid", "azp") == authority.CodeClientId;

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
