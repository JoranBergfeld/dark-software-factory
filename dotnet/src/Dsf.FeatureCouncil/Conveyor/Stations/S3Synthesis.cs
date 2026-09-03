namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S3 — synthesis. Turns gathered evidence into candidate proposals, one per
/// source kind that produced evidence, carrying that kind's evidence references
/// forward so grounding can check them.
/// </summary>
public sealed class S3Synthesis : IStation
{
    public const string StationName = "s3_synthesis";

    public string Name => StationName;

    public Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        foreach (var group in run.Evidence.GroupBy(item => item.SourceKind, StringComparer.OrdinalIgnoreCase))
        {
            var references = group.Select(item => item.Reference).Distinct(StringComparer.Ordinal).ToArray();
            var kind = group.Key.ToLowerInvariant();
            run.Proposals.Add(new Proposal(
                id: $"{run.Id}-{kind}",
                title: $"[{kind}] {group.First().Summary}",
                sourceKind: kind,
                evidenceReferences: references)
            {
                // Scope fingerprint, not run id: the same conclusion reached again
                // must resolve to the same filing intent.
                IntentKey = $"{run.Fingerprint}:{kind}",
            });
        }

        run.Record(
            StationName,
            $"synthesized {run.Proposals.Count} proposal(s) from {run.Evidence.Count} evidence item(s).");
        return Task.CompletedTask;
    }
}
