namespace Dsf.Runtime;

/// <summary>
/// Resolves the specific <see cref="ISourceIntegration"/> a served source agent
/// uses to gather its kind's evidence: a kind with a typed adapter registered
/// (e.g. a dedicated Azure Monitor/FoundryIQ/WebIQ integration) uses it
/// exclusively; every other, unregistered kind falls back to the generic
/// <see cref="HttpSourceIntegration"/>, so a kind with no typed adapter yet still
/// works exactly as it did before any adapter existed.
/// </summary>
public sealed class SourceIntegrationRegistry(
    IReadOnlyDictionary<string, ISourceIntegration> byKind, ISourceIntegration fallback)
{
    public ISourceIntegration Resolve(string kind) =>
        byKind.TryGetValue(Normalize(kind), out var integration) ? integration : fallback;

    private static string Normalize(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();
}
