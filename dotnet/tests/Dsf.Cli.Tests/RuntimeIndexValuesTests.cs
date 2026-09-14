using Dsf.Cli;
using Dsf.Core.Instances;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

/// <summary>
/// The runtime index published to owner App Configuration by <c>dsf new</c>'s
/// GitHub provisioning step must carry every setting
/// <c>RuntimeSettingsComposer</c> (the .NET runtime host) requires to compose a
/// product's <c>RuntimeSettings</c> purely from that index -- otherwise a runtime
/// command resolving configuration through the owner authority fails even though
/// the product was fully provisioned.
/// </summary>
public sealed class RuntimeIndexValuesTests
{
    [Fact]
    public void Runtime_index_carries_every_setting_the_runtime_composer_requires()
    {
        var definition = SampleDefinition();

        var values = CliApplication.RuntimeIndexValues(definition, "https://product-appconfig.example");

        Assert.Equal("paritydemo", values["DSF_PRODUCT"]);
        Assert.Equal("acme/paritydemo", values[ProductConfigurationKeys.OwnerIndexGitHubRepository]);
        Assert.Equal("https://product-appconfig.example", values[ProductConfigurationKeys.OwnerIndexAppConfigEndpoint]);
        Assert.Equal("https://cosmos.example", values["AZURE_COSMOS_ENDPOINT"]);
        Assert.Equal("https://openai.example", values["AZURE_OPENAI_ENDPOINT"]);
        Assert.Equal("gpt-deploy", values["AZURE_OPENAI_DEPLOYMENT"]);
        Assert.Equal("embed-deploy", values["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"]);
        Assert.Equal("https://keyvault.example", values["AZURE_KEYVAULT_URI"]);
        Assert.Equal("7", values["GITHUB_APP_ID"]);
        Assert.Equal("42", values["GITHUB_INSTALLATION_ID"]);
        Assert.Equal("github-app-private-key", values["GITHUB_APP_PRIVATE_KEY_SECRET"]);
        Assert.Equal("medium", values[RuntimeIntegrationSettings.CreationMaturity]);
        Assert.Equal("paritydemo", values[RuntimeIntegrationSettings.CosmosDatabase]);
        Assert.Equal("https://webiq.internal.example",
            values[RuntimeIntegrationSettings.SourceAgentEndpoint("webiq")]);
    }

    private static InstanceDefinition SampleDefinition() => new()
    {
        Product = new ProductSettings { Key = "paritydemo", CreationMaturity = "medium" },
        Runtime = new Dsf.Core.Instances.RuntimeSettings(),
        Governance = new GovernanceSettings(),
        GitHub = new GitHubSettings
        {
            Owner = "acme",
            Repository = "paritydemo",
            Visibility = "private",
            AppId = "7",
            InstallationId = "42",
            PrivateKeySecretName = "github-app-private-key",
        },
        Azure = new AzureSettings
        {
            NamePrefix = "parityde0000",
            ResourceGroup = "rg-dsf-paritydemo",
            DeploymentName = "dsf-paritydemo",
            SreAgent = new SreAgentSettings
            {
                Name = "dsf-sre-paritydemo",
                ResourceGroup = "rg-dsf-sre-paritydemo",
                MonitoredResourceGroups = ["rg-dsf-paritydemo"],
            },
            Outputs = new Dictionary<string, string>
            {
                ["cosmosEndpoint"] = "https://cosmos.example",
                ["openaiEndpoint"] = "https://openai.example",
                ["openaiDeployment"] = "gpt-deploy",
                ["openaiEmbeddingDeployment"] = "embed-deploy",
                ["keyVaultUri"] = "https://keyvault.example",
                ["sourceAgentEndpoints"] = """{"DSF_SOURCE_AGENT_ENDPOINT_WEBIQ":"https://webiq.internal.example"}""",
            },
        },
        Status = new InstanceStatus { GeneratedAt = DateTimeOffset.UnixEpoch },
    };
}
