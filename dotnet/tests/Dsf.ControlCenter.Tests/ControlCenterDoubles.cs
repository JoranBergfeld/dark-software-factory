using System.Runtime.CompilerServices;
using Dsf.ControlCenter;

namespace Dsf.ControlCenter.Tests;

/// <summary>
/// Deterministic in-test stand-in for a live App Configuration store: holds
/// entries in memory so the real
/// <see cref="AppConfigurationProductPolicyAuthority"/> key/label conventions can
/// be asserted without a subscription.
/// </summary>
internal sealed class ScriptedConfigurationStore : IConfigurationStoreGateway
{
    private readonly List<ConfigurationEntry> _entries = [];

    public List<(string Endpoint, string Key, string Value, string? Label)> Writes { get; } = [];

    public Exception? ListFailure { get; set; }

    public ScriptedConfigurationStore Seed(string endpoint, string key, string value, string? label = null)
    {
        _entries.RemoveAll(e => e.Endpoint == endpoint && e.Key == key && e.Label == label);
        _entries.Add(new ConfigurationEntry(endpoint, key, value, label));
        return this;
    }

    public async IAsyncEnumerable<(string Key, string Value, string? Label)> ListAsync(
        string endpoint,
        string label,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        foreach (var entry in _entries.Where(e => e.Endpoint == endpoint && Matches(e.Label, label)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (entry.Key, entry.Value, entry.Label);
        }
    }

    public Task SetAsync(string endpoint, string key, string value, string? label, CancellationToken cancellationToken)
    {
        Writes.Add((endpoint, key, value, label));
        Seed(endpoint, key, value, label);
        return Task.CompletedTask;
    }

    private static bool Matches(string? entryLabel, string filter) => filter switch
    {
        "*" => true,
        "\0" => entryLabel is null,
        _ => string.Equals(entryLabel, filter, StringComparison.Ordinal),
    };

    private sealed record ConfigurationEntry(string Endpoint, string Key, string Value, string? Label);
}

/// <summary>Records the writes the endpoints attempt, and serves scripted policy reads.</summary>
internal sealed class RecordingProductPolicyAuthority : IProductPolicyAuthority
{
    public List<ProductSummary> Products { get; } = [];

    public Dictionary<string, ProductPolicy> Policies { get; } = new(StringComparer.Ordinal);

    public List<(string Product, string Kind, bool Enabled)> AgentWrites { get; } = [];

    public List<(string Product, double Threshold)> ThresholdWrites { get; } = [];

    public Exception? ListFailure { get; set; }

    public Task<IReadOnlyList<ProductSummary>> ListProductsAsync(CancellationToken cancellationToken) =>
        ListFailure is not null
            ? Task.FromException<IReadOnlyList<ProductSummary>>(ListFailure)
            : Task.FromResult<IReadOnlyList<ProductSummary>>(Products);

    public Task<ProductPolicy> ReadPolicyAsync(string product, CancellationToken cancellationToken) =>
        Policies.TryGetValue(product, out var policy)
            ? Task.FromResult(policy)
            : throw new InvalidOperationException($"unknown product '{product}'");

    public Task SetAgentEnabledAsync(string product, string kind, bool enabled, CancellationToken cancellationToken)
    {
        AgentWrites.Add((product, kind, enabled));
        return Task.CompletedTask;
    }

    public Task SetConfidenceThresholdAsync(string product, double threshold, CancellationToken cancellationToken)
    {
        ThresholdWrites.Add((product, threshold));
        return Task.CompletedTask;
    }
}
