using System.Runtime.CompilerServices;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Unit tests for the production sweep control store: the operator's paused flag
/// and interval live as unlabelled <c>sweep-paused</c>/<c>sweep-interval-seconds</c>
/// entries on the product's own App Configuration store, exactly matching the
/// confidence threshold reader's key convention. Exercised against a hand-written
/// gateway double, never a live subscription.
/// </summary>
public sealed class SweepControlTests
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
    public async Task A_store_with_neither_key_reports_unset()
    {
        var gateway = new RecordingConfigurationSettingsGateway([]);

        var state = await new AzureAppConfigurationSweepControlStore(gateway, Settings)
            .ReadAsync(CancellationToken.None);

        Assert.Equal(ProductConfigurationKeys.NoLabel, gateway.ReadLabel);
        Assert.Equal(SweepControlState.Unset, state);
    }

    [Fact]
    public async Task Reads_the_products_own_unlabelled_paused_and_interval_entries()
    {
        var gateway = new RecordingConfigurationSettingsGateway(
        [
            (ProductConfigurationKeys.SweepPaused, "true"),
            (ProductConfigurationKeys.SweepIntervalSeconds, "120"),
        ]);

        var state = await new AzureAppConfigurationSweepControlStore(gateway, Settings)
            .ReadAsync(CancellationToken.None);

        Assert.True(state.Paused);
        Assert.Equal(120, state.IntervalSeconds);
    }

    [Fact]
    public async Task SetPausedAsync_writes_the_unlabelled_paused_key()
    {
        var gateway = new RecordingConfigurationSettingsGateway([]);

        await new AzureAppConfigurationSweepControlStore(gateway, Settings)
            .SetPausedAsync(true, CancellationToken.None);

        Assert.Equal((ProductConfigurationKeys.SweepPaused, "true", null), gateway.Written);
    }

    [Fact]
    public async Task SetIntervalSecondsAsync_writes_the_unlabelled_interval_key()
    {
        var gateway = new RecordingConfigurationSettingsGateway([]);

        await new AzureAppConfigurationSweepControlStore(gateway, Settings)
            .SetIntervalSecondsAsync(45, CancellationToken.None);

        Assert.Equal((ProductConfigurationKeys.SweepIntervalSeconds, "45", null), gateway.Written);
    }

    [Fact]
    public async Task SetIntervalSecondsAsync_rejects_a_non_positive_interval()
    {
        var gateway = new RecordingConfigurationSettingsGateway([]);
        var store = new AzureAppConfigurationSweepControlStore(gateway, Settings);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SetIntervalSecondsAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_reports_an_unreachable_store_by_the_products_endpoint()
    {
        var gateway = new FailingConfigurationSettingsGateway(new HttpRequestException("403 Forbidden"));

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => new AzureAppConfigurationSweepControlStore(gateway, Settings).ReadAsync(CancellationToken.None));

        Assert.Contains("acme", exception.Message);
        Assert.Contains("https://appconfig.example", exception.Message);
    }

    private sealed class RecordingConfigurationSettingsGateway(
        IReadOnlyList<(string Key, string Value)> settings) : IConfigurationSettingsGateway
    {
        public (string Key, string Value, string? Label)? Written { get; private set; }
        public string? ReadLabel { get; private set; }

        public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
            string endpoint, string label, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadLabel = label;
            foreach (var setting in settings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return setting;
            }

            await Task.CompletedTask;
        }

        public Task SetAsync(string endpoint, string key, string value, string? label, CancellationToken cancellationToken)
        {
            Written = (key, value, label);
            return Task.CompletedTask;
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

        public Task SetAsync(string endpoint, string key, string value, string? label, CancellationToken cancellationToken) =>
            throw new NotSupportedException("this test double is read-only.");
    }
}
