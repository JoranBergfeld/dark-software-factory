using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
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
    [Fact]
    public async Task Local_integration_values_override_owner_values_but_blank_values_do_not_erase_them()
    {
        var integrations = new Dictionary<string, string?>(FullyConfigured)
        {
            [RuntimeIntegrationSettings.CosmosDatabase] = "owner-database",
            [RuntimeIntegrationSettings.CosmosContainer] = "owner-runs",
            [RuntimeIntegrationSettings.CosmosLearningContainer] = "owner-learning",
            [DeliberationSettings.RoundsKey] = "1",
        };
        var localModels = JurySettings.Read(FullyConfigured).Jurors.ToArray();
        localModels[0] = localModels[0] with { Deployment = "local-gpt-deployment" };
        var local = new Dictionary<string, string?>
        {
            [JurySettings.ModelsKey] = JurySettings.Create(localModels).SerializeModels(),
            [RuntimeIntegrationSettings.SourceAgentEndpoint("azuremonitor")] = "https://local-agent.example",
            [RuntimeIntegrationSettings.CosmosDatabase] = "local-database",
            [RuntimeIntegrationSettings.CosmosContainer] = "local-runs",
            [RuntimeIntegrationSettings.CosmosLearningContainer] = " ",
            [DeliberationSettings.RoundsKey] = "2",
        };
        var gateway = new IntegrationCosmosGateway();
        var handler = new SourceEndpointHandler();
        var services = new EnvironmentConveyorComposer(local, new HttpClient(handler), cosmosGateway: gateway)
            .ComposeFor(SettingsWithGitHubApp() with { IntegrationSettings = integrations });

        await services.GathererFor("azuremonitor")!.GatherAsync(
            new ConveyorRun { ProductHints = ["acme"] }, CancellationToken.None);
        await services.RunStore.LoadAsync("run", CancellationToken.None);
        await services.LearningStore!.RetrieveAsync("intent", CancellationToken.None);

        Assert.Equal("https://local-agent.example/gather", Assert.Single(handler.Endpoints));
        Assert.Equal("local-gpt-deployment", services.ValidationJurors[0].Model!.Deployment);
        Assert.Equal(2, services.DeliberationRounds);
        Assert.Contains(("local-database", "local-runs"), gateway.Reads);
        Assert.Contains(("local-database", "owner-learning"), gateway.Reads);
        Assert.Equal("owner-database", integrations[RuntimeIntegrationSettings.CosmosDatabase]);
    }

    [Fact]
    public async Task A_shared_composer_keeps_owner_integrations_isolated_between_products()
    {
        var handler = new SourceEndpointHandler();
        var composer = new EnvironmentConveyorComposer(new Dictionary<string, string?>(), new HttpClient(handler));
        var first = composer.ComposeFor(SettingsWithGitHubApp() with { IntegrationSettings = FullyConfigured });
        var secondSettings = new Dictionary<string, string?>(FullyConfigured)
        {
            [RuntimeIntegrationSettings.SourceAgentEndpointTemplate] = "https://second-{kind}.internal",
        };
        var second = composer.ComposeFor(SettingsWithGitHubApp() with
        {
            Product = "second",
            IntegrationSettings = secondSettings,
        });

        await first.GathererFor("azuremonitor")!.GatherAsync(
            new ConveyorRun { ProductHints = ["acme"] }, CancellationToken.None);
        await second.GathererFor("azuremonitor")!.GatherAsync(
            new ConveyorRun { ProductHints = ["second"] }, CancellationToken.None);

        Assert.Equal(["https://acme-azuremonitor.internal/gather", "https://second-azuremonitor.internal/gather"],
            handler.Endpoints);
    }

    [Fact]
    public async Task Owner_index_integrations_supply_source_endpoints_jury_lenses_and_cosmos_containers()
    {
        var integrations = new Dictionary<string, string?>(FullyConfigured)
        {
            [RuntimeIntegrationSettings.CosmosDatabase] = "owner-database",
            [RuntimeIntegrationSettings.CosmosContainer] = "owner-runs",
            [RuntimeIntegrationSettings.CosmosLearningContainer] = "owner-learning",
            [DeliberationSettings.RoundsKey] = "1",
            [DeliberationSettings.Key("security", "WEIGHT")] = "4",
        };
        var gateway = new IntegrationCosmosGateway();
        var handler = new SourceEndpointHandler();
        var services = new EnvironmentConveyorComposer(
            new Dictionary<string, string?>(), new HttpClient(handler), cosmosGateway: gateway)
            .ComposeFor(SettingsWithGitHubApp() with { IntegrationSettings = integrations });

        await services.GathererFor("azuremonitor")!.GatherAsync(
            new ConveyorRun { ProductHints = ["acme"] }, CancellationToken.None);
        await services.RunStore.LoadAsync("run", CancellationToken.None);
        await services.LearningStore!.RetrieveAsync("intent", CancellationToken.None);

        Assert.Equal("https://acme-azuremonitor.internal/gather", Assert.Single(handler.Endpoints));
        Assert.Equal(["gpt", "deepseek", "grok"], services.ValidationJurors.Select(juror => juror.Model!.Family));
        Assert.Equal(1, services.DeliberationRounds);
        Assert.Equal(4, services.DeliberationLenses.Single(lens => lens.Name == "security").Weight);
        Assert.Contains(("owner-database", "owner-runs"), gateway.Reads);
        Assert.Contains(("owner-database", "owner-learning"), gateway.Reads);
    }

    [Fact]
    public async Task Production_jurors_call_three_configured_real_model_targets_not_the_synthesis_gateway()
    {
        var handler = new JuryCompletionHandler();
        var services = new EnvironmentConveyorComposer(
            FullyConfigured, httpClient: new HttpClient(handler), juryCredential: new JuryCredential())
            .ComposeFor(SettingsWithGitHubApp());
        var run = new ConveyorRun();
        var proposal = new Proposal("p", "checkout timeout", ["azuremonitor"], ["ref"]);
        run.Evidence.Add(new EvidenceItem("azuremonitor", "ref", "checkout requests time out"));
        var recommendation = new LensSynthesis(true, 1, false, [new LensVerdict("value", LensPosition.Go, "real need", 1)]);

        foreach (var juror in services.ValidationJurors)
        {
            Assert.Equal(JurorPosition.Go, (await juror.ValidateAsync(proposal, run, recommendation, CancellationToken.None)).Position);
        }

        Assert.Equal(["gpt-4.1", "DeepSeek-V3", "grok-3"], handler.Deployments);
    }

    [Theory]
    [InlineData("DSF_JURY_TIMEOUT_SECONDS", "0")]
    [InlineData("DSF_DELIBERATION_ROUNDS", "3")]
    [InlineData("DSF_LENS_SECURITY_WEIGHT", "NaN")]
    [InlineData("DSF_LENS_VALUE_WEIGHT", "-1")]
    [InlineData("DSF_LENS_COST_ENABLED", "maybe")]
    public void Invalid_jury_or_deliberation_configuration_fails_by_setting_name(string setting, string value)
    {
        var env = new Dictionary<string, string?>(FullyConfigured) { [setting] = value };
        var exception = Assert.Throws<RuntimeConfigurationException>(() =>
            new EnvironmentConveyorComposer(env).ComposeFor(SettingsWithGitHubApp()));
        Assert.Contains(setting, exception.Message);
    }

    [Theory]
    [InlineData("family", "gpt")]
    [InlineData("provider", "openai")]
    [InlineData("endpoint", "http://models.example")]
    public void Invalid_juror_configuration_names_the_json_roster_setting(string field, string value)
    {
        var models = JurySettings.Read(FullyConfigured).Jurors.ToArray();
        models[1] = field switch
        {
            "family" => models[1] with { Family = value },
            "provider" => models[1] with { Provider = value },
            _ => models[1] with { Endpoint = value },
        };
        var env = new Dictionary<string, string?>(FullyConfigured)
        {
            [JurySettings.ModelsKey] = JsonSerializer.Serialize(models),
        };
        var exception = Assert.Throws<RuntimeConfigurationException>(() =>
            new EnvironmentConveyorComposer(env).ComposeFor(SettingsWithGitHubApp()));
        Assert.Contains(JurySettings.ModelsKey, exception.Message);
    }

    [Fact]
    public void A_relabelled_shared_deployment_cannot_masquerade_as_three_independent_jurors()
    {
        var models = JurySettings.Read(FullyConfigured).Jurors.ToArray();
        models[1] = models[1] with { Deployment = models[0].Deployment };
        var env = new Dictionary<string, string?>(FullyConfigured)
        {
            [JurySettings.ModelsKey] = JsonSerializer.Serialize(models),
        };
        var exception = Assert.Throws<RuntimeConfigurationException>(() =>
            new EnvironmentConveyorComposer(env).ComposeFor(SettingsWithGitHubApp()));
        Assert.Contains("distinct", exception.Message);
    }

    [Fact]
    public void Production_jury_exposes_three_distinct_model_identities_for_auditing()
    {
        var services = new EnvironmentConveyorComposer(FullyConfigured).ComposeFor(SettingsWithGitHubApp());
        Assert.Equal(["gpt", "deepseek", "grok"], services.ValidationJurors.Select(juror => juror.Model!.Family));
        Assert.Equal(["gpt-4.1", "DeepSeek-V3", "grok-3"], services.ValidationJurors.Select(juror => juror.Model!.Deployment));
    }

    [Fact]
    public void Production_composition_honours_lens_weights_enablement_rounds_and_jury_timeout()
    {
        var env = new Dictionary<string, string?>(FullyConfigured)
        {
            ["DSF_DELIBERATION_ROUNDS"] = "1",
            ["DSF_LENS_VALUE_WEIGHT"] = "3.5",
            ["DSF_LENS_COST_ENABLED"] = "false",
            ["DSF_JURY_TIMEOUT_SECONDS"] = "45",
        };

        var services = new EnvironmentConveyorComposer(env).ComposeFor(SettingsWithGitHubApp());

        Assert.Equal(1, services.DeliberationRounds);
        Assert.Equal(TimeSpan.FromSeconds(45), services.JuryTimeout);
        Assert.Equal(3.5, services.DeliberationLenses.Single(lens => lens.Name == "value").Weight);
        Assert.DoesNotContain(services.DeliberationLenses, lens => lens.Name == "cost");
        Assert.Equal(4, services.DeliberationLenses.Count);
    }

    [Fact]
    public void Production_composition_requires_three_explicit_juror_model_configurations()
    {
        var composer = new EnvironmentConveyorComposer(
            new Dictionary<string, string?>(),
            privateKeySecretReader: new StubPrivateKeySecretReader());

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => composer.ComposeFor(SettingsWithGitHubApp()));

        Assert.Contains(JurySettings.ModelsKey, exception.Message);
    }

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

    private sealed class SourceEndpointHandler : HttpMessageHandler
    {
        public List<string> Endpoints { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoints.Add(request.RequestUri!.ToString());
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    kind = document.RootElement.GetProperty("kind").GetString(),
                    product = document.RootElement.GetProperty("product").GetString(),
                    evidence = Array.Empty<object>(),
                }),
            };
        }
    }

    private sealed class IntegrationCosmosGateway : ICosmosDocumentGateway
    {
        public List<(string Database, string Container)> Reads { get; } = [];

        public Task<string?> ReadAsync(string endpoint, string database, string container, string partitionKey,
            string id, CancellationToken cancellationToken)
        {
            Reads.Add((database, container));
            return Task.FromResult<string?>(null);
        }

        public Task UpsertAsync(string endpoint, string database, string container, string partitionKey,
            string id, string json, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class JuryCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class JuryCompletionHandler : HttpMessageHandler
    {
        public List<string> Deployments { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://jury.example/openai/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var deployment = document.RootElement.GetProperty("model").GetString()!;
            Deployments.Add(deployment);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    model = deployment,
                    choices = new[] { new { finish_reason = "stop", message = new { content = "GO: evidence supports delivery" } } },
                }),
            };
        }
    }

    /// <summary>
    /// Everything a production composition needs, using the real GitHub App
    /// settings -- no <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c> anywhere, so any test
    /// built on this fixture proves the App settings alone are sufficient.
    /// </summary>
    private static readonly Dictionary<string, string?> FullyConfigured = new()
    {
        ["DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE"] = "https://acme-{kind}.internal",
        [JurySettings.ModelsKey] = """
            [
              {"provider":"openai","family":"gpt","endpoint":"https://jury.example","deployment":"gpt-4.1"},
              {"provider":"deepseek","family":"deepseek","endpoint":"https://jury.example","deployment":"DeepSeek-V3"},
              {"provider":"xai","family":"grok","endpoint":"https://jury.example","deployment":"grok-3"}
            ]
            """,
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
            new SourceIntegrationRegistry(
                SourceAgentKinds.Known.ToDictionary(
                    kind => kind,
                    _ => (ISourceIntegration)new HttpSourceIntegration(env ?? FullyConfigured),
                    StringComparer.Ordinal)),
            new EnvironmentLearningComposer(
                env ?? FullyConfigured,
                privateKeySecretReader: privateKeySecretReader ?? new StubPrivateKeySecretReader()));

    /// <summary>
    /// S2 investigation always calls out to a served source agent over A2A --
    /// there is no in-process gathering path. A factory with no
    /// <c>DSF_SOURCE_AGENT_ENDPOINT*</c> setting at all composes no gatherer for
    /// any kind: a run scoped to any of them fails at S2, naming the unset
    /// setting, rather than silently gathering nothing in-process.
    /// </summary>
    [Fact]
    public void Production_composition_without_any_source_agent_endpoint_composes_no_gatherer_for_any_kind()
    {
        var env = new Dictionary<string, string?>(FullyConfigured);
        env.Remove("DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE");
        var dependencies = ProductionDependencies(env);

        var services = dependencies.ConveyorServicesFor(SettingsWithGitHubApp());

        foreach (var kind in SourceAgentKinds.Known)
        {
            Assert.Null(services.GathererFor(kind));
        }
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
        Assert.IsType<CosmosProblemIdentityResolver>(services.ProblemIdentityResolver);
    }

    [Fact]
    public void Production_composition_honours_per_kind_endpoint_overrides()
    {
        var env = new Dictionary<string, string?>(FullyConfigured)
        {
            ["DSF_SOURCE_AGENT_ENDPOINT_AZUREMONITOR"] = "https://azuremonitor-agent.internal",
        };
        env.Remove("DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE");

        var services = ProductionDependencies(env).ConveyorServicesFor(SettingsWithGitHubApp());

        Assert.IsType<SourceAgentEvidenceGatherer>(services.GathererFor("azuremonitor"));
        Assert.Null(services.GathererFor("foundryiq"));
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
