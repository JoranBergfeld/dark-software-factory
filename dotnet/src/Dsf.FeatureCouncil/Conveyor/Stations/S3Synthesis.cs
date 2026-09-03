namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S3 — synthesis. Turns gathered evidence into candidate proposals, one per
/// source kind that produced evidence, carrying that kind's evidence references
/// forward so grounding can check them. For each proposal, the station also asks
/// the model client to reason over that kind's evidence and records what it
/// answered; a failed model call fails this station exactly like any other
/// station-local error, which the conveyor line turns into an audited
/// <see cref="RunStatus.Error"/> rather than a synthesis that silently skipped
/// reasoning over its evidence.
/// </summary>
public sealed class S3Synthesis : IStation
{
    public const string StationName = "s3_synthesis";

    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        foreach (var group in run.Evidence.GroupBy(item => item.SourceKind, StringComparer.OrdinalIgnoreCase))
        {
            var references = group.Select(item => item.Reference).Distinct(StringComparer.Ordinal).ToArray();
            var kind = group.Key.ToLowerInvariant();
            var proposal = new Proposal(
                id: $"{run.Id}-{kind}",
                title: $"[{kind}] {group.First().Summary}",
                sourceKind: kind,
                evidenceReferences: references)
            {
                // Scope fingerprint, not run id: the same conclusion reached again
                // must resolve to the same filing intent.
                IntentKey = $"{run.Fingerprint}:{kind}",
            };
            run.Proposals.Add(proposal);

            var synthesis = await services.ModelClient.CompleteAsync(
                SynthesisPrompt(kind, group.Select(item => item.Summary)), cancellationToken);
            run.Record(StationName, $"model synthesis for '{proposal.Id}': {synthesis}");
        }

        run.Record(
            StationName,
            $"synthesized {run.Proposals.Count} proposal(s) from {run.Evidence.Count} evidence item(s).");
    }

    private static string SynthesisPrompt(string kind, IEnumerable<string> summaries) =>
        $"Synthesize a concise feature/bug proposal from '{kind}' evidence: " + string.Join("; ", summaries);
}
