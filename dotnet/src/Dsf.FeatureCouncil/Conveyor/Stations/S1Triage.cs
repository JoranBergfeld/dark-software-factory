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
        run.Fingerprint = RunIdentity.Compute(run.Trigger, run.ProductHints, run.SourceKinds);
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
}
