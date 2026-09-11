namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>Resolves a durable problem key independently of changing observations and source references.</summary>
public interface IProblemIdentityResolver
{
    Task<string> ResolveAsync(string scope, EvidenceCluster cluster, CancellationToken cancellationToken);
}
