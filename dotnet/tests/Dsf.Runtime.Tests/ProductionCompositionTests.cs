using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
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
    private static RuntimeSettings SettingsWith(string cosmosEndpoint = "https://cosmos.example",
        string repository = "acme/acme") => new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: "",
        AppInsightsConnectionString: "",
        CosmosEndpoint: cosmosEndpoint,
        OpenAiEndpoint: "https://openai.example",
        OpenAiDeployment: "gpt-deploy",
        OpenAiEmbeddingDeployment: "embed-deploy",
        GitHubAppId: "",
        GitHubInstallationId: "",
        GitHubAppPrivateKeySecret: "",
        GitHubRepository: repository);

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
}
