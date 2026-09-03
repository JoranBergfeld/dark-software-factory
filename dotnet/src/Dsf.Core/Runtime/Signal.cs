namespace Dsf.Core.Runtime;

/// <summary>
/// A <c>run</c> verb's parsed <c>--signal</c> JSON file. Mirrors the shape the
/// Python runtime's <c>control.signal_to_run</c> normalizes a webhook/alert
/// payload into: product hints and source kinds pulled from the payload, unknown
/// source kinds dropped rather than rejected.
/// </summary>
public sealed record Signal(
    string Path,
    IReadOnlyList<string> ProductHints,
    IReadOnlyList<string> SourceKinds,
    bool DryRun);
