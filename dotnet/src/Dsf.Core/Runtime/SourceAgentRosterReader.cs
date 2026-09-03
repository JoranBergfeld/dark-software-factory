namespace Dsf.Core.Runtime;

/// <summary>
/// Resolves which source agents are currently enabled for a product, from the
/// product's own App Configuration store -- the same authority the Python runtime
/// reads through <c>dsf.config.flags.agent_enabled</c> (config key
/// <c>agents.&lt;KIND&gt;.enabled</c>, per-product overrides carried on the App
/// Configuration label). This is what makes <c>sweep</c> a real, configuration-
/// driven sweep: the roster it sweeps is discovered, never assumed.
/// </summary>
public interface ISourceAgentRosterReader
{
    /// <summary>
    /// Returns the enabled source agent kinds (lower-case, as in
    /// <see cref="SourceAgentKinds.Known"/>) for <paramref name="settings"/>'s
    /// product. An empty result means the store is reachable and no agent is
    /// enabled; an unreachable or unreadable store throws.
    /// </summary>
    Task<IReadOnlyList<string>> ReadEnabledKindsAsync(RuntimeSettings settings, CancellationToken cancellationToken);
}
