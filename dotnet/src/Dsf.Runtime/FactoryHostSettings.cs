using Dsf.Core.Runtime;

namespace Dsf.Runtime;

public record FactoryHostSettings(
    string Product, string ProductEndpoint, string ProfilePin, string ManagedIdentityClientId)
{
    public static FactoryHostSettings Read(IReadOnlyDictionary<string, string?> env, string capability)
    {
        string[] required = ["DSF_PRODUCT", "AZURE_APPCONFIG_ENDPOINT", "DSF_FACTORY_PROFILE_PIN", "AZURE_CLIENT_ID"];
        var missing = required.Where(key => string.IsNullOrWhiteSpace(env.GetValueOrDefault(key))).ToArray();
        if (missing.Length != 0)
            throw new RuntimeVerbException(capability + " blocked: required " + string.Join(", ", missing) + ".");
        return new(env["DSF_PRODUCT"]!, env["AZURE_APPCONFIG_ENDPOINT"]!, env["DSF_FACTORY_PROFILE_PIN"]!, env["AZURE_CLIENT_ID"]!);
    }
}
