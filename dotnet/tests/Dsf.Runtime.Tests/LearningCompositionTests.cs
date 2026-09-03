using Dsf.Core.Runtime;
using Dsf.Runtime.GitHubApp;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The learning loop's production composition must be complete or fail by name,
/// exactly like the conveyor's: an operator missing GitHub App auth, a
/// repository, or Cosmos persistence must be told which setting to set, not
/// handed a learning loop that silently polls or records nothing.
/// </summary>
public sealed class LearningCompositionTests
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
        AppInsightsConnectionString: "InstrumentationKey=abc123",
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

    private static EnvironmentLearningComposer Composer(Dictionary<string, string?>? env = null) =>
        new(env ?? [], privateKeySecretReader: new StubPrivateKeySecretReader());

    [Fact]
    public void Fully_configured_composition_wires_an_outcome_source_and_a_learning_store()
    {
        var services = Composer().ComposeFor(SettingsWithGitHubApp());

        Assert.NotNull(services.OutcomeSource);
        Assert.NotNull(services.LearningStore);
    }

    [Fact]
    public void Missing_github_app_settings_are_named()
    {
        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => Composer().ComposeFor(SettingsWith()));

        Assert.Contains("GITHUB_APP_ID", exception.Message);
        Assert.Contains("GITHUB_INSTALLATION_ID", exception.Message);
        Assert.Contains("GITHUB_APP_PRIVATE_KEY_SECRET", exception.Message);
        Assert.Contains("AZURE_KEYVAULT_URI", exception.Message);
    }

    [Fact]
    public void Missing_repository_is_named()
    {
        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => Composer().ComposeFor(SettingsWithGitHubApp(repository: "")));

        Assert.Contains("GITHUB_REPOSITORY", exception.Message);
    }

    [Fact]
    public void Missing_cosmos_endpoint_is_named()
    {
        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => Composer().ComposeFor(SettingsWithGitHubApp(cosmosEndpoint: "")));

        Assert.Contains("AZURE_COSMOS_ENDPOINT", exception.Message);
    }

    [Fact]
    public void Every_unmet_requirement_is_named_at_once()
    {
        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => Composer().ComposeFor(SettingsWith(cosmosEndpoint: "", repository: "")));

        Assert.Contains("GITHUB_APP_ID", exception.Message);
        Assert.Contains("GITHUB_REPOSITORY", exception.Message);
        Assert.Contains("AZURE_COSMOS_ENDPOINT", exception.Message);
    }
}
