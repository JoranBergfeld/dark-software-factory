namespace Dsf.Core.Runtime;

/// <summary>
/// Per-product runtime configuration resolved from the environment. Mirrors
/// <c>AzureRuntimeSettings</c> in the Python <c>core/src/dsf/container.py</c>: the
/// same env var names are reused so a factory instance can be configured
/// identically regardless of which runtime (Python or .NET) is deployed.
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
