namespace Dsf.Runtime.GitHubApp;

/// <summary>
/// A fixed token supplied ahead of time, e.g. a developer's personal access token
/// read from <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c>. This is a documented local-dev
/// override, not the production auth mechanism: production factories authenticate
/// through <see cref="GitHubAppAuthProvider"/> instead, using the same GitHub App
/// settings the CLI provisions.
/// </summary>
public sealed class StaticGitHubAuthProvider(string token) : IGitHubAuthProvider
{
    private readonly string token = token.Trim();

    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
}
