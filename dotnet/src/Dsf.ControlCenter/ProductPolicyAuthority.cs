using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Azure.Identity;
using Dsf.Core.Runtime;

namespace Dsf.ControlCenter;

/// <summary>One product as published in the owner App Configuration index.</summary>
internal sealed record ProductSummary(string Key, string GitHubRepository, string AppConfigEndpoint);

/// <summary>A product's effective, governable policy as the runtime reads it.</summary>
internal sealed record ProductPolicy(
    string Product,
    string GitHubRepository,
    IReadOnlyDictionary<string, bool> AgentEnablement,
    double ConfidenceThreshold);

internal sealed class ProductNotFoundException(string message) : InvalidOperationException(message);

internal sealed class ConfigurationAuthorityUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// The product/config authority the Control Center governs through: the owner
/// App Configuration index lists the products, and each product's own store
/// carries the policy the runtime reads.
/// </summary>
internal interface IProductPolicyAuthority
{
    Task<IReadOnlyList<ProductSummary>> ListProductsAsync(CancellationToken cancellationToken);

    Task<ProductPolicy> ReadPolicyAsync(string product, CancellationToken cancellationToken);

    Task SetAgentEnabledAsync(string product, string kind, bool enabled, CancellationToken cancellationToken);

    Task SetConfidenceThresholdAsync(string product, double threshold, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes App Configuration key/values, labeled or unlabeled. The real
/// implementation wraps the Azure SDK; tests substitute a hand-written double so
/// the key/label conventions can be asserted without a live subscription.
/// </summary>
internal interface IConfigurationStoreGateway
{
    IAsyncEnumerable<(string Key, string Value, string? Label)> ListAsync(
        string endpoint,
        string label,
        CancellationToken cancellationToken);

    Task SetAsync(string endpoint, string key, string value, string? label, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps <c>Azure.Data.AppConfiguration.ConfigurationClient</c> authenticated via
/// <see cref="DefaultAzureCredential"/>, matching how the runtime host reads the
/// same stores: a managed identity in Azure Container Apps, developer credentials
/// locally, and no interactive <c>az login</c> in either case.
/// </summary>
internal sealed class AzureConfigurationStoreGateway : IConfigurationStoreGateway
{
    public async IAsyncEnumerable<(string Key, string Value, string? Label)> ListAsync(
        string endpoint,
        string label,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = Client(endpoint);
        var selector = new SettingSelector { LabelFilter = label };
        await foreach (var setting in client.GetConfigurationSettingsAsync(selector, cancellationToken))
        {
            yield return (setting.Key, setting.Value, setting.Label);
        }
    }

    public async Task SetAsync(
        string endpoint,
        string key,
        string value,
        string? label,
        CancellationToken cancellationToken)
    {
        await Client(endpoint).SetConfigurationSettingAsync(
            new ConfigurationSetting(key, value, label),
            onlyIfUnchanged: false,
            cancellationToken);
    }

    private static ConfigurationClient Client(string endpoint) =>
        new(new Uri(endpoint), new DefaultAzureCredential());
}

/// <summary>
/// Governs products through App Configuration using the exact conventions the
/// rest of the factory already relies on: the owner store holds one label per
/// product (<c>GITHUB_REPOSITORY</c> + <c>AZURE_APPCONFIG_ENDPOINT</c>, written by
/// <c>dsf new</c>), the product store holds <c>agents.&lt;kind&gt;.enabled</c>
/// (unlabeled defaults, product-labeled overrides -- what the runtime's sweep
/// roster reads) and the product record's <c>threshold.&lt;product&gt;</c>.
/// </summary>
internal sealed class AppConfigurationProductPolicyAuthority(
    IConfigurationStoreGateway gateway,
    string ownerEndpoint) : IProductPolicyAuthority
{
    /// <summary>App Configuration's "no label" filter token.</summary>
    private const string NoLabel = "\0";

    /// <summary>App Configuration's "any label" filter token.</summary>
    private const string AnyLabel = "*";

    private const string RepositoryKey = "GITHUB_REPOSITORY";
    private const string EndpointKey = "AZURE_APPCONFIG_ENDPOINT";
    private const string AgentKeyPrefix = "agents.";
    private const string AgentKeySuffix = ".enabled";

    /// <summary>
    /// The confidence bar a product falls back to when its record carries no
    /// threshold, matching the runtime's own documented default.
    /// </summary>
    public const double DefaultConfidenceThreshold = 0.6d;

    public async Task<IReadOnlyList<ProductSummary>> ListProductsAsync(CancellationToken cancellationToken)
    {
        var entries = await ReadAsync(ownerEndpoint, AnyLabel, cancellationToken);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Label))
            .GroupBy(entry => entry.Label!, StringComparer.Ordinal)
            .Select(group => Summarize(group.Key, group.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)))
            .OrderBy(product => product.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ProductPolicy> ReadPolicyAsync(string product, CancellationToken cancellationToken)
    {
        var location = await ResolveAsync(product, cancellationToken);

        var enablement = SourceAgentKinds.Known.ToDictionary(kind => kind, _ => false, StringComparer.Ordinal);
        var threshold = DefaultConfidenceThreshold;
        var thresholdKey = ThresholdKey(product);

        // Unlabeled defaults first, then the product's labeled overrides on top --
        // the same precedence the runtime applies when it reads the same keys.
        foreach (var label in new[] { NoLabel, product })
        {
            foreach (var (key, value, _) in await ReadAsync(location.AppConfigEndpoint, label, cancellationToken))
            {
                if (TryReadAgentKind(key, out var kind) && enablement.ContainsKey(kind))
                {
                    enablement[kind] = IsTrue(value);
                }
                else if (string.Equals(key, thresholdKey, StringComparison.Ordinal)
                    && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && double.IsFinite(parsed))
                {
                    threshold = parsed;
                }
            }
        }

        return new ProductPolicy(product, location.GitHubRepository, enablement, threshold);
    }

    public async Task SetAgentEnabledAsync(
        string product,
        string kind,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!SourceAgentKinds.IsKnown(normalized))
        {
            throw new ArgumentException(
                $"'{kind}' is not a known source agent kind; expected one of {string.Join(", ", SourceAgentKinds.Known)}.",
                nameof(kind));
        }

        var location = await ResolveAsync(product, cancellationToken);
        await WriteAsync(
            location.AppConfigEndpoint,
            $"{AgentKeyPrefix}{normalized}{AgentKeySuffix}",
            enabled ? "true" : "false",
            label: product,
            cancellationToken);
    }

    public async Task SetConfidenceThresholdAsync(
        string product,
        double threshold,
        CancellationToken cancellationToken)
    {
        if (!PolicyValidation.TryValidateConfidenceThreshold(threshold, out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, error);
        }

        var location = await ResolveAsync(product, cancellationToken);
        await WriteAsync(
            location.AppConfigEndpoint,
            ThresholdKey(product),
            threshold.ToString(CultureInfo.InvariantCulture),
            label: null,
            cancellationToken);
    }

    private async Task<ProductSummary> ResolveAsync(string product, CancellationToken cancellationToken)
    {
        var entries = await ReadAsync(ownerEndpoint, product, cancellationToken);
        if (entries.Count == 0)
        {
            throw new ProductNotFoundException(
                $"product '{product}' is absent from the owner App Configuration index at '{ownerEndpoint}'.");
        }

        return Summarize(product, entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
    }

    private ProductSummary Summarize(string product, IReadOnlyDictionary<string, string> index)
    {
        var repository = index.GetValueOrDefault(RepositoryKey);
        var endpoint = index.GetValueOrDefault(EndpointKey);
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ConfigurationAuthorityUnavailableException(
                $"product '{product}' is incomplete in the owner App Configuration index at '{ownerEndpoint}'; "
                + $"{RepositoryKey} and {EndpointKey} are required.");
        }

        return new ProductSummary(product, repository, endpoint);
    }

    private async Task<IReadOnlyList<(string Key, string Value, string? Label)>> ReadAsync(
        string endpoint,
        string label,
        CancellationToken cancellationToken)
    {
        var entries = new List<(string Key, string Value, string? Label)>();
        try
        {
            await foreach (var entry in gateway.ListAsync(endpoint, label, cancellationToken))
            {
                entries.Add(entry);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ConfigurationAuthorityUnavailableException(
                $"failed to read App Configuration at '{endpoint}': {exception.Message}",
                exception);
        }

        return entries;
    }

    private async Task WriteAsync(
        string endpoint,
        string key,
        string value,
        string? label,
        CancellationToken cancellationToken)
    {
        try
        {
            await gateway.SetAsync(endpoint, key, value, label, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ConfigurationAuthorityUnavailableException(
                $"failed to write App Configuration at '{endpoint}': {exception.Message}",
                exception);
        }
    }

    private static string ThresholdKey(string product) => $"threshold.{product}";

    private static bool TryReadAgentKind(string key, out string kind)
    {
        kind = string.Empty;
        if (!key.StartsWith(AgentKeyPrefix, StringComparison.Ordinal)
            || !key.EndsWith(AgentKeySuffix, StringComparison.Ordinal))
        {
            return false;
        }

        kind = key[AgentKeyPrefix.Length..^AgentKeySuffix.Length].Trim().ToLowerInvariant();
        return kind.Length > 0;
    }

    private static bool IsTrue(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<bool?>(value) ?? false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
