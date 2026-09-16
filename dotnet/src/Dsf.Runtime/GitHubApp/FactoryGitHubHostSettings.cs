using Dsf.Core.Runtime;

namespace Dsf.Runtime.GitHubApp;

public sealed record FactoryGitHubHostSettings(
    string Product, string ProductEndpoint, string ProfilePin, string ManagedIdentityClientId)
{
    public static FactoryGitHubHostSettings Read(IReadOnlyDictionary<string, string?> env)
    {
        string[] required = ["DSF_PRODUCT", "AZURE_APPCONFIG_ENDPOINT", "DSF_FACTORY_PROFILE_PIN", "AZURE_CLIENT_ID"];
        var missing = required.Where(key => string.IsNullOrWhiteSpace(env.GetValueOrDefault(key))).ToArray();
        if (missing.Length != 0)
            throw new RuntimeVerbException("GitHub authentication blocked: required " + string.Join(", ", missing) + ".");
        return new(env["DSF_PRODUCT"]!, env["AZURE_APPCONFIG_ENDPOINT"]!,
            env["DSF_FACTORY_PROFILE_PIN"]!, env["AZURE_CLIENT_ID"]!);
    }
}
