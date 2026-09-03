using Dsf.FeatureCouncil.Conveyor.Stations;

namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// Drives stations S1..S7 in order over a run, mirroring the Python
/// <c>orchestrator/conveyor.run_line</c>: the run is persisted through
/// <see cref="IRunStore"/> and a checkpoint recorded after each station so a
/// resumed run skips completed stations, a KILLED run stops the line early, and
/// any per-station exception -- including a failed persist -- becomes an audited
/// terminal <see cref="RunStatus.Error"/>, with the failing station and its reason
/// pinned on the run, rather than propagating to the caller.
/// Each run and station boundary is traced through <see cref="ITracer"/>: a
/// tracer send failure is recorded and swallowed rather than turned into a
/// station failure, since telemetry must never be the reason a run that actually
/// completed its work is reported as having failed.
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

        await TraceAsync(services, "run.start", run, station: null, cancellationToken);

        foreach (var station in Stations)
        {
            if (run.Checkpoints.Contains(station.Name, StringComparer.Ordinal))
            {
                continue;
            }

            await TraceAsync(services, "station.start", run, station.Name, cancellationToken);

            try
            {
                await station.RunAsync(run, services, cancellationToken);
                run.Checkpoints.Add(station.Name);
                // Persist before the next station starts: the checkpoint is only
                // real once the store has it, so a resumed run never skips a
                // station whose result was lost with the process.
                await services.RunStore.SaveAsync(run, station.Name, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                run.Checkpoints.Remove(station.Name);
                run.Status = RunStatus.Error;
                // The cause is pinned here, before tracing or persisting the
                // failure: either of those can fail too, and the operator needs
                // the station that actually broke, not the last thing that did.
                run.FailureReason =
                    $"station '{station.Name}' failed ({exception.GetType().Name}): {exception.Message}";
                run.Record(station.Name, $"station error ({exception.GetType().Name}): {exception.Message}");
                await TraceAsync(services, "station.error", run, station.Name, cancellationToken);
                await TryPersistAsync(run, station.Name, services, cancellationToken);
                await TraceAsync(services, "run.complete", run, station: null, cancellationToken);
                return run;
            }

            await TraceAsync(services, "station.complete", run, station.Name, cancellationToken);

            if (run.Status == RunStatus.Killed)
            {
                await TraceAsync(services, "run.complete", run, station: null, cancellationToken);
                return run;
            }
        }

        await TraceAsync(services, "run.complete", run, station: null, cancellationToken);
        return run;
    }

    /// <summary>
    /// Best-effort persist of a run that already failed. The failure being audited
    /// is the one worth reporting; a store that is itself unreachable must not
    /// replace it with a second, less informative error.
    /// </summary>
    private static async Task TryPersistAsync(
        ConveyorRun run, string station, ConveyorServices services, CancellationToken cancellationToken)
    {
        try
        {
            await services.RunStore.SaveAsync(run, station, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.Record(station, $"could not persist the failed run ({exception.GetType().Name}): {exception.Message}");
        }
    }

    /// <summary>
    /// Sends one telemetry event for a run or station boundary. A tracer send
    /// failure is audited on the run rather than propagated: the line's own
    /// success or failure must never hinge on the telemetry backend being
    /// reachable.
    /// </summary>
    private static async Task TraceAsync(
        ConveyorServices services, string name, ConveyorRun run, string? station, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string?>
        {
            ["product"] = services.Product,
            ["runId"] = run.Id,
            ["status"] = run.Status.ToString(),
            // Carried so an external tracer (Application Insights) can gate its
            // own emission: a dry run must have no external side effects.
            ["dryRun"] = run.DryRun.ToString(),
        };
        if (station is not null)
        {
            properties["station"] = station;
        }

        try
        {
            await services.Tracer.TraceAsync(name, properties, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.Record(station ?? name, $"could not trace '{name}' ({exception.GetType().Name}): {exception.Message}");
        }
    }
}
