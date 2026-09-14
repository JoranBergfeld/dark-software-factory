using System.Text.Json;
using Dsf.Core.Products;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed record ProductLocation(string Key, string GitHubRepository, string AppConfigEndpoint);

internal interface IAppConfigurationClient
{
    Task<IReadOnlyList<ProductLocation>> ListProductsAsync(
        string ownerEndpoint,
        CancellationToken cancellationToken);

    Task<ProductLocation> ResolveProductAsync(
        string ownerEndpoint,
        string product,
        CancellationToken cancellationToken);

    Task<ProductRecord> ReadProductRecordAsync(
        string productEndpoint,
        string product,
        CancellationToken cancellationToken);

    Task SeedProductRecordAsync(
        string productEndpoint,
        ProductRecord record,
        CancellationToken cancellationToken);

    Task SeedSourceAgentRosterAsync(
        string productEndpoint,
        string product,
        IReadOnlyList<string> enabledKinds,
        CancellationToken cancellationToken);

    Task PublishRuntimeIndexAsync(
        string ownerEndpoint,
        string product,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken);
}

/// <summary>Uses the authenticated Azure CLI to access the live App Configuration authority.</summary>
internal sealed class AzureCliAppConfigurationClient(IAzureCliRunner runner)
    : IAppConfigurationClient, IOwnerRuntimeIndexReader
{
    public async Task<IReadOnlyList<ProductLocation>> ListProductsAsync(
        string ownerEndpoint,
        CancellationToken cancellationToken)
    {
        RequireEndpoint(ownerEndpoint, "DSF_OWNER_APPCONFIG_ENDPOINT");
        var values = await ListEntriesAsync(ownerEndpoint, [], cancellationToken);
        return values
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Label))
            .GroupBy(entry => entry.Label!, StringComparer.Ordinal)
            .Select(group =>
            {
                var index = group.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
                var repository = index.GetValueOrDefault(ProductConfigurationKeys.OwnerIndexGitHubRepository);
                var endpoint = index.GetValueOrDefault(ProductConfigurationKeys.OwnerIndexAppConfigEndpoint);
                if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException(
                        $"Product '{group.Key}' is incomplete in the owner App Configuration index at '{ownerEndpoint}'; "
                        + $"{ProductConfigurationKeys.OwnerIndexGitHubRepository} and "
                        + $"{ProductConfigurationKeys.OwnerIndexAppConfigEndpoint} are required.");
                }

                return new ProductLocation(group.Key, repository, endpoint);
            })
            .OrderBy(product => product.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ProductLocation> ResolveProductAsync(
        string ownerEndpoint,
        string product,
        CancellationToken cancellationToken)
    {
        var values = await ReadAsync(ownerEndpoint, product, cancellationToken);
        var repository = values.GetValueOrDefault(ProductConfigurationKeys.OwnerIndexGitHubRepository);
        var productEndpoint = values.GetValueOrDefault(ProductConfigurationKeys.OwnerIndexAppConfigEndpoint);
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(productEndpoint))
        {
            throw new InvalidOperationException(
                $"Product '{product}' is absent or incomplete in the owner App Configuration index at '{ownerEndpoint}'; "
                + $"{ProductConfigurationKeys.OwnerIndexGitHubRepository} and "
                + $"{ProductConfigurationKeys.OwnerIndexAppConfigEndpoint} are required.");
        }

        return new ProductLocation(product, repository, productEndpoint);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string ownerAppConfigEndpoint,
        string product,
        CancellationToken cancellationToken)
    {
        RequireEndpoint(ownerAppConfigEndpoint, RuntimeSettingsComposer.OwnerAppConfigEndpoint);
        var values = (await ListEntriesAsync(
            ownerAppConfigEndpoint, ["--label", product], cancellationToken))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (values.Count == 0)
        {
            throw new InvalidOperationException(
                $"Product '{product}' has no published runtime index in the owner App Configuration "
                + $"at '{ownerAppConfigEndpoint}'.");
        }

        return values;
    }

    public async Task<ProductRecord> ReadProductRecordAsync(
        string productEndpoint,
        string product,
        CancellationToken cancellationToken)
    {
        RequireEndpoint(productEndpoint, "AZURE_APPCONFIG_ENDPOINT");
        var values = (await ListEntriesAsync(productEndpoint, ["--key", "product.*", "--label", ProductConfigurationKeys.NoLabel], cancellationToken))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var threshold = (await ListEntriesAsync(
            productEndpoint,
            ["--key", ProductConfigurationKeys.Threshold(product), "--label", ProductConfigurationKeys.NoLabel],
            cancellationToken))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var repository = ReadJson<string>(values, ProductConfigurationKeys.GitHubRepository);
        if (string.IsNullOrWhiteSpace(repository) || !threshold.TryGetValue(ProductConfigurationKeys.Threshold(product), out var thresholdValue)
            || !double.TryParse(thresholdValue, System.Globalization.CultureInfo.InvariantCulture, out var confidence))
        {
            throw new InvalidOperationException(
                $"Product '{product}' has no complete record in App Configuration at '{productEndpoint}'.");
        }

        return new ProductRecord(
            product,
            repository,
            ReadJson<Dictionary<string, IReadOnlyList<string>>>(values, ProductConfigurationKeys.LabelTaxonomy) ?? [],
            ReadJson<string>(values, ProductConfigurationKeys.FoundryIqScope) ?? string.Empty,
            ReadJson<List<string>>(values, ProductConfigurationKeys.SentryProjects) ?? [],
            ReadJson<List<string>>(values, ProductConfigurationKeys.GrafanaDashboards) ?? [],
            ReadJson<string>(values, ProductConfigurationKeys.AzureMonitorScope) ?? string.Empty,
            confidence);
    }

    public async Task SeedProductRecordAsync(
        string productEndpoint,
        ProductRecord record,
        CancellationToken cancellationToken)
    {
        RequireEndpoint(productEndpoint, "AZURE_APPCONFIG_ENDPOINT");
        foreach (var (key, value) in ProductValues(record))
        {
            await SetAsync(productEndpoint, key, value, label: null, cancellationToken);
        }
    }

    public async Task PublishRuntimeIndexAsync(
        string ownerEndpoint,
        string product,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        RequireEndpoint(ownerEndpoint, "DSF_OWNER_APPCONFIG_ENDPOINT");
        foreach (var (key, value) in values)
        {
            await SetAsync(ownerEndpoint, key, value, product, cancellationToken);
        }
    }

    public async Task SeedSourceAgentRosterAsync(
        string productEndpoint,
        string product,
        IReadOnlyList<string> enabledKinds,
        CancellationToken cancellationToken)
    {
        RequireEndpoint(productEndpoint, "AZURE_APPCONFIG_ENDPOINT");
        foreach (var kind in SourceAgentKinds.Known)
        {
            await SetAsync(productEndpoint, ProductConfigurationKeys.AgentEnabled(kind),
                enabledKinds.Contains(kind, StringComparer.Ordinal) ? "true" : "false", product, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<AppConfigurationEntry>> ListEntriesAsync(
        string endpoint,
        IReadOnlyList<string> filters,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "appconfig", "kv", "list", "--endpoint", endpoint, "--auth-mode", "login" };
        arguments.AddRange(filters);
        arguments.AddRange(["-o", "json"]);
        var result = await RunAsync(arguments, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"App Configuration at '{endpoint}' returned an invalid key-value list.");
        }

        return document.RootElement.EnumerateArray()
            .Where(entry => entry.TryGetProperty("key", out _) && entry.TryGetProperty("value", out _))
            .Select(entry => new AppConfigurationEntry(
                entry.GetProperty("key").GetString() ?? string.Empty,
                entry.GetProperty("value").GetString() ?? string.Empty,
                entry.TryGetProperty("label", out var label) ? label.GetString() : null))
            .ToArray();
    }

    internal sealed record AppConfigurationEntry(string Key, string Value, string? Label);

    private Task SetAsync(
        string endpoint,
        string key,
        string value,
        string? label,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "appconfig", "kv", "set", "--endpoint", endpoint, "--auth-mode", "login", "--key", key, "--value", value,
        };
        if (label is not null)
        {
            arguments.AddRange(["--label", label]);
        }

        arguments.Add("--yes");
        return RunAsync(arguments, cancellationToken);
    }

    private async Task<AzureCliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    private static T? ReadJson<T>(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : default;

    private static IEnumerable<(string Key, string Value)> ProductValues(ProductRecord record)
    {
        yield return (ProductConfigurationKeys.GitHubRepository, JsonSerializer.Serialize(record.GitHubRepository));
        yield return (ProductConfigurationKeys.LabelTaxonomy, JsonSerializer.Serialize(record.LabelTaxonomy));
        yield return (ProductConfigurationKeys.FoundryIqScope, JsonSerializer.Serialize(record.FoundryIqScope));
        yield return (ProductConfigurationKeys.SentryProjects, JsonSerializer.Serialize(record.SentryProjects));
        yield return (ProductConfigurationKeys.GrafanaDashboards, JsonSerializer.Serialize(record.GrafanaDashboards));
        yield return (ProductConfigurationKeys.AzureMonitorScope, JsonSerializer.Serialize(record.AzureMonitorScope));
        yield return (ProductConfigurationKeys.Threshold(record.Key), record.ConfidenceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void RequireEndpoint(string endpoint, string setting)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"{setting} is required to access App Configuration.");
        }
    }
}
