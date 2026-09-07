using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

/// <summary>
/// The real, deterministic clustering algorithm S3 synthesis groups evidence
/// through: lexical (Jaccard) similarity over summary text, independent of
/// source kind.
/// </summary>
public sealed class LexicalSimilarityEvidenceClustererTests
{
    [Fact]
    public void Evidence_from_different_kinds_with_overlapping_summaries_lands_in_one_cluster()
    {
        var clusterer = new LexicalSimilarityEvidenceClusterer();
        var evidence = new[]
        {
            new EvidenceItem("azuremonitor", "AM-1", "checkout 500s spiked after release 4.2"),
            new EvidenceItem("foundryiq", "FIQ-1", "checkout 500s spiked, same release 4.2"),
        };

        var clusters = clusterer.Cluster(evidence);

        var cluster = Assert.Single(clusters);
        Assert.Equal(2, cluster.Evidence.Count);
        Assert.Equal(["azuremonitor", "foundryiq"], cluster.SourceKinds);
    }

    [Fact]
    public void Evidence_with_dissimilar_summaries_lands_in_separate_clusters_even_from_the_same_kind()
    {
        var clusterer = new LexicalSimilarityEvidenceClusterer();
        var evidence = new[]
        {
            new EvidenceItem("azuremonitor", "AM-1", "checkout 500s spiked after release 4.2"),
            new EvidenceItem("azuremonitor", "AM-2", "database failover latency doubled"),
        };

        var clusters = clusterer.Cluster(evidence);

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.Single(cluster.Evidence));
    }

    [Fact]
    public void A_cluster_reports_every_contributing_kind_lower_case_and_deduplicated()
    {
        var clusterer = new LexicalSimilarityEvidenceClusterer();
        var evidence = new[]
        {
            new EvidenceItem("AZUREMONITOR", "AM-1", "checkout 500s spiked after release 4.2"),
            new EvidenceItem("azuremonitor", "AM-2", "checkout 500s spiked again after release 4.2"),
        };

        var clusters = clusterer.Cluster(evidence);

        var cluster = Assert.Single(clusters);
        Assert.Equal(["azuremonitor"], cluster.SourceKinds);
    }

    [Fact]
    public void Empty_evidence_clusters_into_nothing()
    {
        var clusterer = new LexicalSimilarityEvidenceClusterer();

        var clusters = clusterer.Cluster([]);

        Assert.Empty(clusters);
    }
}
