using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class FactoryWorkloadCredential(TokenCredential inner, string tenant, string? principalId, string? clientId, string? resourceId,
    TimeProvider clock, bool requireGuidV2Audience = false) : TokenCredential
{
    internal FactoryWorkloadCredential(TokenCredential inner, string tenant, FoundationOperation binding, TimeProvider clock)
        : this(inner, tenant, binding.PrincipalId, binding.ClientId, binding.ResourceId, clock) { }
    public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => GetTokenAsync(context, token).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
    {
        var access = await inner.GetTokenAsync(context, token);
        try
        {
            var part = access.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var claims = document.RootElement;
            var now = clock.GetUtcNow();
            var version = claims.GetProperty("ver").GetString();
            var clientClaim = version switch { "1.0" => "appid", "2.0" => "azp", _ => throw new JsonException() };
            var clients = claims.EnumerateObject().Where(p => p.Name is "appid" or "azp").ToArray();
            if (claims.GetProperty("oid").GetString() != principalId || claims.GetProperty("tid").GetString() != tenant
                || clients.Length != 1 || clients[0].Name != clientClaim || clients[0].Value.GetString() != clientId
                || !string.Equals(claims.GetProperty("xms_mirid").GetString(), resourceId, StringComparison.OrdinalIgnoreCase)
                || access.ExpiresOn <= now || claims.GetProperty("exp").GetInt64() <= now.ToUnixTimeSeconds()
                || claims.GetProperty("nbf").GetInt64() > now.ToUnixTimeSeconds()
                || requireGuidV2Audience && version == "2.0" && !Guid.TryParseExact(claims.GetProperty("aud").GetString(), "D", out _)
                || !AudienceMatches(claims.GetProperty("aud").GetString(), context.Scopes.Single(), version))
                throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or FormatException or IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException)
        { throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, "workload-token", "Current unexpired native workload MI token must match principal/client/tenant/resource and audience; no developer credential proof."); }
        return access;
    }

    private static bool AudienceMatches(string? audience, string scope, string version)
    {
        if (!scope.EndsWith("/.default", StringComparison.Ordinal)) return false;
        var resource = scope[..^"/.default".Length];
        if (!resource.StartsWith("api://", StringComparison.Ordinal))
            return audience?.TrimEnd('/') == resource.TrimEnd('/');
        // The requested scope stays URI-shaped; native v2 custom-API audiences are client IDs.
        return Guid.TryParseExact(resource["api://".Length..], "D", out var expected) && expected != Guid.Empty
            && (version == "1.0" && audience == resource || Guid.TryParseExact(audience, "D", out var actual) && actual == expected);
    }
}
