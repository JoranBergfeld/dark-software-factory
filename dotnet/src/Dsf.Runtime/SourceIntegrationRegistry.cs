using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// Resolves the specific <see cref="ISourceIntegration"/> a served source agent
/// uses to gather its kind's evidence: a kind with a typed adapter registered
/// (e.g. a dedicated Azure Monitor/FoundryIQ/WebIQ integration) uses it
/// exclusively. A caller may explicitly supply the generic HTTP fallback for
/// known kinds; production does not supply one or enable undeveloped adapters.
/// </summary>
public sealed class SourceIntegrationRegistry
{
    private readonly IReadOnlyDictionary<string, ISourceIntegration> byKind;
    private readonly ISourceIntegration? fallback;

    public SourceIntegrationRegistry(
        IReadOnlyDictionary<string, ISourceIntegration> byKind, ISourceIntegration? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(byKind);
        this.byKind = byKind.ToDictionary(pair => Normalize(pair.Key), pair => pair.Value, StringComparer.Ordinal);
        this.fallback = fallback;
        foreach (var (kind, integration) in this.byKind)
        {
            if (!SourceAgentKinds.IsKnown(kind) || integration is null)
            {
                throw new ArgumentException($"Invalid source adapter registration for '{kind}'.", nameof(byKind));
            }
        }
    }

    public ISourceIntegration Resolve(string kind)
    {
        var normalized = Normalize(kind);
        if (SourceAgentKinds.IsKnown(normalized))
        {
            if (byKind.TryGetValue(normalized, out var integration))
            {
                return integration;
            }

            if (fallback is not null)
            {
                return fallback;
            }
        }

        throw new RuntimeConfigurationException(
            $"source agent kind '{kind}' has no registered source agent kind adapter "
            + $"(registered: {RegisteredKinds()}).", []);
    }

    private string RegisteredKinds() => byKind.Count == 0
        ? "(none)"
        : string.Join(", ", byKind.Keys.Order(StringComparer.Ordinal));

    private static string Normalize(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();
}
