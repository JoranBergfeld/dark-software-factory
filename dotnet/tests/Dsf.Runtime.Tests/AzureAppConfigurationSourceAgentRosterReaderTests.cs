using System.Runtime.CompilerServices;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Unit tests for the production source agent roster reader: which agents
/// <c>sweep</c> actually sweeps is read from the product's App Configuration store
/// using the same <c>agents.&lt;KIND&gt;.enabled</c> convention the Python config
/// store writes, with the product label overriding the unlabelled default.
/// Exercised against a hand-written gateway double, never a live subscription.
/// </summary>
public sealed class AzureAppConfigurationSourceAgentRosterReaderTests
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
    public async Task Returns_only_the_known_kinds_whose_flag_is_true()
    {
        var gateway = new LabelledConfigurationSettingsGateway(new Dictionary<string, (string, string)[]>
        {
            ["\0"] =
            [
                ("agents.SENTRY.enabled", "true"),
                ("agents.GRAFANA.enabled", "false"),
                ("agents.NOTAKIND.enabled", "true"),
                ("critics.value.enabled", "true"),
            ],
            ["acme"] = [],
        });

        var kinds = await new AzureAppConfigurationSourceAgentRosterReader(gateway)
            .ReadEnabledKindsAsync(Settings, CancellationToken.None);

        Assert.Equal(["sentry"], kinds);
    }

    [Fact]
    public async Task Product_labelled_override_wins_over_the_unlabelled_default()
    {
        var gateway = new LabelledConfigurationSettingsGateway(new Dictionary<string, (string, string)[]>
        {
            ["\0"] = [("agents.SENTRY.enabled", "true"), ("agents.GRAFANA.enabled", "false")],
            ["acme"] = [("agents.SENTRY.enabled", "false"), ("agents.GRAFANA.enabled", "true")],
        });

        var kinds = await new AzureAppConfigurationSourceAgentRosterReader(gateway)
            .ReadEnabledKindsAsync(Settings, CancellationToken.None);

        Assert.Equal(["grafana"], kinds);
    }

    [Fact]
    public async Task A_store_with_no_agent_flags_reports_an_empty_roster()
    {
        var gateway = new LabelledConfigurationSettingsGateway(new Dictionary<string, (string, string)[]>());

        var kinds = await new AzureAppConfigurationSourceAgentRosterReader(gateway)
            .ReadEnabledKindsAsync(Settings, CancellationToken.None);

        Assert.Empty(kinds);
        Assert.Equal(["https://appconfig.example", "https://appconfig.example"], gateway.RequestedEndpoints);
    }

    [Fact]
    public async Task An_unreadable_store_fails_loudly_naming_the_product_and_endpoint()
    {
        var gateway = new FailingConfigurationSettingsGateway(new InvalidOperationException("403 Forbidden"));

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => new AzureAppConfigurationSourceAgentRosterReader(gateway)
                .ReadEnabledKindsAsync(Settings, CancellationToken.None));

        Assert.Contains("acme", exception.Message);
        Assert.Contains("https://appconfig.example", exception.Message);
        Assert.Contains("403 Forbidden", exception.Message);
    }

    private sealed class LabelledConfigurationSettingsGateway(
        IReadOnlyDictionary<string, (string Key, string Value)[]> byLabel) : IConfigurationSettingsGateway
    {
        public List<string> RequestedEndpoints { get; } = [];

        public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
            string endpoint, string label, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedEndpoints.Add(endpoint);
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
