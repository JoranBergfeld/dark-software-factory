using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsf.Core.Onboarding;

/// <summary>Validates the product-store registration and its pinned inactive governance, never activation.</summary>
public static class FactoryGovernance
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static FactoryManifest ReadInactiveManifest(
        string? manifestValue, string? profileValue, string product, string productEndpoint, string? expectedPin = null)
    {
        if (string.IsNullOrWhiteSpace(manifestValue) || string.IsNullOrWhiteSpace(profileValue)
            || Encoding.UTF8.GetByteCount(manifestValue) >= 10 * 1024 || Encoding.UTF8.GetByteCount(profileValue) >= 10 * 1024)
            throw new JsonException();
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(manifestValue, Json) ?? throw new JsonException();
        var identity = manifest.Identity;
        if (manifest.SchemaVersion != 1 || manifest.Kind != "decide-factory-manifest"
            || manifest.Authority != "product-app-configuration" || manifest.Publication != "Inactive"
            || identity.FactoryId == Guid.Empty || identity.OperationId == Guid.Empty
            || identity.ProductKey != product || identity.RepositoryId <= 0 || identity.GitHubActorId <= 0
            || !Guid.TryParse(identity.AzureActorId, out var actor) || actor == Guid.Empty
            || !Guid.TryParse(identity.TenantId, out var tenant) || tenant == Guid.Empty
            || !Guid.TryParse(identity.SubscriptionId, out var subscription) || subscription == Guid.Empty
            || identity.CreatedAt == default || string.IsNullOrWhiteSpace(identity.ApplicationEnvironment)
            || NormalizeEndpoint(identity.OwnerEndpoint) != identity.OwnerEndpoint
            || NormalizeEndpoint(productEndpoint) != "https://dsfo-" + identity.FactoryId.ToString("N") + ".azconfig.io"
            || !IsHash(identity.PlanFingerprint) || !IsHash(identity.SelectionFingerprint)
            || !IsHash(manifest.ReviewedPlanSha256) || !IsHash(manifest.ProfilePin)
            || expectedPin is not null && manifest.ProfilePin != expectedPin
            || manifest.PlanBytes is <= 0 or > 1024 * 1024
            || manifest.PlanChunks != (manifest.PlanBytes + 3999) / 4000)
            throw new JsonException();
        var profile = JsonSerializer.Deserialize<FactoryOnboardingProfile>(profileValue, Json) ?? throw new JsonException();
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profileValue))) != manifest.ProfilePin
            || profile.SchemaVersion != 1 || profile.Profile != "onboard-decide-inactive-v1"
            || profile.FactoryId != identity.FactoryId || profile.PlanFingerprint != identity.PlanFingerprint
            || profile.AllowedRepositoryMutations.Count != 0)
            throw new JsonException();
        return manifest;
    }

    public static string NormalizeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/")
            throw new JsonException();
        return "https://" + uri.IdnHost.TrimEnd('.').ToLowerInvariant();
    }

    private static bool IsHash(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
