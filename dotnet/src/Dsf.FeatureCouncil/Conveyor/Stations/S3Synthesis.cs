namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S3 — synthesis. Turns gathered evidence into candidate proposals by
/// clustering it first: <see cref="ConveyorServices.EvidenceClusterer"/> groups
/// evidence into clusters that may span more than one source kind when it
/// describes the same underlying problem, and each cluster becomes exactly one
/// proposal, carrying every contributing kind's evidence references forward so
/// grounding can check them. Before reasoning over each proposal, the station
/// consults <see cref="ConveyorServices.LearningStore"/> for any human verdicts
/// already recorded against that exact recurring intent
/// (<see cref="Proposal.IntentKey"/>) -- so a conclusion the council reaches
/// again is reasoned over with the benefit of what actually happened to it last
/// time, not blind to its own history. No prior lesson (or no learning store
/// wired at all) synthesizes exactly as it always has. The station also asks the
/// model client to reason over the cluster's evidence and records what it
/// answered; a failed model call fails this station exactly like any other
/// station-local error, which the conveyor line turns into an audited
/// <see cref="RunStatus.Error"/> rather than a synthesis that silently skipped
/// reasoning over its evidence. S3 still never decides worth-building -- that
/// remains S5's job, untouched by clustering.
/// </summary>
public sealed class S3Synthesis : IStation
{
    public const string StationName = "s3_synthesis";

    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        var clusters = services.EvidenceClusterer.Cluster(run.Evidence);
        ValidateClusters(run.Evidence, clusters);
        var proposals = new List<Proposal>();
        var problemKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < clusters.Count; index++)
        {
            var cluster = clusters[index];
            var resolver = services.ProblemIdentityResolver
                ?? throw new InvalidOperationException("S3 requires a configured problem identity resolver for nonempty clusters");
            var problemKey = await resolver.ResolveAsync(run.Fingerprint, cluster, cancellationToken);
            if (string.IsNullOrWhiteSpace(problemKey))
            {
                throw new InvalidOperationException("S3 problem identity resolver returned a blank problem key");
            }
            if (!problemKeys.Add(problemKey))
            {
                throw new InvalidOperationException($"S3 problem identity resolver returned duplicate problem key '{problemKey}'");
            }
            var references = cluster.Evidence
                .Select(item => item.Reference)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var kindLabel = string.Join("+", cluster.SourceKinds);
            var proposal = new Proposal(
                id: $"{run.Id}-{index}-{kindLabel}",
                title: $"[{kindLabel}] {cluster.Evidence[0].Summary}",
                sourceKinds: cluster.SourceKinds,
                evidenceReferences: references)
            {
                IntentKey = $"{run.Fingerprint}:{problemKey}",
                ClusterEvidence = cluster.Evidence.Distinct().ToArray(),
            };
            proposals.Add(proposal);

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
                SynthesisPrompt(kindLabel, cluster.Evidence.Select(item => item.Summary), lessons), cancellationToken);
            if (string.IsNullOrWhiteSpace(synthesis))
            {
                throw new InvalidOperationException($"S3 returned an incomplete synthesis for '{proposal.Id}'");
            }
            run.Record(StationName, $"model synthesis for '{proposal.Id}': {synthesis}");
        }

        run.Proposals.Clear();
        run.Proposals.AddRange(proposals);
        run.Record(
            StationName,
            $"synthesized {run.Proposals.Count} proposal(s) from {run.Evidence.Count} evidence item(s).");
    }

    private static void ValidateClusters(IReadOnlyList<EvidenceItem> evidence, IReadOnlyList<EvidenceCluster> clusters)
    {
        if (evidence.Any(item => item is null || string.IsNullOrWhiteSpace(item.SourceKind)
                || string.IsNullOrWhiteSpace(item.Reference) || string.IsNullOrWhiteSpace(item.Summary)))
        {
            throw new InvalidOperationException("S3 evidence requires a source kind, reference and summary");
        }

        var expected = evidence.ToHashSet();
        var claimed = new HashSet<EvidenceItem>();
        foreach (var cluster in clusters)
        {
            if (cluster is null || cluster.Evidence.Count == 0)
            {
                throw new InvalidOperationException("S3 received an empty evidence cluster");
            }

            foreach (var item in cluster.Evidence.Distinct())
            {
                if (!expected.Contains(item) || !claimed.Add(item))
                {
                    throw new InvalidOperationException("S3 clusters contain invented evidence or assign evidence to multiple proposals");
                }
            }
        }

        if (!claimed.SetEquals(expected))
        {
            throw new InvalidOperationException("S3 clusters omit gathered evidence references");
        }
    }

    private static string SynthesisPrompt(
        string kindLabel, IEnumerable<string> summaries, IReadOnlyList<LearningRecord> lessons)
    {
        var prompt = $"Synthesize a concise feature/bug proposal from '{kindLabel}' evidence: "
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
