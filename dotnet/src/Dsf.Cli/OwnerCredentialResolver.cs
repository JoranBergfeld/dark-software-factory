namespace Dsf.Cli;

internal sealed record OwnerGitHubIdentity(
    string AppId,
    string InstallationId,
    string? InstallationSelection = null);

internal interface IOwnerCredentialReader
{
    Task<OwnerGitHubIdentity?> ReadIdentityFromStatusAsync(
        string ownerAppConfigEndpoint,
        CancellationToken cancellationToken) =>
        Task.FromResult<OwnerGitHubIdentity?>(null);

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
        => await ResolveIdentityAsync(keyVaultUri, null, cancellationToken);

    public async Task<OwnerGitHubIdentity> ResolveIdentityAsync(
        string keyVaultUri,
        string? ownerAppConfigEndpoint,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ownerAppConfigEndpoint))
        {
            var statusIdentity = await reader.ReadIdentityFromStatusAsync(
                ownerAppConfigEndpoint,
                cancellationToken);
            if (statusIdentity is not null)
            {
                return statusIdentity;
            }
        }

        var credentials = await reader.ReadAsync(keyVaultUri, includePrivateKey: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(credentials.AppId)
            || string.IsNullOrWhiteSpace(credentials.InstallationId))
        {
            throw new InvalidOperationException(
                $"Owner Key Vault '{keyVaultUri}' has incomplete GitHub App identifiers.");
        }

        return new OwnerGitHubIdentity(
            credentials.AppId,
            credentials.InstallationId,
            credentials.InstallationSelection);
    }
}
