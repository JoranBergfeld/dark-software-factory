using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;

namespace Dsf.Runtime;

/// <summary>
/// Builds the conveyor's collaborators for a product. Composition is the boundary
/// where an incomplete factory is caught: a runtime with no source agents to
/// gather from, nothing to file through, or nowhere to persist what it decided is
/// reported by the settings that are unset, before any run can finish looking
/// successful while having done none of those things.
/// </summary>
public interface IConveyorComposer
{
    ConveyorServices ComposeFor(RuntimeSettings settings);
}

/// <summary>
/// The production composer: reads the runtime's environment and settings and
/// wires the real adapters -- an in-process gatherer per source kind by default
/// (reading that kind's upstream integration directly, in this process), falling
/// back to an A2A gatherer against a remote source agent only for a kind whose
/// agent endpoint is explicitly configured; the GitHub REST issue filer; the
/// Cosmos-backed run store; the Azure OpenAI-backed model client; and the
/// Application Insights-backed tracer. Anything unset raises
/// <see cref="RuntimeConfigurationException"/> naming every missing setting at
/// once.
/// </summary>
internal sealed class EnvironmentConveyorComposer(
    IReadOnlyDictionary<string, string?> env,
    HttpClient? httpClient = null,
    ICosmosDocumentGateway? cosmosGateway = null,
    IPrivateKeySecretReader? privateKeySecretReader = null,
    IModelCompletionGateway? modelGateway = null,
    ITelemetryGateway? telemetryGateway = null,
    ISourceIntegration? sourceIntegration = null,
    IConfigurationSettingsGateway? configurationSettingsGateway = null) : IConveyorComposer
{
    private const string KindPlaceholder = "{kind}";
    private const string DefaultGitHubApiUrl = "https://api.github.com/";

    private readonly HttpClient httpClient = httpClient ?? new HttpClient();
    private readonly ISourceIntegration sourceIntegration = sourceIntegration ?? new HttpSourceIntegration(env);
    private readonly IConfigurationSettingsGateway configurationSettingsGateway =
        configurationSettingsGateway ?? new AzureConfigurationSettingsGateway();

    public ConveyorServices ComposeFor(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var missing = new List<string>();
        var gatherers = ComposeGatherers(settings);
        var filer = ComposeFiler(settings, missing);
        var runStore = ComposeRunStore(settings, missing);
        var modelClient = ComposeModelClient(settings, missing);
        var tracer = ComposeTracer(settings, missing);

        if (missing.Count > 0)
        {
            throw new RuntimeConfigurationException(
                $"the runtime for product '{settings.Product}' cannot be composed: "
                + string.Join("; ", Explain(settings)),
                missing);
        }

        // The learning store is enrichment, not a requirement: a factory whose
        // Cosmos endpoint is unset still composes and runs the line exactly as
        // before, just without any prior verdict for synthesis to consult.
        var learningStore = ComposeLearningStore(settings);
        var confidenceThresholdReader = new AzureAppConfigurationConfidenceThresholdReader(
            configurationSettingsGateway, settings);

        return new ConveyorServices(
            settings.Product, gatherers, filer, runStore!, modelClient!, tracer!, confidenceThresholdReader,
            learningStore);
    }

    /// <summary>
    /// Wires the same Cosmos endpoint the run store uses, in a separate container
    /// (<see cref="RuntimeIntegrationSettings.CosmosLearningContainer"/>), so
    /// synthesis can consult any human verdict already recorded for a recurring
    /// intent. Returns <c>null</c> when no Cosmos endpoint is configured, rather
    /// than raising -- learning is enrichment the filing/persistence requirements
    /// above already gate, not a new hard requirement of its own.
    /// </summary>
    private ILearningStore? ComposeLearningStore(RuntimeSettings settings)
    {
        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            return null;
        }

        return CosmosLearningStoreFactory.Create(settings, env, cosmosGateway);
    }

    /// <summary>
    /// One gatherer per known source kind: in-process by default, gathering
    /// directly from the kind's configured upstream integration in this same
    /// process; a remote, served source agent is used instead only for a kind
    /// whose agent endpoint is explicitly configured (a per-kind
    /// <see cref="RuntimeIntegrationSettings.SourceAgentEndpoint"/>, or the
    /// <see cref="RuntimeIntegrationSettings.SourceAgentEndpointTemplate"/>). A
    /// kind with neither configured still composes here -- it fails at gather
    /// time, naming its unset upstream integration setting, exactly as a served
    /// agent's own <c>/gather</c> endpoint would.
    /// </summary>
    private IReadOnlyList<IEvidenceGatherer> ComposeGatherers(RuntimeSettings settings)
    {
        var template = Read(RuntimeIntegrationSettings.SourceAgentEndpointTemplate);
        var gatherers = new List<IEvidenceGatherer>();
        foreach (var kind in SourceAgentKinds.Known)
        {
            var endpoint = Read(RuntimeIntegrationSettings.SourceAgentEndpoint(kind));
            if (endpoint.Length == 0 && template.Length > 0)
            {
                endpoint = template.Replace(KindPlaceholder, kind, StringComparison.OrdinalIgnoreCase);
            }

            gatherers.Add(endpoint.Length > 0
                ? new SourceAgentEvidenceGatherer(kind, new Uri(EnsureTrailingSlash(endpoint)), httpClient)
                : new InProcessEvidenceGatherer(kind, settings.Product, sourceIntegration));
        }

        return gatherers;
    }

    /// <summary>
    /// Wires the GitHub issue filer. Auth reuses the runtime's existing GitHub App
    /// settings (<c>GITHUB_APP_ID</c>, <c>GITHUB_INSTALLATION_ID</c>,
    /// <c>GITHUB_APP_PRIVATE_KEY_SECRET</c>, <c>AZURE_KEYVAULT_URI</c>) -- the same
    /// names the Python runtime resolves -- and mints installation access tokens
    /// through <see cref="GitHubAppAuthProvider"/>. There is no
    /// <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c> fallback in any environment: an
    /// incomplete App configuration is reported as unset settings rather than
    /// silently accepting a personal access token in its place.
    /// </summary>
    private IIssueFiler? ComposeFiler(RuntimeSettings settings, List<string> missing)
    {
        var repository = settings.GitHubRepository.Trim();
        if (repository.Length == 0)
        {
            missing.Add(RuntimeIntegrationSettings.GitHubRepository);
        }

        var appId = settings.GitHubAppId.Trim();
        var installationId = settings.GitHubInstallationId.Trim();
        var privateKeySecret = settings.GitHubAppPrivateKeySecret.Trim();
        var keyVaultUri = settings.KeyVaultUri.Trim();
        var appSettings = new (string Value, string EnvVar)[]
        {
            (appId, RuntimeSettingsComposer.GitHubAppId),
            (installationId, RuntimeSettingsComposer.GitHubInstallationId),
            (privateKeySecret, RuntimeSettingsComposer.GitHubAppPrivateKeySecret),
            (keyVaultUri, RuntimeSettingsComposer.AzureKeyVaultUri),
        };

        if (appSettings.All(setting => setting.Value.Length > 0))
        {
            return repository.Length > 0
                ? GitHubIssueFiler.Create(
                    Read(RuntimeIntegrationSettings.GitHubApiUrl),
                    BuildGitHubAppAuthProvider(appId, installationId, keyVaultUri, privateKeySecret),
                    repository,
                    assignCloudAgent: string.Equals(
                        Read(RuntimeIntegrationSettings.AssignCloudAgentToFiledIssues),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                : null;
        }

        missing.AddRange(appSettings.Where(setting => setting.Value.Length == 0).Select(setting => setting.EnvVar));
        return null;
    }

    private IGitHubAuthProvider BuildGitHubAppAuthProvider(
        string appId, string installationId, string keyVaultUri, string privateKeySecret) =>
        GitHubAppAuthProviderFactory.Build(
            appId,
            installationId,
            keyVaultUri,
            privateKeySecret,
            Read(RuntimeIntegrationSettings.GitHubApiUrl),
            privateKeySecretReader);

    private IRunStore? ComposeRunStore(RuntimeSettings settings, List<string> missing)
    {
        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureCosmosEndpoint);
            return null;
        }

        var database = Read(RuntimeIntegrationSettings.CosmosDatabase);
        var container = Read(RuntimeIntegrationSettings.CosmosContainer);
        return new CosmosRunStore(
            settings.CosmosEndpoint.Trim(),
            database.Length > 0 ? database : RuntimeIntegrationSettings.DefaultCosmosDatabase,
            container.Length > 0 ? container : RuntimeIntegrationSettings.DefaultCosmosContainer,
            settings.Product,
            cosmosGateway ?? new AzureCosmosDocumentGateway());
    }

    /// <summary>
    /// Wires the model client synthesis and council reason with, over the
    /// runtime's existing Azure OpenAI settings (<c>AZURE_OPENAI_ENDPOINT</c>,
    /// <c>AZURE_OPENAI_DEPLOYMENT</c>).
    /// </summary>
    private IModelClient? ComposeModelClient(RuntimeSettings settings, List<string> missing)
    {
        var endpoint = settings.OpenAiEndpoint.Trim();
        var deployment = settings.OpenAiDeployment.Trim();
        if (endpoint.Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureOpenAiEndpoint);
        }

        if (deployment.Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureOpenAiDeployment);
        }

        return endpoint.Length > 0 && deployment.Length > 0
            ? new AzureOpenAiModelClient(endpoint, deployment, modelGateway ?? new AzureOpenAiCompletionGateway())
            : null;
    }

    /// <summary>
    /// Wires the tracer the conveyor line reports run and station boundaries
    /// through, over the runtime's existing
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>.
    /// </summary>
    private ITracer? ComposeTracer(RuntimeSettings settings, List<string> missing)
    {
        var connectionString = settings.AppInsightsConnectionString.Trim();
        if (connectionString.Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.ApplicationInsightsConnectionString);
            return null;
        }

        return new ApplicationInsightsTracer(connectionString, telemetryGateway ?? new ApplicationInsightsTelemetryGateway());
    }

    /// <summary>Human-readable reasons, one per unmet requirement.</summary>
    private IEnumerable<string> Explain(RuntimeSettings settings)
    {
        var missingAppSettings = new (string Value, string EnvVar)[]
        {
            (settings.GitHubAppId, RuntimeSettingsComposer.GitHubAppId),
            (settings.GitHubInstallationId, RuntimeSettingsComposer.GitHubInstallationId),
            (settings.GitHubAppPrivateKeySecret, RuntimeSettingsComposer.GitHubAppPrivateKeySecret),
            (settings.KeyVaultUri, RuntimeSettingsComposer.AzureKeyVaultUri),
        }
            .Where(setting => setting.Value.Trim().Length == 0)
            .Select(setting => setting.EnvVar)
            .ToList();

        if (missingAppSettings.Count > 0)
        {
            yield return "no GitHub App auth is configured (set " + string.Join(", ", missingAppSettings) + ")";
        }

        if (settings.GitHubRepository.Trim().Length == 0)
        {
            yield return "no repository is configured to file into (set "
                + $"{RuntimeIntegrationSettings.GitHubRepository})";
        }

        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            yield return "no blackboard persistence is configured (set "
                + $"{RuntimeSettingsComposer.AzureCosmosEndpoint})";
        }

        var missingOpenAiSettings = new (string Value, string EnvVar)[]
        {
            (settings.OpenAiEndpoint, RuntimeSettingsComposer.AzureOpenAiEndpoint),
            (settings.OpenAiDeployment, RuntimeSettingsComposer.AzureOpenAiDeployment),
        }
            .Where(setting => setting.Value.Trim().Length == 0)
            .Select(setting => setting.EnvVar)
            .ToList();

        if (missingOpenAiSettings.Count > 0)
        {
            yield return "no model is configured to reason with (set " + string.Join(", ", missingOpenAiSettings) + ")";
        }

        if (settings.AppInsightsConnectionString.Trim().Length == 0)
        {
            yield return "no tracing backend is configured (set "
                + $"{RuntimeSettingsComposer.ApplicationInsightsConnectionString})";
        }
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
