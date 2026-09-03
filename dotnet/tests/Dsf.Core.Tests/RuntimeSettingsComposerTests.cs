using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Core.Tests;

/// <summary>
/// Parity with the Python <c>AzureRuntimeSettings.from_env</c> /
/// <c>build_services</c> contract in <c>core/src/dsf/container.py</c>: reuse the
/// existing env var names, require <c>DSF_PRODUCT</c> before anything else, then
/// require every data-plane endpoint and name all of them together when more than
/// one is unset.
/// </summary>
public sealed class RuntimeSettingsComposerTests
{
    private static readonly IReadOnlyDictionary<string, string?> FullEnvironment = new Dictionary<string, string?>
    {
        ["DSF_PRODUCT"] = "acme",
        ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
        ["AZURE_KEYVAULT_URI"] = "https://keyvault.example",
        ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=abc",
        ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example",
        ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
        ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-deploy",
        ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embed-deploy",
        ["GITHUB_APP_ID"] = "123",
        ["GITHUB_INSTALLATION_ID"] = "456",
        ["GITHUB_APP_PRIVATE_KEY_SECRET"] = "gh-secret",
        ["GITHUB_REPOSITORY"] = "acme/acme",
    };

    [Fact]
    public void Composes_settings_from_the_existing_env_var_names()
    {
        var settings = RuntimeSettingsComposer.FromEnvironment(FullEnvironment);

        Assert.Equal("acme", settings.Product);
        Assert.Equal("https://appconfig.example", settings.AppConfigEndpoint);
        Assert.Equal("https://keyvault.example", settings.KeyVaultUri);
        Assert.Equal("InstrumentationKey=abc", settings.AppInsightsConnectionString);
        Assert.Equal("https://cosmos.example", settings.CosmosEndpoint);
        Assert.Equal("https://openai.example", settings.OpenAiEndpoint);
        Assert.Equal("gpt-deploy", settings.OpenAiDeployment);
        Assert.Equal("embed-deploy", settings.OpenAiEmbeddingDeployment);
        Assert.Equal("123", settings.GitHubAppId);
        Assert.Equal("456", settings.GitHubInstallationId);
        Assert.Equal("gh-secret", settings.GitHubAppPrivateKeySecret);
        Assert.Equal("acme/acme", settings.GitHubRepository);
    }

    [Fact]
    public void Missing_product_fails_loudly_before_checking_any_other_setting()
    {
        var env = new Dictionary<string, string?>(FullEnvironment) { ["DSF_PRODUCT"] = "" };

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => RuntimeSettingsComposer.FromEnvironment(env));

