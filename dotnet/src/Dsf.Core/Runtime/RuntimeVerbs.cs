using System.Text.Json;

namespace Dsf.Core.Runtime;

/// <summary>
/// The minimal real per-verb operation each runtime verb performs once its
/// settings validate, ahead of the full conveyor pipeline (#142) and source-agent
/// contracts (#144). Every verb here does real, input-dependent work -- parses
/// <c>--signal</c>, validates <c>--kind</c> against the known source agent kinds --
/// and only reports a pending-pipeline condition once that work succeeds, never
/// unconditionally. This is the runtime host's real operation seam, not a stub:
/// each failure names a genuine, inspectable reason the pipeline can't finish yet.
/// </summary>
public static class RuntimeVerbs
{
    /// <summary>
    /// Parses and validates <paramref name="signalPath"/>, then reports the run
    /// as pending the conveyor station pipeline (#142). Throws
    /// <see cref="RuntimeVerbException"/> for a missing <c>--signal</c> path, a
    /// missing file, invalid JSON, or (always, once parsing succeeds) the pending
    /// pipeline.
    /// </summary>
    public static Signal Run(string? signalPath, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(signalPath))
        {
            throw new RuntimeVerbException("--signal <path> is required for run.");
        }

        Signal signal;
        try
        {
            signal = SignalReader.ReadFromFile(signalPath, dryRun);
        }
        catch (Exception exception) when (exception is FileNotFoundException or JsonException)
        {
            throw new RuntimeVerbException(exception.Message);
        }

        throw new RuntimeVerbException(
            $"run parsed signal '{signalPath}' (product_hints=[{string.Join(", ", signal.ProductHints)}], "
            + $"source_kinds=[{string.Join(", ", signal.SourceKinds)}]) but the .NET conveyor station pipeline "
            + "is not wired yet (tracked in #142).");
    }

    /// <summary>Reports a sweep as pending source-agent runners (#144).</summary>
    public static void Sweep(string product) =>
        throw new RuntimeVerbException(
            $"sweep validated runtime settings for product '{product}', but no source agent runners are wired "
            + "in the .NET runtime yet (tracked in #144).");

    /// <summary>Reports the orchestrator worker as pending source-agent runners (#144).</summary>
    public static void ServeOrchestrator(string product) =>
        throw new RuntimeVerbException(
            $"serve-orchestrator validated runtime settings for product '{product}', but no source agent "
            + "runners are wired in the .NET runtime yet (tracked in #144).");

    /// <summary>
    /// Validates <paramref name="kind"/> against <see cref="SourceAgentKinds"/>.
    /// Throws <see cref="RuntimeVerbException"/> immediately for an unrecognized
    /// kind, or (for a recognized kind) reports it as pending the .NET agent host
    /// (#144).
    /// </summary>
    public static void ServeAgent(string kind)
    {
        var normalized = kind.Trim().ToLowerInvariant();
        if (!SourceAgentKinds.IsKnown(normalized))
        {
            throw new RuntimeVerbException(
                $"unknown source agent kind '{kind}' (choices: {string.Join(", ", SourceAgentKinds.Known)}).");
        }

        throw new RuntimeVerbException(
            $"source agent kind '{normalized}' is recognized but the .NET agent host is not wired yet "
            + "(tracked in #144).");
    }
}
