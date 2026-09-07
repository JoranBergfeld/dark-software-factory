using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// Reads the enabled source agent roster from the product's own App Configuration
/// store (<c>AZURE_APPCONFIG_ENDPOINT</c>), using the key convention the Python
/// config store already writes and reads: <c>agents.&lt;KIND&gt;.enabled</c> with a
/// JSON boolean value, where an entry labelled with the product overrides the
/// unlabelled default. Authenticates via the same managed-identity-capable
/// gateway the owner runtime index reader uses, so a Container App needs no
/// interactive login.
/// </summary>
internal sealed class AzureAppConfigurationSourceAgentRosterReader(IConfigurationSettingsGateway gateway)
    : ISourceAgentRosterReader
{
    public AzureAppConfigurationSourceAgentRosterReader()
        : this(new AzureConfigurationSettingsGateway())
    {
    }

    public async Task<IReadOnlyList<string>> ReadEnabledKindsAsync(
        RuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var enabled = new Dictionary<string, (bool Value, string Key)>(StringComparer.Ordinal);
        // Unlabelled defaults first, then the product's labelled overrides on top.
        foreach (var label in new[] { ProductConfigurationKeys.NoLabel, settings.Product })
        {
            foreach (var (key, value) in await ReadAsync(settings, label, cancellationToken))
            {
                if (ProductConfigurationKeys.TryReadAgentKind(key, out var kind))
                {
                    enabled[kind] = (ProductConfigurationKeys.IsJsonTrue(value), key);
                }
            }
        }

        foreach (var (kind, value) in enabled.Where(entry => entry.Value.Value))
        {
            if (!SourceAgentKinds.IsKnown(kind))
            {
                throw new RuntimeConfigurationException(
                    $"source agent roster for product '{settings.Product}' enables unknown kind "
                    + $"'{kind}' via key '{value.Key}'; registered source agent kinds are "
                    + $"{string.Join(", ", SourceAgentKinds.Known)}.",
                    [RuntimeSettingsComposer.AzureAppConfigEndpoint]);
            }
        }

        return enabled.Where(entry => entry.Value.Value)
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Materializes one label's settings. The gateway is an async iterator, so a
    /// connectivity or authorization failure surfaces during enumeration -- the
    /// enumeration is therefore what is guarded, and the failure is re-reported as
    /// a loud configuration error naming the store it could not read.
    /// </summary>
    private async Task<IReadOnlyList<(string Key, string Value)>> ReadAsync(
        RuntimeSettings settings, string label, CancellationToken cancellationToken)
    {
        var settingsRead = new List<(string Key, string Value)>();
        try
        {
            await foreach (var entry in gateway.ListAsync(settings.AppConfigEndpoint, label, cancellationToken))
            {
                settingsRead.Add(entry);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RuntimeConfigurationException(
                $"failed to read the source agent roster for product '{settings.Product}' from the App "
                + $"Configuration store at '{settings.AppConfigEndpoint}': {exception.Message}",
                [RuntimeSettingsComposer.AzureAppConfigEndpoint]);
        }

        return settingsRead;
    }

}
