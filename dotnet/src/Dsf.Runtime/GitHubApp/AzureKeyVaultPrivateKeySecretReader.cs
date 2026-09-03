using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Dsf.Runtime.GitHubApp;

/// <summary>
/// The production <see cref="IPrivateKeySecretReader"/>: reads the GitHub App's
/// private key from Azure Key Vault, authenticating with
/// <see cref="DefaultAzureCredential"/> so the same code runs unchanged whether
/// it is a developer's local credential or the runtime's managed identity.
/// One <see cref="SecretClient"/> is cached per vault URI so repeated token
/// refreshes do not re-authenticate on every read.
/// </summary>
public sealed class AzureKeyVaultPrivateKeySecretReader(TokenCredential? credential = null) : IPrivateKeySecretReader
{
    private readonly TokenCredential credential = credential ?? new DefaultAzureCredential();
    private readonly ConcurrentDictionary<Uri, SecretClient> clients = new();

    public async Task<string> GetSecretAsync(Uri vaultUri, string secretName, CancellationToken cancellationToken)
    {
        var client = clients.GetOrAdd(vaultUri, uri => new SecretClient(uri, credential));
        var secret = await client.GetSecretAsync(secretName, cancellationToken: cancellationToken);
        return secret.Value.Value;
    }
}