        Assert.Equal(
            "DSF_PRODUCT is required to scope the factory runtime (set DSF_PRODUCT=<product>).",
            exception.Message);
        Assert.Equal(["DSF_PRODUCT"], exception.MissingSettings);
    }

    [Fact]
    public void Product_override_takes_precedence_over_the_environment_value()
    {
        var env = new Dictionary<string, string?>(FullEnvironment) { ["DSF_PRODUCT"] = "" };

        var settings = RuntimeSettingsComposer.FromEnvironment(env, productOverride: "override-product");

        Assert.Equal("override-product", settings.Product);
    }

    [Fact]
    public void Missing_every_required_endpoint_names_all_of_them_together()
    {
        var env = new Dictionary<string, string?>(FullEnvironment)
        {
            ["AZURE_APPCONFIG_ENDPOINT"] = "",
            ["AZURE_COSMOS_ENDPOINT"] = "",
            ["AZURE_OPENAI_ENDPOINT"] = "",
            ["AZURE_OPENAI_DEPLOYMENT"] = "",
            ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "",
        };

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => RuntimeSettingsComposer.FromEnvironment(env));

        Assert.Equal(
            "missing required Azure runtime configuration: AZURE_APPCONFIG_ENDPOINT, " +
            "AZURE_COSMOS_ENDPOINT, AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_DEPLOYMENT, " +
            "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
            exception.Message);
        Assert.Equal(
            new[]
            {
                "AZURE_APPCONFIG_ENDPOINT",
                "AZURE_COSMOS_ENDPOINT",
                "AZURE_OPENAI_ENDPOINT",
                "AZURE_OPENAI_DEPLOYMENT",
                "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
            },
            exception.MissingSettings);
    }

    [Fact]
    public void Missing_a_single_endpoint_names_only_that_endpoint()
    {
        var env = new Dictionary<string, string?>(FullEnvironment) { ["AZURE_COSMOS_ENDPOINT"] = "" };

        var exception = Assert.Throws<RuntimeConfigurationException>(
            () => RuntimeSettingsComposer.FromEnvironment(env));

        Assert.Equal(
            "missing required Azure runtime configuration: AZURE_COSMOS_ENDPOINT",
            exception.Message);
    }

    [Fact]
    public void Optional_settings_default_to_empty_when_unset()
    {
        var env = new Dictionary<string, string?>(FullEnvironment)
        {
            ["AZURE_KEYVAULT_URI"] = null,
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = null,
            ["GITHUB_APP_ID"] = null,
            ["GITHUB_INSTALLATION_ID"] = null,
            ["GITHUB_APP_PRIVATE_KEY_SECRET"] = null,
            ["GITHUB_REPOSITORY"] = null,
        };

        var settings = RuntimeSettingsComposer.FromEnvironment(env);

        Assert.Equal(string.Empty, settings.KeyVaultUri);
        Assert.Equal(string.Empty, settings.AppInsightsConnectionString);
        Assert.Equal(string.Empty, settings.GitHubAppId);
        Assert.Equal(string.Empty, settings.GitHubInstallationId);
        Assert.Equal(string.Empty, settings.GitHubAppPrivateKeySecret);
        Assert.Equal(string.Empty, settings.GitHubRepository);
    }

    [Fact]
    public async Task ComposeAsync_fills_missing_settings_from_the_owner_runtime_index_when_configured()
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "acme",
            ["DSF_OWNER_APPCONFIG_ENDPOINT"] = "https://owner-appconfig.example",
        };
        var reader = new StubOwnerRuntimeIndexReader(new Dictionary<string, string>
        {
            ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
            ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example",
            ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-deploy",
            ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embed-deploy",
            ["GITHUB_REPOSITORY"] = "acme/acme",
        });

        var settings = await RuntimeSettingsComposer.ComposeAsync(env, productOverride: null, reader, CancellationToken.None);

        Assert.Equal("acme", settings.Product);
        Assert.Equal("https://appconfig.example", settings.AppConfigEndpoint);
        Assert.Equal("https://cosmos.example", settings.CosmosEndpoint);
        Assert.Equal("https://openai.example", settings.OpenAiEndpoint);
        Assert.Equal("gpt-deploy", settings.OpenAiDeployment);
        Assert.Equal("embed-deploy", settings.OpenAiEmbeddingDeployment);
        Assert.Equal("acme/acme", settings.GitHubRepository);
        Assert.Equal("acme", reader.RequestedProduct);
        Assert.Equal("https://owner-appconfig.example", reader.RequestedOwnerEndpoint);
    }

    [Fact]
    public async Task ComposeAsync_local_environment_values_take_precedence_over_the_owner_runtime_index()
    {
        var env = new Dictionary<string, string?>(FullEnvironment)
        {
            ["DSF_OWNER_APPCONFIG_ENDPOINT"] = "https://owner-appconfig.example",
        };
        var reader = new StubOwnerRuntimeIndexReader(new Dictionary<string, string>
        {
            ["AZURE_APPCONFIG_ENDPOINT"] = "https://remote-appconfig.example",
        });

        var settings = await RuntimeSettingsComposer.ComposeAsync(env, productOverride: null, reader, CancellationToken.None);

        Assert.Equal("https://appconfig.example", settings.AppConfigEndpoint);
    }

    [Fact]
    public async Task ComposeAsync_never_consults_the_owner_runtime_index_when_no_owner_endpoint_is_configured()
    {
        var reader = new ThrowingOwnerRuntimeIndexReader();

        var settings = await RuntimeSettingsComposer.ComposeAsync(FullEnvironment, productOverride: null, reader, CancellationToken.None);

        Assert.Equal("acme", settings.Product);
    }

    [Fact]
    public async Task ComposeAsync_fails_loudly_when_the_owner_runtime_index_lookup_fails()
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "acme",
            ["DSF_OWNER_APPCONFIG_ENDPOINT"] = "https://owner-appconfig.example",
        };
        var reader = new ThrowingOwnerRuntimeIndexReader();

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => RuntimeSettingsComposer.ComposeAsync(env, productOverride: null, reader, CancellationToken.None));

        Assert.Contains("acme", exception.Message);
        Assert.Contains("https://owner-appconfig.example", exception.Message);
    }

    private sealed class StubOwnerRuntimeIndexReader(IReadOnlyDictionary<string, string> values) : IOwnerRuntimeIndexReader
    {
        public string? RequestedOwnerEndpoint { get; private set; }

        public string? RequestedProduct { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> ReadAsync(
            string ownerAppConfigEndpoint,
            string product,
            CancellationToken cancellationToken)
        {
            RequestedOwnerEndpoint = ownerAppConfigEndpoint;
            RequestedProduct = product;
            return Task.FromResult(values);
        }
    }

    private sealed class ThrowingOwnerRuntimeIndexReader : IOwnerRuntimeIndexReader
    {
        public Task<IReadOnlyDictionary<string, string>> ReadAsync(
            string ownerAppConfigEndpoint,
            string product,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"product '{product}' is absent from the owner runtime index at '{ownerAppConfigEndpoint}'.");
    }
}
