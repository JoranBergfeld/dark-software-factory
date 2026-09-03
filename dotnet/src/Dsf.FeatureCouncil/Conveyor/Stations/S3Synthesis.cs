namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S3 — synthesis. Turns gathered evidence into candidate proposals, one per
/// source kind that produced evidence, carrying that kind's evidence references
/// forward so grounding can check them. Before reasoning over each proposal, the
/// station consults <see cref="ConveyorServices.LearningStore"/> for any human
/// verdicts already recorded against that exact recurring intent
/// (<see cref="Proposal.IntentKey"/>) -- so a conclusion the council reaches
/// again is reasoned over with the benefit of what actually happened to it last
/// time, not blind to its own history. No prior lesson (or no learning store
/// wired at all) synthesizes exactly as it always has. The station also asks the
/// model client to reason over that kind's evidence and records what it
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

            IReadOnlyList<LearningRecord> lessons = [];
            if (services.LearningStore is not null)
            {
                lessons = await services.LearningStore.RetrieveAsync(proposal.IntentKey, cancellationToken);
                if (lessons.Count > 0)
                {
                    run.Record(
                        StationName,
                        $"consulted {lessons.Count} prior recorded verdict(s) for '{proposal.Id}': "
                        + string.Join(", ", lessons.Select(lesson => lesson.Verdict)));
                }
            }

            var synthesis = await services.ModelClient.CompleteAsync(
                SynthesisPrompt(kind, group.Select(item => item.Summary), lessons), cancellationToken);
            run.Record(StationName, $"model synthesis for '{proposal.Id}': {synthesis}");
        }

        run.Record(
            StationName,
            $"synthesized {run.Proposals.Count} proposal(s) from {run.Evidence.Count} evidence item(s).");
    }

    private static string SynthesisPrompt(
        string kind, IEnumerable<string> summaries, IReadOnlyList<LearningRecord> lessons)
    {
        var prompt = $"Synthesize a concise feature/bug proposal from '{kind}' evidence: "
            + string.Join("; ", summaries);
        if (lessons.Count == 0)
        {
            return prompt;
        }

        var verdicts = string.Join(
            "; ", lessons.Select(lesson => $"{lesson.Verdict} ({lesson.IssueUrl})"));
        return prompt + $" Prior human verdict(s) on this exact recurring conclusion: {verdicts}.";
    }
}
