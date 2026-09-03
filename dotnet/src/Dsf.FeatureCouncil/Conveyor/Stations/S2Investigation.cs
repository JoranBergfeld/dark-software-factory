namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S2 — investigation. Asks the evidence gatherer registered for each of the run's
/// source kinds for evidence. A kind with no registered gatherer is audited by
/// name (the A2A source agents ship in #144) instead of being silently skipped or
/// filled in with invented evidence.
/// </summary>
public sealed class S2Investigation : IStation
{
    public const string StationName = "s2_investigation";

    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        foreach (var kind in run.SourceKinds)
        {
            var gatherer = services.GathererFor(kind);
            if (gatherer is null)
            {
                run.Record(
                    StationName,
                    $"no evidence gatherer is wired for source kind '{kind}'; gathered nothing from it "
                    + "(source agents are tracked in #144).");
                continue;
            }

            var gathered = await gatherer.GatherAsync(run, cancellationToken);
            run.Evidence.AddRange(gathered);
            run.Record(StationName, $"gathered {gathered.Count} evidence item(s) from '{kind}'.");
        }

        run.Record(StationName, $"investigation complete: {run.Evidence.Count} evidence item(s).");
    }
}
