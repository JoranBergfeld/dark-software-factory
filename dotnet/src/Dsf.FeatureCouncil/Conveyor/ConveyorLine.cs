using Dsf.FeatureCouncil.Conveyor.Stations;

namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// Drives stations S1..S7 in order over a run, mirroring the Python
/// <c>orchestrator/conveyor.run_line</c>: a checkpoint is recorded after each
/// station so a resumed run skips completed stations, a KILLED run stops the line
/// early, and any per-station exception becomes an audited terminal
/// <see cref="RunStatus.Error"/> rather than propagating to the caller. Durable
/// blackboard persistence of that state ships with the memory adapter in #142;
/// the checkpoints and audit trail this records are the run's inspectable result
/// in the meantime.
/// </summary>
public static class ConveyorLine
{
    /// <summary>The ordered pipeline.</summary>
    public static IReadOnlyList<IStation> Stations { get; } =
    [
        new S1Triage(),
        new S2Investigation(),
        new S3Synthesis(),
        new S4Grounding(),
        new S5Council(),
        new S6Routing(),
        new S7Filing(),
    ];

    /// <summary>The station names, in pipeline order.</summary>
    public static IReadOnlyList<string> StationNames { get; } = Stations.Select(station => station.Name).ToArray();

    private static readonly RunStatus[] Terminal =
        [RunStatus.Killed, RunStatus.Previewed, RunStatus.Filed, RunStatus.Error];

    public static async Task<ConveyorRun> RunAsync(
        ConveyorRun run,
        ConveyorServices services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(services);

        if (Terminal.Contains(run.Status))
        {
            return run;
        }

        foreach (var station in Stations)
        {
            if (run.Checkpoints.Contains(station.Name, StringComparer.Ordinal))
            {
                continue;
            }

            try
            {
                await station.RunAsync(run, services, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                run.Status = RunStatus.Error;
                run.Record(station.Name, $"station error ({exception.GetType().Name}): {exception.Message}");
                return run;
            }

            run.Checkpoints.Add(station.Name);

            if (run.Status == RunStatus.Killed)
            {
                return run;
            }
        }

        return run;
    }
}
