namespace Dsf.Core.Runtime;

/// <summary>
/// The source agent kinds the .NET runtime recognizes, mirroring the Python
/// <c>SourceKind</c> enum (<c>contracts/enums.py</c>) and <c>agents/registry.py</c>'s
/// <c>DEPLOYABLE_AGENTS</c> keys. Used to validate <c>serve-agent --kind</c> (and a
/// signal's <c>source_kinds</c>) against a real, known set instead of treating every
/// kind identically as unimplemented.
/// </summary>
public static class SourceAgentKinds
{
    public static readonly IReadOnlyList<string> Known =
        ["azuremonitor", "foundryiq", "grafana", "incidents", "sentry", "webiq"];

    public static bool IsKnown(string kind) =>
        Known.Contains(kind.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}
