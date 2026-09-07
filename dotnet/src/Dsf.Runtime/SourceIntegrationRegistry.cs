using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// Resolves the specific <see cref="ISourceIntegration"/> a served source agent
/// uses to gather its kind's evidence: a kind with a typed adapter registered
/// (e.g. a dedicated Azure Monitor/FoundryIQ/WebIQ integration) uses it
/// exclusively; every other, unregistered kind fails loudly so source-agent
/// support cannot appear by accident through a generic HTTP seam.
/// </summary>
public sealed class SourceIntegrationRegistry(
    IReadOnlyDictionary<string, ISourceIntegration> byKind)
{
    public ISourceIntegration Resolve(string kind) =>
        byKind.TryGetValue(Normalize(kind), out var integration)
            ? integration
            : throw new RuntimeConfigurationException(
                $"source agent kind '{kind}' has no registered source agent kind adapter "
                + $"(registered: {RegisteredKinds()}).",
                []);

    private string RegisteredKinds() => byKind.Count == 0
        ? "(none)"
        : string.Join(", ", byKind.Keys.Order(StringComparer.Ordinal));

    private static string Normalize(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();
}
