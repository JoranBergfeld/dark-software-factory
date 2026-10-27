namespace Dsf.Runtime.GitHubApp;

public sealed record FactoryGitHubHostSettings(
    string Product, string ProductEndpoint, string ProfilePin, string ManagedIdentityClientId)
    : FactoryHostSettings(Product, ProductEndpoint, ProfilePin, ManagedIdentityClientId)
{
    public static FactoryGitHubHostSettings Read(IReadOnlyDictionary<string, string?> env)
    {
        var settings = FactoryHostSettings.Read(env, "GitHub authentication");
        return new(settings.Product, settings.ProductEndpoint, settings.ProfilePin, settings.ManagedIdentityClientId);
    }
}
