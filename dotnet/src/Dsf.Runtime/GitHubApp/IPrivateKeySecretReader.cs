namespace Dsf.Runtime.GitHubApp;

/// <summary>
/// Resolves the GitHub App's PEM-encoded private key from wherever it is stored.
/// The production implementation reads it from Azure Key Vault; tests substitute
/// a deterministic double that returns a locally generated key, never a live
/// secret.
/// </summary>
public interface IPrivateKeySecretReader
{
    Task<string> GetSecretAsync(Uri vaultUri, string secretName, CancellationToken cancellationToken);
}
