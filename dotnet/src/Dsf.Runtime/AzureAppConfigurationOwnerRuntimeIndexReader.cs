using System.Runtime.CompilerServices;
using Azure.Data.AppConfiguration;
using Azure.Identity;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// Lists an App Configuration store's key/value settings, labeled or unlabeled.
/// The real implementation (<see cref="AzureConfigurationSettingsGateway"/>) wraps
/// the Azure SDK; tests substitute a hand-written double instead of a live
/// subscription.
/// </summary>
internal interface IConfigurationSettingsGateway
{
    IAsyncEnumerable<(string Key, string Value)> ListAsync(
        string endpoint, string label, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps <c>Azure.Data.AppConfiguration.ConfigurationClient</c> authenticated via
/// <see cref="DefaultAzureCredential"/> -- which resolves a workload/managed
/// identity when running in Azure Container Apps, and falls back to developer
/// credentials (Azure CLI, VS Code, etc.) locally. This is what lets the runtime
/// host read the owner App Configuration runtime index without an interactive
/// <c>az login</c>, unlike the CLI's own <c>AzureCliAppConfigurationClient</c>
/// (which is fine to require <c>az</c>, since it always runs on an operator's
/// authenticated workstation).
/// </summary>
internal sealed class AzureConfigurationSettingsGateway : IConfigurationSettingsGateway
{
    public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
        string endpoint,
        string label,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = new ConfigurationClient(new Uri(endpoint), new DefaultAzureCredential());
        var selector = new SettingSelector { LabelFilter = label };
        await foreach (var setting in client.GetConfigurationSettingsAsync(selector, cancellationToken))
        {
            yield return (setting.Key, setting.Value);
        }
    }
}

/// <summary>
/// Reads the owner App Configuration runtime index via the managed-identity-
/// capable Azure SDK, matching the shape <c>dsf new</c> (<c>Dsf.Cli</c>'s
/// <c>AzureCliAppConfigurationClient.PublishRuntimeIndexAsync</c>) publishes: entries
/// labeled with the product key, keyed by the exact env var names
/// <see cref="RuntimeSettingsComposer"/> reads. This is the runtime host's only
/// production implementation of <see cref="IOwnerRuntimeIndexReader"/>; tests use a
/// hand-written <see cref="IConfigurationSettingsGateway"/> double instead of
/// shelling out to a real Azure subscription.
/// </summary>
internal sealed class AzureAppConfigurationOwnerRuntimeIndexReader(IConfigurationSettingsGateway gateway)
    : IOwnerRuntimeIndexReader
{
    public AzureAppConfigurationOwnerRuntimeIndexReader()
        : this(new AzureConfigurationSettingsGateway())
    {
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string ownerAppConfigEndpoint,
        string product,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await foreach (var (key, value) in gateway.ListAsync(ownerAppConfigEndpoint, product, cancellationToken))
            {
                values[key] = value;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"failed to read the owner App Configuration runtime index for product '{product}' at "
                + $"'{ownerAppConfigEndpoint}' via managed identity: {exception.Message}",
                exception);
        }

        if (values.Count == 0)
        {
            throw new InvalidOperationException(
                $"product '{product}' has no published runtime index in the owner App Configuration at "
                + $"'{ownerAppConfigEndpoint}'.");
        }

        return values;
    }
}
