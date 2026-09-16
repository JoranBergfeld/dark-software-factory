using System.Text.Json;

namespace Dsf.Core.Onboarding;

public static class FactoryHostedWorkloadPolicy
{
    public static ModelResourceObservation ObserveModelHost(JsonElement resource, string target)
    {
        var system = resource.GetProperty("systemData");
        var created = system.GetProperty("createdAt").GetDateTimeOffset();
        if (!string.Equals(resource.GetProperty("id").GetString(), target, StringComparison.OrdinalIgnoreCase)
            || system.GetProperty("lastModifiedAt").GetDateTimeOffset() != created) throw new JsonException();
        var properties = resource.GetProperty("properties");
        var policy = target.EndsWith("/authconfigs/current", StringComparison.Ordinal);
        return new(target, FactoryModelsValidation.Hash(new
        {
            target, created,
            policy = policy ? properties : default(JsonElement?),
            environment = !policy ? properties.GetProperty("managedEnvironmentId") : default(JsonElement?),
            configuration = !policy ? properties.GetProperty("configuration") : default(JsonElement?),
            template = !policy ? properties.GetProperty("template") : default(JsonElement?),
            identity = resource.TryGetProperty("identity", out var identity) ? identity : default(JsonElement?),
        }), created.ToString("O"));
    }

    public static string Validate(JsonElement host, JsonElement auth, FoundationOperation identity, FoundationOperation hosting,
        string image, string verb, string product, string endpoint, string pin, string tenant, string audience, IReadOnlyList<string> callers)
    {
        var nativeIdentity = host.GetProperty("identity");
        var properties = host.GetProperty("properties");
        var configuration = properties.GetProperty("configuration");
        var ingress = configuration.GetProperty("ingress");
        var template = properties.GetProperty("template");
        var containers = template.GetProperty("containers").EnumerateArray().ToArray();
        var env = new Dictionary<string, string>
        {
            ["DSF_PRODUCT"] = product, ["AZURE_APPCONFIG_ENDPOINT"] = endpoint,
            ["DSF_FACTORY_PROFILE_PIN"] = pin, ["AZURE_CLIENT_ID"] = identity.ClientId!,
        };
        if (nativeIdentity.GetProperty("type").GetString() != "UserAssigned"
            || !nativeIdentity.GetProperty("userAssignedIdentities").EnumerateObject().Select(p => p.Name).SequenceEqual([identity.Target], StringComparer.OrdinalIgnoreCase)
            || !string.Equals(properties.GetProperty("managedEnvironmentId").GetString(), hosting.Target, StringComparison.OrdinalIgnoreCase)
            || configuration.GetProperty("activeRevisionsMode").GetString() != "Single"
            || configuration.TryGetProperty("secrets", out var secrets) && secrets.GetArrayLength() != 0
            || ingress.GetProperty("external").ValueKind != JsonValueKind.True || ingress.GetProperty("allowInsecure").ValueKind != JsonValueKind.False
            || ingress.GetProperty("targetPort").GetInt32() != 8080 || containers.Length != 1
            || Nonempty(template, "initContainers") || Nonempty(template, "volumes") || Nonempty(template, "serviceBinds")
            || Nonempty(containers[0], "volumeMounts")
            || configuration.TryGetProperty("service", out var service) && service.ValueKind != JsonValueKind.Null
            || configuration.TryGetProperty("dapr", out var dapr) && dapr.ValueKind != JsonValueKind.Null
                && dapr.GetProperty("enabled").ValueKind == JsonValueKind.True
            || containers[0].GetProperty("image").GetString() != image
            || !FactoryModelsValidation.Strings(containers[0], "command").SequenceEqual(["dotnet", "dsf-runtime.dll"])
            || !FactoryModelsValidation.Strings(containers[0], "args").SequenceEqual([verb]))
            throw new JsonException("Exact isolated workload host binding required.");
        var environment = containers[0].GetProperty("env").EnumerateArray().ToArray();
        if (environment.Length != env.Count || environment.Select(e => e.GetProperty("name").GetString()).Distinct().Count() != env.Count
            || environment.Any(e => !env.TryGetValue(e.GetProperty("name").GetString()!, out var value)
                || e.TryGetProperty("secretRef", out var secret) && secret.ValueKind != JsonValueKind.Null || e.GetProperty("value").GetString() != value))
            throw new JsonException();
        var policy = auth.GetProperty("properties");
        var global = policy.GetProperty("globalValidation");
        var aad = policy.GetProperty("identityProviders").GetProperty("azureActiveDirectory");
        var registration = aad.GetProperty("registration");
        var validation = aad.GetProperty("validation");
        var principals = validation.GetProperty("defaultAuthorizationPolicy").GetProperty("allowedPrincipals");
        if (policy.GetProperty("platform").GetProperty("enabled").ValueKind != JsonValueKind.True
            || global.GetProperty("unauthenticatedClientAction").GetString() != "Return401"
            || global.TryGetProperty("excludedPaths", out var excluded) && excluded.GetArrayLength() != 0
            || policy.GetProperty("httpSettings").GetProperty("requireHttps").ValueKind != JsonValueKind.True
            || policy.GetProperty("login").GetProperty("tokenStore").GetProperty("enabled").ValueKind != JsonValueKind.False
            || aad.GetProperty("enabled").ValueKind != JsonValueKind.True || registration.GetProperty("clientId").GetString() != audience
            || registration.GetProperty("openIdIssuer").GetString() != "https://login.microsoftonline.com/" + tenant + "/v2.0"
            || !FactoryModelsValidation.Strings(principals, "identities").Order().SequenceEqual(callers.Order())
            || principals.TryGetProperty("groups", out var groups) && groups.GetArrayLength() != 0
            || !FactoryModelsValidation.Strings(validation, "allowedAudiences").Order().SequenceEqual(new[] { audience, "api://" + audience }.Order()))
            throw new JsonException("Exact native authenticated caller policy required.");
        var fqdn = ingress.GetProperty("fqdn").GetString()!;
        if (fqdn.Length > 253 || !System.Text.RegularExpressions.Regex.IsMatch(fqdn, "^[a-z0-9.-]+\\.azurecontainerapps\\.io$"))
            throw new JsonException();
        return FactoryGovernance.NormalizeEndpoint("https://" + fqdn);
    }

    private static bool Nonempty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var array) && array.ValueKind != JsonValueKind.Null && array.GetArrayLength() != 0;
}
