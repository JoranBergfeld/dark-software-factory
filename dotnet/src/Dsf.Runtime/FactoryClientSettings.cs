using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal static partial class RuntimeWorkflow
{
    internal static async Task<RuntimeSettings?> ComposeFactoryClientAsync(IReadOnlyDictionary<string, string?> env,
        string? productOverride, RuntimeDependencies dependencies, CancellationToken token)
    {
        var endpoint = env.GetValueOrDefault(RuntimeSettingsComposer.AzureAppConfigEndpoint)?.Trim();
        var pinSupplied = env.ContainsKey(RuntimeIntegrationSettings.FactoryProfilePin);
        var nativeEndpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host.Length == 49
            && uri.Host.StartsWith("dsfo-", StringComparison.Ordinal) && uri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal)
            && Guid.TryParseExact(uri.Host.AsSpan(5, 32), "N", out _);
        if (!pinSupplied && !nativeEndpoint) return null;
        var product = (productOverride ?? env.GetValueOrDefault(RuntimeSettingsComposer.DsfProduct))?.Trim();
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(endpoint))
            throw new RuntimeConfigurationException("Onboarding client requires DSF_PRODUCT and AZURE_APPCONFIG_ENDPOINT; no legacy settings fallback.",
                [RuntimeSettingsComposer.DsfProduct, RuntimeSettingsComposer.AzureAppConfigEndpoint]);
        try
        {
            endpoint = FactoryGovernance.NormalizeEndpoint(endpoint);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var (key, value) in dependencies.ConfigurationSettingsGateway.ListAsync(endpoint,
                ProductConfigurationKeys.NoLabel, token))
                if (!values.TryAdd(key, value) || values.Count > FactoryAudit.MaximumRecords) throw new JsonException();
            var pin = env.GetValueOrDefault(RuntimeIntegrationSettings.FactoryProfilePin);
            var manifest = FactoryGovernance.ReadInactiveManifest(values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile), product, endpoint, pin);
            if (pinSupplied && pin != manifest.ProfilePin) throw new JsonException();
            // Client routing has no local model, Cosmos or GitHub credentials. Native admission is separate.
            return new RuntimeSettings(product, endpoint, "", "", "", "", "", "", "", "", "", "")
            {
                IntegrationSettings = new Dictionary<string, string?>
                { [RuntimeIntegrationSettings.FactoryProfilePin] = manifest.ProfilePin }
            };
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw new RuntimeConfigurationException("Runtime admission denied: required original onboarding manifest/profile is unreadable or invalid; no legacy settings fallback.",
                [RuntimeSettingsComposer.AzureAppConfigEndpoint, RuntimeIntegrationSettings.FactoryProfilePin]);
        }
    }
}
