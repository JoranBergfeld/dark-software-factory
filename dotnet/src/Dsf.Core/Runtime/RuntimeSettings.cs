namespace Dsf.Core.Runtime;

/// <summary>
/// Per-product runtime configuration resolved from the environment. Reuses the
/// established DSF env var names so every .NET runtime process receives the same
/// product-scoped Azure/GitHub endpoints and identities.
/// </summary>
public sealed record RuntimeSettings(
    string Product,
    string AppConfigEndpoint,
    string KeyVaultUri,
    string AppInsightsConnectionString,
    string CosmosEndpoint,
    string OpenAiEndpoint,
    string OpenAiDeployment,
    string OpenAiEmbeddingDeployment,
    string GitHubAppId,
    string GitHubInstallationId,
    string GitHubAppPrivateKeySecret,
    string GitHubRepository);
