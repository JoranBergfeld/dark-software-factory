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
}

internal sealed class RecordingOwnerCredentialReader(OwnerGitHubCredentials credentials)
    : IOwnerCredentialReader
{
    public bool PrivateKeyRead { get; private set; }

    public Task<OwnerGitHubCredentials> ReadAsync(
        string keyVaultUri,
        bool includePrivateKey,
        CancellationToken cancellationToken)
    {
        PrivateKeyRead = includePrivateKey;
        return Task.FromResult(includePrivateKey ? credentials : credentials with { PrivateKey = string.Empty });
    }
}
