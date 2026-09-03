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
    public const string OwnerAppConfigEndpoint = "DSF_OWNER_APPCONFIG_ENDPOINT";

    /// <summary>Every env var name <see cref="ComposeAsync"/> can fill in from the owner runtime index.</summary>
    private static readonly string[] ComposedEnvVars =
    [
        AzureAppConfigEndpoint,
        AzureKeyVaultUri,
        ApplicationInsightsConnectionString,
        AzureCosmosEndpoint,
        AzureOpenAiEndpoint,
        AzureOpenAiDeployment,
        AzureOpenAiEmbeddingDeployment,
        GitHubAppId,
        GitHubInstallationId,
        GitHubAppPrivateKeySecret,
        GitHubRepository,
    ];

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

    /// <summary>
    /// Resolves <see cref="RuntimeSettings"/> the same way as <see cref="FromEnvironment(IReadOnlyDictionary{string,string},string)"/>,
    /// but first consults the owner App Configuration runtime index (published by
    /// <c>dsf new</c>'s GitHub provisioning step) for any of the composed settings
    /// that are not already set locally. This is what lets a runtime command run
    /// with only <c>--product</c>/<c>DSF_PRODUCT</c> resolve the rest of its
    /// configuration through the same authority the CLI provisioned it into,
    /// instead of requiring every value to be restated as a local env var.
    /// <c>DSF_OWNER_APPCONFIG_ENDPOINT</c> is read from <paramref name="env"/>; when
    /// unset, this behaves identically to <see cref="FromEnvironment(IReadOnlyDictionary{string,string},string)"/>
    /// and <paramref name="ownerRuntimeIndexReader"/> is never consulted. When set,
    /// a lookup failure (missing product, unreachable endpoint) fails loudly rather
    /// than silently falling back to whatever local settings happen to be present.
    /// </summary>
    public static async Task<RuntimeSettings> ComposeAsync(
        IReadOnlyDictionary<string, string?> env,
        string? productOverride,
        IOwnerRuntimeIndexReader ownerRuntimeIndexReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownerRuntimeIndexReader);

        string Read(string name) => (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;

        var product = (productOverride ?? Read(DsfProduct)).Trim();
        if (product.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"{DsfProduct} is required to scope the factory runtime (set {DsfProduct}=<product>).",
                [DsfProduct]);
        }

        var ownerEndpoint = Read(OwnerAppConfigEndpoint);
        var merged = new Dictionary<string, string?>(env) { [DsfProduct] = product };

        if (ownerEndpoint.Length > 0)
        {
            IReadOnlyDictionary<string, string> index;
            try
            {
                index = await ownerRuntimeIndexReader.ReadAsync(ownerEndpoint, product, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new RuntimeConfigurationException(
                    $"failed to resolve runtime configuration for product '{product}' from the owner App "
                    + $"Configuration runtime index at '{ownerEndpoint}': {exception.Message}",
                    [OwnerAppConfigEndpoint]);
            }

            foreach (var envVar in ComposedEnvVars)
            {
                if (Read(envVar).Length == 0
                    && index.TryGetValue(envVar, out var remoteValue)
                    && !string.IsNullOrWhiteSpace(remoteValue))
                {
                    merged[envVar] = remoteValue;
                }
            }
        }

        return FromEnvironment(merged, product);
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
