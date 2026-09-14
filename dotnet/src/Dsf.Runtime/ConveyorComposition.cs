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
/// The production composer: merges nonsecret owner-index integrations with local
/// environment overrides and wires served A2A source agents, the GitHub REST issue filer, the
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
    IConfigurationSettingsGateway? configurationSettingsGateway = null,
    Azure.Core.TokenCredential? juryCredential = null) : IConveyorComposer
{
    private const string KindPlaceholder = "{kind}";
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();
    private readonly IConfigurationSettingsGateway configurationSettingsGateway =
        configurationSettingsGateway ?? new AzureConfigurationSettingsGateway();

    public ConveyorServices ComposeFor(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var environment = new Dictionary<string, string?>(settings.IntegrationSettings, StringComparer.Ordinal);
        foreach (var (key, value) in env)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                environment[key] = value;
            }
        }

        var missing = new List<string>();
        var gatherers = ComposeGatherers(settings.Product, environment);
        var filer = ComposeFiler(settings, missing, environment);
        var runStore = ComposeRunStore(settings, missing, environment);
        var modelClient = ComposeModelClient(settings, missing);
        var tracer = ComposeTracer(settings, missing);

        if (missing.Count > 0)
        {
            throw new RuntimeConfigurationException(
                $"the runtime for product '{settings.Product}' cannot be composed: "
                + string.Join("; ", Explain(settings)),
                missing);
        }

        var jurySettings = JurySettings.Read(environment);
        var deliberation = DeliberationSettings.Read(environment);
        var lenses = deliberation.Lenses.Where(lens => lens.Enabled).Select(lens => (IDeliberationLens)(lens.Name switch
        {
            "value" => ModelDeliberationLens.Value(lens.Weight),
            "cost" => ModelDeliberationLens.Cost(lens.Weight),
            "feasibility" => ModelDeliberationLens.Feasibility(lens.Weight),
            "security" => ModelDeliberationLens.Security(lens.Weight),
            "strategic-fit" => ModelDeliberationLens.StrategicFit(lens.Weight),
            _ => throw new InvalidOperationException($"unknown deliberation lens '{lens.Name}'"),
        })).ToArray();
        var jurors = jurySettings.Jurors.Select(juror => (IValidationJuror)new ModelValidationJuror(
            juror.Name, "whether this recommendation is justified by the evidence and deliberation",
            new AzureFoundryJuryModelClient(juror, juryCredential, httpClient), juror)).ToArray();

        // The learning store is enrichment, not a requirement: a factory whose
        // Cosmos endpoint is unset still composes and runs the line exactly as
        // before, just without any prior verdict for synthesis to consult.
        var learningStore = ComposeLearningStore(settings, environment);
        var confidenceThresholdReader = new AzureAppConfigurationConfidenceThresholdReader(
            configurationSettingsGateway, settings);

        return new ConveyorServices(
            settings.Product, gatherers, filer, runStore!, modelClient!, tracer!, confidenceThresholdReader,
            learningStore, DeliberationLenses: lenses, ValidationJurors: jurors,
            ProductMaturity: settings.CreationMaturity, DeliberationRounds: deliberation.Rounds, JuryTimeout: jurySettings.Timeout,
            ProblemIdentityResolver: CosmosLearningStoreFactory.CreateProblemIdentityResolver(settings, environment, cosmosGateway));
    }

    /// <summary>
    /// Wires the same Cosmos endpoint the run store uses, in a separate container
    /// (<see cref="RuntimeIntegrationSettings.CosmosLearningContainer"/>), so
    /// synthesis can consult any human verdict already recorded for a recurring
    /// intent. Returns <c>null</c> when no Cosmos endpoint is configured, rather
    /// than raising -- learning is enrichment the filing/persistence requirements
    /// above already gate, not a new hard requirement of its own.
    /// </summary>
    private ILearningStore? ComposeLearningStore(RuntimeSettings settings, IReadOnlyDictionary<string, string?> environment)
    {
        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            return null;
        }

        return CosmosLearningStoreFactory.Create(settings, environment, cosmosGateway);
    }

    /// <summary>
    /// One served-agent gatherer per known source kind whose endpoint resolves
    /// (a per-kind <see cref="RuntimeIntegrationSettings.SourceAgentEndpoint"/>,
    /// or the <see cref="RuntimeIntegrationSettings.SourceAgentEndpointTemplate"/>
    /// with <c>{kind}</c> substituted). S2 investigation always calls out to a
    /// served source agent over A2A (<c>/gather</c>) -- there is no in-process
    /// gathering path. A kind whose endpoint cannot be resolved is left
    /// uncomposed here rather than defaulting to anything in-process: a run
    /// scoped to that kind fails at S2 with the kind and setting named, exactly
    /// as a served agent's own <c>/gather</c> endpoint would if it were
    /// unreachable.
    /// </summary>
    private IReadOnlyList<IEvidenceGatherer> ComposeGatherers(string product, IReadOnlyDictionary<string, string?> environment)
    {
        var template = Read(environment, RuntimeIntegrationSettings.SourceAgentEndpointTemplate);
        var gatherers = new List<IEvidenceGatherer>();
        foreach (var kind in SourceAgentKinds.Known)
        {
            var endpoint = Read(environment, RuntimeIntegrationSettings.SourceAgentEndpoint(kind));
            if (endpoint.Length == 0 && template.Length > 0)
            {
                endpoint = template.Replace(KindPlaceholder, kind, StringComparison.OrdinalIgnoreCase);
            }

            if (endpoint.Length > 0)
            {
                gatherers.Add(new SourceAgentEvidenceGatherer(kind, product, new Uri(EnsureTrailingSlash(endpoint)), httpClient));
            }
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
    private IIssueFiler? ComposeFiler(
        RuntimeSettings settings, List<string> missing, IReadOnlyDictionary<string, string?> environment)
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
                    Read(environment, RuntimeIntegrationSettings.GitHubApiUrl),
                    BuildGitHubAppAuthProvider(appId, installationId, keyVaultUri, privateKeySecret,
                        Read(environment, RuntimeIntegrationSettings.GitHubApiUrl)),
                    repository,
                    assignCloudAgent: string.Equals(
                        Read(environment, RuntimeIntegrationSettings.AssignCloudAgentToFiledIssues),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                : null;
        }

        missing.AddRange(appSettings.Where(setting => setting.Value.Length == 0).Select(setting => setting.EnvVar));
        return null;
    }

    private IGitHubAuthProvider BuildGitHubAppAuthProvider(
        string appId, string installationId, string keyVaultUri, string privateKeySecret, string apiUrl) =>
        GitHubAppAuthProviderFactory.Build(
            appId,
            installationId,
            keyVaultUri,
            privateKeySecret,
            apiUrl,
            privateKeySecretReader);

    private IRunStore? ComposeRunStore(
        RuntimeSettings settings, List<string> missing, IReadOnlyDictionary<string, string?> environment)
    {
        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureCosmosEndpoint);
            return null;
        }

        var database = Read(environment, RuntimeIntegrationSettings.CosmosDatabase);
        var container = Read(environment, RuntimeIntegrationSettings.CosmosContainer);
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

    private static string Read(IReadOnlyDictionary<string, string?> environment, string name) =>
        (environment.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
