namespace Dsf.Core.Runtime;

/// <summary>
/// The source agent kinds the .NET runtime recognizes: exactly the Microsoft-native
/// kinds this product governs (<c>grafana</c>/<c>sentry</c>/<c>incidents</c> are
/// out of scope). Used to validate <c>serve-agent --kind</c> (and a signal's
/// <c>source_kinds</c>) against a real, known set instead of treating every kind
/// identically as unimplemented.
/// </summary>
public static class SourceAgentKinds
{
    public static readonly IReadOnlyList<string> Known = ["azuremonitor", "foundryiq", "webiq"];

    public static bool IsKnown(string kind) =>
        Known.Contains(kind.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}
