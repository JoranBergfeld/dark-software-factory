using System.Globalization;
using Dsf.Core.Products;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;

namespace Dsf.Runtime;

/// <summary>
/// Reads the council's governed acceptance bar from the product's own App
/// Configuration store (<c>AZURE_APPCONFIG_ENDPOINT</c>), using the exact key
/// convention Control Center writes through
/// <c>AppConfigurationProductPolicyAuthority.SetConfidenceThresholdAsync</c>:
/// an unlabelled <c>threshold.&lt;product&gt;</c> entry. A store with no such
/// entry, or one that fails to parse as a finite number, falls back to
/// <see cref="S5Council.DefaultThreshold"/> -- the same fallback Control Center
/// itself reports as the "effective" value for a product whose record carries no
/// threshold. Authenticates via the same managed-identity-capable gateway the
/// source agent roster reader uses, so a Container App needs no interactive
/// login.
/// </summary>
internal sealed class AzureAppConfigurationConfidenceThresholdReader(
    IConfigurationSettingsGateway gateway,
    RuntimeSettings settings) : IConfidenceThresholdReader
{
    /// <summary>App Configuration's "no label" filter token.</summary>
    private const string NoLabel = "\0";

    public AzureAppConfigurationConfidenceThresholdReader(RuntimeSettings settings)
        : this(new AzureConfigurationSettingsGateway(), settings)
    {
    }

    public async Task<double> ReadThresholdAsync(CancellationToken cancellationToken)
    {
        var key = ThresholdKey(settings.Product);
        IReadOnlyList<(string Key, string Value)> entries;
        try
        {
            var read = new List<(string Key, string Value)>();
            await foreach (var entry in gateway.ListAsync(settings.AppConfigEndpoint, NoLabel, cancellationToken))
            {
                read.Add(entry);
            }

            entries = read;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RuntimeConfigurationException(
                $"failed to read the confidence threshold for product '{settings.Product}' from the App "
                + $"Configuration store at '{settings.AppConfigEndpoint}': {exception.Message}",
                [RuntimeSettingsComposer.AzureAppConfigEndpoint]);
        }

        foreach (var (entryKey, value) in entries)
        {
            if (string.Equals(entryKey, key, StringComparison.Ordinal)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && ConfidenceThresholdRange.Contains(parsed))
            {
                return parsed;
            }
        }

        return S5Council.DefaultThreshold;
    }

    private static string ThresholdKey(string product) => $"threshold.{product}";
}
