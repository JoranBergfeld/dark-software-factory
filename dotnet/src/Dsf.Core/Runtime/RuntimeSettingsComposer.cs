namespace Dsf.Core.Runtime;

/// <summary>
/// Composes <see cref="RuntimeSettings"/> from the environment, reusing the exact
/// env var names the Python runtime uses (see <c>container.py</c>
/// <c>AzureRuntimeSettings.from_env</c> / <c>build_services</c>). <see cref="DsfProduct"/>
/// is checked first and fails alone (the runtime is meaningless without a product
/// scope); once it is present, every required data-plane endpoint is checked
/// together so a single failure names every unset requirement, not just the first.
/// </summary>
public static class RuntimeSettingsComposer
{
    public const string DsfProduct = "DSF_PRODUCT";
    public const string AzureAppConfigEndpoint = "AZURE_APPCONFIG_ENDPOINT";
    public const string AzureKeyVaultUri = "AZURE_KEYVAULT_URI";
    public const string ApplicationInsightsConnectionString = "APPLICATIONINSIGHTS_CONNECTION_STRING";
    public const string AzureCosmosEndpoint = "AZURE_COSMOS_ENDPOINT";
    public const string AzureOpenAiEndpoint = "AZURE_OPENAI_ENDPOINT";
    public const string AzureOpenAiDeployment = "AZURE_OPENAI_DEPLOYMENT";
    public const string AzureOpenAiEmbeddingDeployment = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT";
    public const string GitHubAppId = "GITHUB_APP_ID";
    public const string GitHubInstallationId = "GITHUB_INSTALLATION_ID";
    public const string GitHubAppPrivateKeySecret = "GITHUB_APP_PRIVATE_KEY_SECRET";
    public const string GitHubRepository = "GITHUB_REPOSITORY";

    /// <summary>Required data-plane endpoints, paired with the env var that supplies each one.</summary>
    private static readonly (string EnvVar, Func<Dictionary<string, string>, string> Read)[] RequiredEndpoints =
    [
        (AzureAppConfigEndpoint, e => e[AzureAppConfigEndpoint]),
        (AzureCosmosEndpoint, e => e[AzureCosmosEndpoint]),
        (AzureOpenAiEndpoint, e => e[AzureOpenAiEndpoint]),
        (AzureOpenAiDeployment, e => e[AzureOpenAiDeployment]),
        (AzureOpenAiEmbeddingDeployment, e => e[AzureOpenAiEmbeddingDeployment]),
    ];

    /// <summary>
    /// Resolves <see cref="RuntimeSettings"/> from <paramref name="env"/>.
    /// <paramref name="productOverride"/> (e.g. a CLI <c>--product</c> flag) takes
    /// precedence over the <c>DSF_PRODUCT</c> env var when set. Throws
    /// <see cref="RuntimeConfigurationException"/> naming every unset requirement.
    /// </summary>
    public static RuntimeSettings FromEnvironment(
        IReadOnlyDictionary<string, string?> env,
        string? productOverride = null)
    {
        string Read(string name) => (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;

        var product = (productOverride ?? Read(DsfProduct)).Trim();
        if (product.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"{DsfProduct} is required to scope the factory runtime (set {DsfProduct}=<product>).",
                [DsfProduct]);
        }

        var values = new Dictionary<string, string>
        {
            [AzureAppConfigEndpoint] = Read(AzureAppConfigEndpoint),
            [AzureCosmosEndpoint] = Read(AzureCosmosEndpoint),
            [AzureOpenAiEndpoint] = Read(AzureOpenAiEndpoint),
            [AzureOpenAiDeployment] = Read(AzureOpenAiDeployment),
            [AzureOpenAiEmbeddingDeployment] = Read(AzureOpenAiEmbeddingDeployment),
        };

        var missing = RequiredEndpoints
            .Where(required => values[required.EnvVar].Length == 0)
            .Select(required => required.EnvVar)
            .ToList();
        if (missing.Count > 0)
        {
            throw new RuntimeConfigurationException(
                "missing required Azure runtime configuration: " + string.Join(", ", missing),
                missing);
        }

        return new RuntimeSettings(
            Product: product,
            AppConfigEndpoint: values[AzureAppConfigEndpoint],
            KeyVaultUri: Read(AzureKeyVaultUri),
            AppInsightsConnectionString: Read(ApplicationInsightsConnectionString),
            CosmosEndpoint: values[AzureCosmosEndpoint],
            OpenAiEndpoint: values[AzureOpenAiEndpoint],
            OpenAiDeployment: values[AzureOpenAiDeployment],
            OpenAiEmbeddingDeployment: values[AzureOpenAiEmbeddingDeployment],
            GitHubAppId: Read(GitHubAppId),
            GitHubInstallationId: Read(GitHubInstallationId),
            GitHubAppPrivateKeySecret: Read(GitHubAppPrivateKeySecret),
            GitHubRepository: Read(GitHubRepository));
    }

    /// <summary>Resolves <see cref="RuntimeSettings"/> from the real process environment.</summary>
    public static RuntimeSettings FromEnvironment(string? productOverride = null) =>
        FromEnvironment(CurrentEnvironment(), productOverride);

    private static IReadOnlyDictionary<string, string?> CurrentEnvironment()
    {
        var result = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            result[(string)entry.Key] = entry.Value as string;
        }

        return result;
    }
}
