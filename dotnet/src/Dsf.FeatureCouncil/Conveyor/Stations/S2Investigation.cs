using Dsf.Core.Runtime;

namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S2 — investigation. Asks the evidence gatherer registered for each of the run's
/// source kinds for evidence. A kind the run was scoped to with no gatherer behind
/// it fails the run, naming the kind and the setting that would wire it: a line
/// that cannot look where it was told to look has nothing truthful to say, and an
/// empty investigation must never be mistaken for a clean one.
/// </summary>
public sealed class S2Investigation : IStation
{
    public const string StationName = "s2_investigation";

    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        foreach (var kind in run.SourceKinds)
        {
            var gatherer = services.GathererFor(kind)
                ?? throw new InvalidOperationException(
                    $"no evidence gatherer is configured for source kind '{kind}' (product "
                    + $"'{services.Product}'): set {RuntimeIntegrationSettings.SourceAgentEndpoint(kind)} "
                    + $"or {RuntimeIntegrationSettings.SourceAgentEndpointTemplate} to the source agent "
                    + "serving that kind.");

            var gathered = await gatherer.GatherAsync(run, cancellationToken);
            run.Evidence.AddRange(gathered);
            run.Record(StationName, $"gathered {gathered.Count} evidence item(s) from '{kind}'.");
        }

        run.Record(StationName, $"investigation complete: {run.Evidence.Count} evidence item(s).");
    }
}
