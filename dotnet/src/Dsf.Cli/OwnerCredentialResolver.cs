namespace Dsf.Cli;

internal sealed record OwnerGitHubIdentity(string AppId, string InstallationId);

internal interface IOwnerCredentialReader
{
    Task<OwnerGitHubCredentials> ReadAsync(
        string keyVaultUri,
        bool includePrivateKey,
        CancellationToken cancellationToken);
}

internal sealed class OwnerCredentialResolver(IOwnerCredentialReader reader)
{
    public async Task<OwnerGitHubIdentity> ResolveIdentityAsync(
        string keyVaultUri,
        CancellationToken cancellationToken)
    {
        var credentials = await reader.ReadAsync(keyVaultUri, includePrivateKey: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(credentials.AppId)
            || string.IsNullOrWhiteSpace(credentials.InstallationId))
        {
            throw new InvalidOperationException(
                $"Owner Key Vault '{keyVaultUri}' has incomplete GitHub App identifiers.");
        }

        return new OwnerGitHubIdentity(credentials.AppId, credentials.InstallationId);
    }
}
