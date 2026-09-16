using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class FactoryWorkloadCredential(TokenCredential inner, string tenant, FoundationOperation binding, TimeProvider clock) : TokenCredential
{
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
            var clients = claims.EnumerateObject().Where(p => p.Name is "appid" or "azp").ToArray();
            if (claims.GetProperty("oid").GetString() != binding.PrincipalId || claims.GetProperty("tid").GetString() != tenant
                || clients.Length != 1 || clients[0].Value.GetString() != binding.ClientId
                || !string.Equals(claims.GetProperty("xms_mirid").GetString(), binding.ResourceId, StringComparison.OrdinalIgnoreCase)
                || access.ExpiresOn <= now || claims.GetProperty("exp").GetInt64() <= now.ToUnixTimeSeconds()
                || claims.GetProperty("nbf").GetInt64() > now.ToUnixTimeSeconds()
                || claims.GetProperty("aud").GetString()?.TrimEnd('/') != context.Scopes.Single()[..^"/.default".Length].TrimEnd('/'))
                throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or FormatException or IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException)
        { throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, "workload-token", "Current unexpired native workload MI token must match principal/client/tenant/resource and audience; no developer credential proof."); }
        return access;
    }
}
