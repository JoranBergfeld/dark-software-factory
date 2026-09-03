using System.Runtime.CompilerServices;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Unit tests for the production confidence threshold reader: the council's
/// acceptance bar is read from the product's own App Configuration store using
/// the exact unlabelled <c>threshold.&lt;product&gt;</c> key Control Center writes
/// through <c>AppConfigurationProductPolicyAuthority.SetConfidenceThresholdAsync</c>.
/// Exercised against a hand-written gateway double, never a live subscription.
/// </summary>
public sealed class AzureAppConfigurationConfidenceThresholdReaderTests
{
    private static readonly RuntimeSettings Settings = new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: "",
        AppInsightsConnectionString: "",
        CosmosEndpoint: "https://cosmos.example",
        OpenAiEndpoint: "https://openai.example",
        OpenAiDeployment: "gpt",
        OpenAiEmbeddingDeployment: "embed",
        GitHubAppId: "",
        GitHubInstallationId: "",
        GitHubAppPrivateKeySecret: "",
        GitHubRepository: "");

    [Fact]
    public async Task Reads_the_products_own_unlabelled_threshold_entry()
    {
        var gateway = new LabelledConfigurationSettingsGateway(new Dictionary<string, (string, string)[]>
        {
            ["\0"] = [("threshold.acme", "0.85"), ("threshold.other-product", "0.2")],
        });

        var threshold = await new AzureAppConfigurationConfidenceThresholdReader(gateway, Settings)
            .ReadThresholdAsync(CancellationToken.None);

        Assert.Equal(0.85, threshold);
    }

    [Fact]
    public async Task Falls_back_to_the_councils_documented_default_when_no_entry_is_configured()
    {
        var gateway = new LabelledConfigurationSettingsGateway(new Dictionary<string, (string, string)[]>());

        var threshold = await new AzureAppConfigurationConfidenceThresholdReader(gateway, Settings)
            .ReadThresholdAsync(CancellationToken.None);

        Assert.Equal(S5Council.DefaultThreshold, threshold);
    }

    [Fact]
    public async Task Falls_back_to_the_default_when_the_stored_value_does_not_parse_as_a_finite_number()
    {
        var gateway = new LabelledConfigurationSettingsGateway(new Dictionary<string, (string, string)[]>
        {
            ["\0"] = [("threshold.acme", "not-a-number")],
        });

        var threshold = await new AzureAppConfigurationConfidenceThresholdReader(gateway, Settings)
            .ReadThresholdAsync(CancellationToken.None);

        Assert.Equal(S5Council.DefaultThreshold, threshold);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    public async Task Falls_back_to_the_default_when_the_stored_value_is_outside_the_valid_range(
        string storedValue)
    {
        var gateway = new LabelledConfigurationSettingsGateway(new Dictionary<string, (string, string)[]>
        {
            ["\0"] = [("threshold.acme", storedValue)],
        });

        var threshold = await new AzureAppConfigurationConfidenceThresholdReader(gateway, Settings)
            .ReadThresholdAsync(CancellationToken.None);

        Assert.Equal(S5Council.DefaultThreshold, threshold);
    }

    [Fact]
    public async Task An_unreadable_store_fails_loudly_naming_the_product_and_endpoint()
    {
        var gateway = new FailingConfigurationSettingsGateway(new InvalidOperationException("403 Forbidden"));

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => new AzureAppConfigurationConfidenceThresholdReader(gateway, Settings)
                .ReadThresholdAsync(CancellationToken.None));

        Assert.Contains("acme", exception.Message);
        Assert.Contains("https://appconfig.example", exception.Message);
        Assert.Contains("403 Forbidden", exception.Message);
    }

    private sealed class LabelledConfigurationSettingsGateway(
        IReadOnlyDictionary<string, (string Key, string Value)[]> byLabel) : IConfigurationSettingsGateway
    {
        public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
            string endpoint, string label, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var setting in byLabel.TryGetValue(label, out var settings) ? settings : [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return setting;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FailingConfigurationSettingsGateway(Exception exception) : IConfigurationSettingsGateway
    {
        public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
            string endpoint, string label, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw exception;
#pragma warning disable CS0162 // unreachable: keeps this a valid async iterator
            yield break;
#pragma warning restore CS0162
        }
    }
}
