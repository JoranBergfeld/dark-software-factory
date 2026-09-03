using Dsf.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Unit tests for <see cref="AzureAppConfigurationOwnerRuntimeIndexReader"/>: the
/// managed-identity-capable production reader of the owner App Configuration
/// runtime index. Exercises the reader's mapping/error-handling logic against a
/// hand-written <see cref="IConfigurationSettingsGateway"/> double (no live Azure
/// subscription, no <c>az</c> CLI process) so ACA/container managed-identity
/// deployments -- which have no interactive <c>az login</c> -- are exercised the
/// same way production is.
/// </summary>
public sealed class AzureAppConfigurationOwnerRuntimeIndexReaderTests
{
    [Fact]
    public async Task Maps_every_listed_setting_into_the_result_and_passes_through_endpoint_and_label()
    {
        var gateway = new RecordingConfigurationSettingsGateway(
        [
            ("AZURE_APPCONFIG_ENDPOINT", "https://appconfig.example"),
            ("AZURE_COSMOS_ENDPOINT", "https://cosmos.example"),
        ]);
        var reader = new AzureAppConfigurationOwnerRuntimeIndexReader(gateway);

        var values = await reader.ReadAsync("https://owner-appconfig.example", "acme", CancellationToken.None);

        Assert.Equal("https://appconfig.example", values["AZURE_APPCONFIG_ENDPOINT"]);
        Assert.Equal("https://cosmos.example", values["AZURE_COSMOS_ENDPOINT"]);
        Assert.Equal("https://owner-appconfig.example", gateway.RequestedEndpoint);
        Assert.Equal("acme", gateway.RequestedLabel);
    }

    [Fact]
    public async Task Empty_listing_fails_loudly_naming_the_product_and_endpoint()
    {
        var reader = new AzureAppConfigurationOwnerRuntimeIndexReader(new RecordingConfigurationSettingsGateway([]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("https://owner-appconfig.example", "acme", CancellationToken.None));

        Assert.Contains("acme", exception.Message);
        Assert.Contains("https://owner-appconfig.example", exception.Message);
    }

    [Fact]
    public async Task Gateway_failure_is_wrapped_naming_the_product_and_endpoint()
    {
        var reader = new AzureAppConfigurationOwnerRuntimeIndexReader(
            new ThrowingConfigurationSettingsGateway(new InvalidOperationException("401 Unauthorized")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("https://owner-appconfig.example", "acme", CancellationToken.None));

        Assert.Contains("acme", exception.Message);
        Assert.Contains("https://owner-appconfig.example", exception.Message);
        Assert.Contains("401 Unauthorized", exception.Message);
    }

    [Fact]
    public async Task Cancellation_propagates_without_being_wrapped()
    {
        var reader = new AzureAppConfigurationOwnerRuntimeIndexReader(
            new ThrowingConfigurationSettingsGateway(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => reader.ReadAsync("https://owner-appconfig.example", "acme", CancellationToken.None));
    }

    private sealed class RecordingConfigurationSettingsGateway(
        IReadOnlyList<(string Key, string Value)> settings) : IConfigurationSettingsGateway
    {
        public string? RequestedEndpoint { get; private set; }

        public string? RequestedLabel { get; private set; }

        public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
            string endpoint, string label, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedEndpoint = endpoint;
            RequestedLabel = label;
            foreach (var setting in settings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return setting;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingConfigurationSettingsGateway(Exception exception) : IConfigurationSettingsGateway
    {
        public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
            string endpoint, string label, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw exception;
#pragma warning disable CS0162 // unreachable code: required so the method remains a valid async iterator
            yield break;
#pragma warning restore CS0162
        }
    }
}
