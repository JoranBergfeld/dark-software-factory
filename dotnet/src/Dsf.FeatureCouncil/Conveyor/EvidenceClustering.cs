namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// One cluster of evidence judged to describe the same underlying problem,
/// however many source kinds it spans. S3 synthesis turns each cluster returned
/// by <see cref="IEvidenceClusterer"/> into exactly one proposal.
/// </summary>
public sealed record EvidenceCluster(IReadOnlyList<EvidenceItem> Evidence)
{
    /// <summary>Every source kind that contributed evidence to this cluster, lower-case, de-duplicated, ordered.</summary>
    public IReadOnlyList<string> SourceKinds { get; } =
        Evidence
            .Select(item => item.SourceKind.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Groups gathered evidence into clusters that may span more than one source
/// kind: replaces the "one proposal per source kind" grouping S3 synthesis used
/// to do, so evidence from different kinds describing the same underlying
/// problem lands in one cluster/proposal instead of two. Deciding whether a
/// cluster is worth building remains S5 council's job, untouched by clustering.
/// </summary>
public interface IEvidenceClusterer
{
    IReadOnlyList<EvidenceCluster> Cluster(IReadOnlyList<EvidenceItem> evidence);
}

/// <summary>
/// Clusters evidence by lexical similarity of its summary text -- Jaccard
/// similarity over lower-cased word tokens -- independent of source kind: two
/// items whose summaries overlap enough are judged to describe the same
/// underlying problem and land in the same cluster even when they come from
/// different kinds; anything below the threshold gets its own singleton
/// cluster. Deterministic and reproducible from the evidence alone -- no model
/// call -- so S3's clustering step stays auditable independent of the model
/// client it separately consults for prose synthesis.
/// </summary>
public sealed class LexicalSimilarityEvidenceClusterer(double similarityThreshold = 0.34) : IEvidenceClusterer
{
    private static readonly char[] TokenSeparators = [' ', '\t', '\n', '\r', '.', ',', '!', '?', ':', ';', '"', '\''];

    public IReadOnlyList<EvidenceCluster> Cluster(IReadOnlyList<EvidenceItem> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var clusters = new List<List<EvidenceItem>>();
        var tokensByItem = new Dictionary<EvidenceItem, HashSet<string>>();
        foreach (var item in evidence)
        {
            var tokens = Tokenize(item.Summary);
            tokensByItem[item] = tokens;

            var match = clusters.FirstOrDefault(
                cluster => cluster.Any(existing => Similarity(tokens, tokensByItem[existing]) >= similarityThreshold));
            if (match is not null)
            {
                match.Add(item);
            }
            else
            {
                clusters.Add([item]);
            }
        }

        return clusters.Select(cluster => new EvidenceCluster(cluster)).ToArray();
    }

    private static HashSet<string> Tokenize(string text) =>
        text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    private static double Similarity(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0d;
        }

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0d : (double)intersection / union;
    }
}
