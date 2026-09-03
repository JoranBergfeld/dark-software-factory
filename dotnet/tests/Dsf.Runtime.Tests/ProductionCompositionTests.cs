using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The production dependency composition must be complete or fail by name. A
/// runtime that cannot reach its source agents, cannot file, or cannot persist
/// what it decided must say which setting is unset -- it must never compose an
/// empty dependency set that lets a non-dry run finish "successfully" having
/// gathered nothing and filed nothing.
/// </summary>
public sealed class ProductionCompositionTests
{
    private static RuntimeSettings SettingsWith(
        string cosmosEndpoint = "https://cosmos.example",
        string repository = "acme/acme",
        string keyVaultUri = "",
        string githubAppId = "",
        string githubInstallationId = "",
        string githubAppPrivateKeySecret = "") => new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: keyVaultUri,
        AppInsightsConnectionString: "",
        CosmosEndpoint: cosmosEndpoint,
        OpenAiEndpoint: "https://openai.example",
        OpenAiDeployment: "gpt-deploy",
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

    private static readonly Dictionary<string, string?> FullyConfigured = new()
    {
        ["DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE"] = "https://acme-{kind}.internal",
        ["GITHUB_TOKEN"] = "ghp_test",
    };

    [Fact]
    public void Production_composition_without_source_agent_endpoints_names_the_unset_settings()
    {
        var dependencies = RuntimeDependencies.Production(
            new Dictionary<string, string?> { ["GITHUB_TOKEN"] = "ghp_test" });

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWith()));

        Assert.Contains("DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE", exception.Message);
        Assert.Contains("DSF_SOURCE_AGENT_ENDPOINT_SENTRY", exception.Message);
    }

    [Fact]
    public void Production_composition_without_a_github_token_names_the_unset_setting()
    {
        var dependencies = RuntimeDependencies.Production(new Dictionary<string, string?>
        {
            ["DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE"] = "https://acme-{kind}.internal",
        });

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWith()));

        Assert.Contains("GITHUB_TOKEN", exception.Message);
    }

    [Fact]
    public void Production_composition_without_a_repository_names_the_unset_setting()
    {
        var dependencies = RuntimeDependencies.Production(FullyConfigured);

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWith(repository: "")));

        Assert.Contains("GITHUB_REPOSITORY", exception.Message);
    }

    [Fact]
    public void Production_composition_without_a_persistence_endpoint_names_the_unset_setting()
    {
        var dependencies = RuntimeDependencies.Production(FullyConfigured);

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => dependencies.ConveyorServicesFor(SettingsWith(cosmosEndpoint: "")));

        Assert.Contains("AZURE_COSMOS_ENDPOINT", exception.Message);
    }

    [Fact]
    public void Fully_configured_production_composition_wires_gatherers_a_filer_and_a_run_store()
    {
        var services = RuntimeDependencies.Production(FullyConfigured).ConveyorServicesFor(SettingsWith());

        Assert.NotNull(services.IssueFiler);
        Assert.NotNull(services.RunStore);
        foreach (var kind in SourceAgentKinds.Known)
        {
            Assert.NotNull(services.GathererFor(kind));
        }
    }

    [Fact]
    public void Production_composition_honours_per_kind_endpoint_overrides()
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_SOURCE_AGENT_ENDPOINT_SENTRY"] = "https://sentry-agent.internal",
            ["GITHUB_TOKEN"] = "ghp_test",
        };

        var services = RuntimeDependencies.Production(env).ConveyorServicesFor(SettingsWith());

        Assert.NotNull(services.GathererFor("sentry"));
        Assert.Null(services.GathererFor("grafana"));
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
    public void GitHub_App_settings_take_precedence_over_a_dev_token_when_both_are_present()
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
        var env = new Dictionary<string, string?>(FullyConfigured) { ["GITHUB_TOKEN"] = null, ["GH_TOKEN"] = null };
        var composer = new EnvironmentConveyorComposer(env, privateKeySecretReader: new StubPrivateKeySecretReader());

        var exception = Assert.Throws<RuntimeConfigurationException>(() => composer.ComposeFor(settings));

        Assert.Contains("GITHUB_INSTALLATION_ID", exception.Message);
        Assert.Contains("GITHUB_APP_PRIVATE_KEY_SECRET", exception.Message);
        Assert.Contains("AZURE_KEYVAULT_URI", exception.Message);
    }

    [Fact]
    public void No_GitHub_auth_configured_at_all_names_the_App_settings_and_the_dev_override()
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
        Assert.Contains("GITHUB_TOKEN", exception.Message);
    }
}
