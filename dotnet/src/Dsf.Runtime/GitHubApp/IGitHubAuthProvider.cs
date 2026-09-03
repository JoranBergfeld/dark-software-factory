namespace Dsf.Runtime.GitHubApp;

/// <summary>
/// Supplies the bearer token the filing station authenticates GitHub REST calls
/// with. Implementations decide how (and how often) that token is produced --
/// a fixed developer token, or a GitHub App installation access token minted on
/// demand -- so the filer itself never needs to know which mechanism is behind it.
/// </summary>
public interface IGitHubAuthProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}
