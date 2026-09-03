using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The production dependency composition must be complete or fail by name. A
/// runtime that cannot reach its source agents, cannot file, cannot persist what
/// it decided, cannot reason over evidence, or cannot trace its own stations must
/// say which setting is unset -- it must never compose an empty dependency set
/// that lets a non-dry run finish "successfully" having gathered nothing and
/// filed nothing.
/// </summary>
public sealed class ProductionCompositionTests
{
    private static RuntimeSettings SettingsWith(
        string cosmosEndpoint = "https://cosmos.example",
        string repository = "acme/acme",
        string keyVaultUri = "",
        string githubAppId = "",
        string githubInstallationId = "",
        string githubAppPrivateKeySecret = "",
        string appInsightsConnectionString = "InstrumentationKey=abc123",
        string openAiEndpoint = "https://openai.example",
        string openAiDeployment = "gpt-deploy") => new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: keyVaultUri,
        AppInsightsConnectionString: appInsightsConnectionString,
        CosmosEndpoint: cosmosEndpoint,
        OpenAiEndpoint: openAiEndpoint,
        OpenAiDeployment: openAiDeployment,
        OpenAiEmbeddingDeployment: "embed-deploy",
        GitHubAppId: githubAppId,
        GitHubInstallationId: githubInstallationId,
        GitHubAppPrivateKeySecret: githubAppPrivateKeySecret,
        GitHubRepository: repository);

    private static RuntimeSettings SettingsWithGitHubApp(
        string cosmosEndpoint = "https://cosmos.example", string repository = "acme/acme") => SettingsWith(
        cosmosEndpoint: cosmosEndpoint,
        repository: repository,
        keyVaultUri: "https://acme-kv.vault.azure.net/",
        githubAppId: "12345",
        githubInstallationId: "67890",
        githubAppPrivateKeySecret: "gh-app-private-key");

    private sealed class StubPrivateKeySecretReader : IPrivateKeySecretReader
    {
        public Task<string> GetSecretAsync(Uri vaultUri, string secretName, CancellationToken cancellationToken) =>
            Task.FromResult("unused-in-these-tests");
    }

    /// <summary>
    /// Everything a production composition needs, using the real GitHub App
    /// settings -- no <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c> anywhere, so any test
    /// built on this fixture proves the App settings alone are sufficient.
    /// </summary>
    private static readonly Dictionary<string, string?> FullyConfigured = new()
    {
        ["DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE"] = "https://acme-{kind}.internal",
    };

    private static RuntimeDependencies ProductionDependencies(
        Dictionary<string, string?>? env = null, IPrivateKeySecretReader? privateKeySecretReader = null) =>
        new(
            new AzureAppConfigurationOwnerRuntimeIndexReader(),
            new AzureAppConfigurationSourceAgentRosterReader(),
            new WebApplicationHostRunner(),
            new EnvironmentConveyorComposer(
                env ?? FullyConfigured,
                privateKeySecretReader: privateKeySecretReader ?? new StubPrivateKeySecretReader()),
            new HttpSourceIntegration(env ?? FullyConfigured),
            new EnvironmentLearningComposer(
                env ?? FullyConfigured,
                privateKeySecretReader: privateKeySecretReader ?? new StubPrivateKeySecretReader()));

    [Fact]
    public void Production_composition_without_any_source_agent_endpoint_still_wires_in_process_gatherers_for_every_known_kind()
    {
        var dependencies = ProductionDependencies(new Dictionary<string, string?>());

        var services = dependencies.ConveyorServicesFor(SettingsWithGitHubApp());

        foreach (var kind in SourceAgentKinds.Known)
        {
            Assert.IsType<InProcessEvidenceGatherer>(services.GathererFor(kind));
        }
    }

    /// <summary>
    /// In-process is the default evidence path (ADR: source agents run in-process
    /// unless a served agent endpoint is explicitly configured for that kind): a
    /// factory with no <c>DSF_SOURCE_AGENT_ENDPOINT*</c> setting at all must still
    /// compose, gathering directly from each kind's upstream integration rather
    /// than requiring a separately served A2A agent.
    /// </summary>
    [Fact]
    public async Task An_in_process_gatherer_reads_evidence_directly_from_the_kinds_configured_integration()
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_SOURCE_SENTRY_ENDPOINT"] = "unused-by-the-scripted-integration",
        };
        var dependencies = new RuntimeDependencies(
            new AzureAppConfigurationOwnerRuntimeIndexReader(),
            new AzureAppConfigurationSourceAgentRosterReader(),
            new WebApplicationHostRunner(),
            new EnvironmentConveyorComposer(
                env,
                privateKeySecretReader: new StubPrivateKeySecretReader(),
                sourceIntegration: new ScriptedSourceIntegration(new EvidenceItem("sentry", "SENTRY-9", "queue backed up"))),
            new ScriptedSourceIntegration(),
            new EnvironmentLearningComposer(env, privateKeySecretReader: new StubPrivateKeySecretReader()));

        var services = dependencies.ConveyorServicesFor(SettingsWithGitHubApp());
        var gatherer = services.GathererFor("sentry")!;
        var evidence = await gatherer.GatherAsync(
            new ConveyorRun { SourceKinds = ["sentry"], ProductHints = ["acme"] }, CancellationToken.None);

        var item = Assert.Single(evidence);
        Assert.Equal("SENTRY-9", item.Reference);
    }

    /// <summary>
    /// An in-process gatherer for a kind whose upstream integration is
    /// unconfigured must fail at gather time naming the unset setting -- exactly
    /// like the served agent's own <c>/gather</c> endpoint does -- rather than
    /// composing successfully and then reporting an empty investigation.
    /// </summary>
    [Fact]
    public async Task An_in_process_gatherer_names_the_unset_integration_setting_when_asked_to_gather()
    {
        var dependencies = ProductionDependencies(new Dictionary<string, string?>());
        var services = dependencies.ConveyorServicesFor(SettingsWithGitHubApp());
        var gatherer = services.GathererFor("grafana")!;

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => gatherer.GatherAsync(new ConveyorRun { SourceKinds = ["grafana"] }, CancellationToken.None));

        Assert.Contains("DSF_SOURCE_GRAFANA_ENDPOINT", exception.Message);
    }

    [Fact]
    public void Production_composition_without_github_app_settings_names_the_unset_settings()
    {
        var dependencies = ProductionDependencies();

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWith()));

        Assert.Contains("GITHUB_APP_ID", exception.Message);
        Assert.Contains("GITHUB_INSTALLATION_ID", exception.Message);
        Assert.Contains("GITHUB_APP_PRIVATE_KEY_SECRET", exception.Message);
        Assert.Contains("AZURE_KEYVAULT_URI", exception.Message);
    }

    /// <summary>
    /// The core of finding #1: a <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c> present in
    /// the environment must never substitute for the GitHub App settings, in any
    /// environment -- production has no development opt-in for this. Incomplete
    /// App settings must fail exactly as loudly with a bare PAT present as without
    /// one.
    /// </summary>
    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GH_TOKEN")]
    public void Production_composition_never_falls_back_to_a_PAT_when_App_settings_are_incomplete(string variable)
    {
        var env = new Dictionary<string, string?>(FullyConfigured) { [variable] = "ghp_test" };
        var dependencies = ProductionDependencies(env);

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWith()));

        Assert.Contains("GITHUB_APP_ID", exception.Message);
        Assert.DoesNotContain("local-dev", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(variable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_composition_never_wires_a_filer_from_a_PAT_alone_even_with_a_repository_configured()
    {
        var env = new Dictionary<string, string?>(FullyConfigured) { ["GITHUB_TOKEN"] = "ghp_test" };
        var dependencies = ProductionDependencies(env);

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWith(repository: "acme/acme")));

        Assert.Contains("GITHUB_APP_ID", exception.Message);
    }

    [Fact]
    public void Production_composition_without_a_repository_names_the_unset_setting()
    {
        var dependencies = ProductionDependencies();

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWithGitHubApp(repository: "")));

        Assert.Contains("GITHUB_REPOSITORY", exception.Message);
    }

    [Fact]
    public void Production_composition_without_a_persistence_endpoint_names_the_unset_setting()
    {
        var dependencies = ProductionDependencies();

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWithGitHubApp(cosmosEndpoint: "")));

        Assert.Contains("AZURE_COSMOS_ENDPOINT", exception.Message);
    }

    [Fact]
    public void Production_composition_without_azure_openai_settings_names_the_unset_settings()
    {
        var dependencies = ProductionDependencies();

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(
                SettingsWithGitHubApp() with { OpenAiEndpoint = "", OpenAiDeployment = "" }));

        Assert.Contains("AZURE_OPENAI_ENDPOINT", exception.Message);
        Assert.Contains("AZURE_OPENAI_DEPLOYMENT", exception.Message);
    }

    [Fact]
    public void Production_composition_without_an_application_insights_connection_string_names_the_unset_setting()
    {
        var dependencies = ProductionDependencies();

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(
                SettingsWithGitHubApp() with { AppInsightsConnectionString = "" }));

        Assert.Contains("APPLICATIONINSIGHTS_CONNECTION_STRING", exception.Message);
    }

    [Fact]
    public void Fully_configured_production_composition_wires_gatherers_a_filer_a_run_store_a_model_client_and_a_tracer()
    {
        var services = ProductionDependencies().ConveyorServicesFor(SettingsWithGitHubApp());

        Assert.NotNull(services.IssueFiler);
        Assert.NotNull(services.RunStore);
        Assert.NotNull(services.ModelClient);
        Assert.NotNull(services.Tracer);
        foreach (var kind in SourceAgentKinds.Known)
        {
            Assert.NotNull(services.GathererFor(kind));
        }
    }

    [Fact]
    public void Fully_configured_production_composition_wires_a_confidence_threshold_reader_the_council_can_consult()
    {
        var services = ProductionDependencies().ConveyorServicesFor(SettingsWithGitHubApp());

        Assert.IsType<AzureAppConfigurationConfidenceThresholdReader>(services.ConfidenceThresholdReader);
    }

    [Fact]
    public void Fully_configured_production_composition_also_wires_a_learning_store_for_synthesis_to_consult()
    {
        var services = ProductionDependencies().ConveyorServicesFor(SettingsWithGitHubApp());

        Assert.NotNull(services.LearningStore);
        Assert.IsType<CosmosLearningStore>(services.LearningStore);
    }

    [Fact]
    public void Production_composition_honours_per_kind_endpoint_overrides()
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_SOURCE_AGENT_ENDPOINT_SENTRY"] = "https://sentry-agent.internal",
        };

        var services = ProductionDependencies(env).ConveyorServicesFor(SettingsWithGitHubApp());

        Assert.IsType<SourceAgentEvidenceGatherer>(services.GathererFor("sentry"));
        Assert.IsType<InProcessEvidenceGatherer>(services.GathererFor("grafana"));
    }

    [Fact]
    public void Composition_succeeds_from_existing_GitHub_App_settings_with_no_GITHUB_TOKEN_configured()
    {
        var composer = new EnvironmentConveyorComposer(
            FullyConfigured,
            privateKeySecretReader: new StubPrivateKeySecretReader());

        var services = composer.ComposeFor(SettingsWithGitHubApp());

        Assert.NotNull(services.IssueFiler);
    }

    [Fact]
    public void GitHub_App_settings_are_used_even_when_a_dev_token_is_also_present()
    {
        var envWithDevToken = new Dictionary<string, string?>(FullyConfigured) { ["GITHUB_TOKEN"] = "ghp_test" };
        var composer = new EnvironmentConveyorComposer(
            envWithDevToken,
            privateKeySecretReader: new StubPrivateKeySecretReader());

        var services = composer.ComposeFor(SettingsWithGitHubApp());

        Assert.NotNull(services.IssueFiler);
        var authProviderField = services.IssueFiler!.GetType()
            .GetField("authProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var authProvider = authProviderField?.GetValue(services.IssueFiler);
        Assert.IsType<GitHubAppAuthProvider>(authProvider);
    }

    [Fact]
    public void Partially_configured_GitHub_App_settings_are_named_loudly()
    {
        var settings = SettingsWith(githubAppId: "12345");
        var composer = new EnvironmentConveyorComposer(FullyConfigured, privateKeySecretReader: new StubPrivateKeySecretReader());

        var exception = Assert.Throws<RuntimeConfigurationException>(() => composer.ComposeFor(settings));

        Assert.Contains("GITHUB_INSTALLATION_ID", exception.Message);
        Assert.Contains("GITHUB_APP_PRIVATE_KEY_SECRET", exception.Message);
        Assert.Contains("AZURE_KEYVAULT_URI", exception.Message);
    }

    [Fact]
    public void No_GitHub_auth_configured_at_all_names_only_the_App_settings()
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE"] = "https://acme-{kind}.internal",
        };
        var composer = new EnvironmentConveyorComposer(env, privateKeySecretReader: new StubPrivateKeySecretReader());

        var exception = Assert.Throws<RuntimeConfigurationException>(() => composer.ComposeFor(SettingsWith()));

        Assert.Contains("GITHUB_APP_ID", exception.Message);
        Assert.Contains("GITHUB_INSTALLATION_ID", exception.Message);
        Assert.Contains("GITHUB_APP_PRIVATE_KEY_SECRET", exception.Message);
        Assert.Contains("AZURE_KEYVAULT_URI", exception.Message);
        Assert.DoesNotContain("GITHUB_TOKEN", exception.Message);
        Assert.DoesNotContain("local-dev", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
