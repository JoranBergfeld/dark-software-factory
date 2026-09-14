using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class OwnerCredentialResolverTests
{
    [Fact]
    public async Task Resolve_identity_reads_app_and_installation_ids_without_reading_private_key()
    {
        var store = new RecordingOwnerCredentialReader(
            new OwnerGitHubCredentials("7", "42", "private-key"));
        var resolver = new OwnerCredentialResolver(store);

        var identity = await resolver.ResolveIdentityAsync(
            "https://kvdsfsbx20260907.vault.azure.net/",
            CancellationToken.None);

        Assert.Equal("7", identity.AppId);
        Assert.Equal("42", identity.InstallationId);
        Assert.False(store.PrivateKeyRead);
    }

    [Fact]
    public async Task Resolve_identity_prefers_owner_bootstrap_status_when_available()
    {
        var store = new RecordingOwnerCredentialReader(
            new OwnerGitHubCredentials("", "", "private-key"))
        {
            StatusIdentity = new OwnerGitHubIdentity("7", "42"),
        };
        var resolver = new OwnerCredentialResolver(store);

        var identity = await resolver.ResolveIdentityAsync(
            "https://kvdsfsbx20260907.vault.azure.net/",
            "https://appcsdsfsbx20260907.azconfig.io",
            CancellationToken.None);

        Assert.Equal("7", identity.AppId);
        Assert.Equal("42", identity.InstallationId);
        Assert.False(store.PrivateKeyRead);
        Assert.False(store.KeyVaultRead);
    }
}

internal sealed class RecordingOwnerCredentialReader(OwnerGitHubCredentials credentials)
    : IOwnerCredentialReader
{
    public bool PrivateKeyRead { get; private set; }
    public bool KeyVaultRead { get; private set; }
    public OwnerGitHubIdentity? StatusIdentity { get; init; }

    public Task<OwnerGitHubIdentity?> ReadIdentityFromStatusAsync(
        string ownerAppConfigEndpoint,
        CancellationToken cancellationToken) =>
        Task.FromResult(StatusIdentity);

    public Task<OwnerGitHubCredentials> ReadAsync(
        string keyVaultUri,
        bool includePrivateKey,
        CancellationToken cancellationToken)
    {
        KeyVaultRead = true;
        PrivateKeyRead = includePrivateKey;
        return Task.FromResult(includePrivateKey ? credentials : credentials with { PrivateKey = string.Empty });
    }
}
