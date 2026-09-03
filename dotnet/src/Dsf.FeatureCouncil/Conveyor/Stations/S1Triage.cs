using System.Security.Cryptography;
using System.Text;

namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S1 — triage. Computes the run's debounce/dedup fingerprint from its scope and
/// kills a run that has nothing to investigate (no product hints and no
/// recognized source kinds), so the rest of the line is never driven over an
/// empty scope.
/// </summary>
public sealed class S1Triage : IStation
{
    public const string StationName = "s1_triage";

    public string Name => StationName;

    public Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        run.Fingerprint = Fingerprint(run);
        if (run.ProductHints.Count == 0 && run.SourceKinds.Count == 0)
        {
            run.Status = RunStatus.Killed;
            run.Record(StationName, "killed: the signal scopes no product hints and no known source kinds.");
            return Task.CompletedTask;
        }

        run.Record(
            StationName,
            $"triaged fingerprint={run.Fingerprint} products=[{string.Join(", ", run.ProductHints)}] "
            + $"sources=[{string.Join(", ", run.SourceKinds)}]");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A stable content hash of the run's scope: two signals asking for the same
    /// products and sources produce the same fingerprint, which is what later
    /// dedup (#142) matches on.
    /// </summary>
    private static string Fingerprint(ConveyorRun run)
    {
        var scope = string.Join(
            "|",
            run.Trigger,
            string.Join(",", run.ProductHints.Select(h => h.ToLowerInvariant()).Order(StringComparer.Ordinal)),
            string.Join(",", run.SourceKinds.Select(k => k.ToLowerInvariant()).Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..16];
    }
}
