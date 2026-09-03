namespace Dsf.Runtime.GitHubApp;

/// <summary>
/// Builds a <see cref="GitHubAppAuthProvider"/> from the runtime's existing GitHub
/// App settings. Extracted so every collaborator that authenticates to GitHub as
/// the App -- the issue filer and the outcome poller alike -- constructs its auth
/// provider identically, against the same resolved API URL, instead of each
/// composer re-deriving it.
/// </summary>
internal static class GitHubAppAuthProviderFactory
{
    private const string DefaultGitHubApiUrl = "https://api.github.com/";

    public static IGitHubAuthProvider Build(
        string appId,
        string installationId,
        string keyVaultUri,
        string privateKeySecret,
        string apiUrl,
        IPrivateKeySecretReader? privateKeySecretReader)
    {
        var authHttpClient = new HttpClient
        {
            BaseAddress = new Uri(EnsureTrailingSlash(string.IsNullOrWhiteSpace(apiUrl) ? DefaultGitHubApiUrl : apiUrl)),
        };

        return new GitHubAppAuthProvider(
            appId,
            installationId,
            new Uri(keyVaultUri),
            privateKeySecret,
            privateKeySecretReader ?? new AzureKeyVaultPrivateKeySecretReader(),
            authHttpClient);
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
