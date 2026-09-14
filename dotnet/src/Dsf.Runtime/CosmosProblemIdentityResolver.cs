using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>Assigns persistent problem identities using the conveyor's clustering policy, not observation hashes.</summary>
internal sealed class CosmosProblemIdentityResolver(
    string endpoint, string database, string container, string product, ICosmosDocumentGateway gateway,
    IEvidenceClusterer? clusterer = null) : IProblemIdentityResolver
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly IEvidenceClusterer clusterer = clusterer ?? new LexicalSimilarityEvidenceClusterer();

    public async Task<string> ResolveAsync(string scope, EvidenceCluster cluster, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(cluster);
        ValidateEvidence(cluster.Evidence);
        var id = "problem-index-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await gateway.ReadAsync(endpoint, database, container, product, id, cancellationToken);
            var index = json is null ? new ProblemIndex(id, product, scope, [])
                : JsonSerializer.Deserialize<ProblemIndex>(json, Options)
                    ?? throw new InvalidOperationException("stored problem identity index is null");
            if (index.Id != id || index.Product != product || index.Scope != scope || index.Problems is null
                || index.Problems.Any(problem => problem is null || string.IsNullOrWhiteSpace(problem.Key))
                || index.Problems.Select(problem => problem.Key).Distinct(StringComparer.Ordinal).Count() != index.Problems.Count)
            {
                throw new InvalidOperationException("stored problem identity index is incomplete or belongs to another scope");
            }

            var matches = new List<KnownProblem>();
            foreach (var problem in index.Problems)
            {
                ValidateEvidence(problem.Evidence);
                var combined = problem.Evidence.Concat(cluster.Evidence).Distinct().ToArray();
                if (this.clusterer.Cluster(combined).Any(group =>
                    group.Evidence.Intersect(problem.Evidence).Any() && group.Evidence.Intersect(cluster.Evidence).Any()))
                {
                    matches.Add(problem);
                }
            }
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    "evidence matches multiple persisted problems; identity is ambiguous and requires human consolidation");
            }
            var existing = matches.SingleOrDefault();
            var profile = existing?.Evidence ?? [];
            var learned = profile.Concat(cluster.Evidence)
                .DistinctBy(item => item.Summary.Trim(), StringComparer.OrdinalIgnoreCase).ToArray();
            if (existing is not null && learned.Length == profile.Count)
            {
                return existing.Key;
            }

            var key = existing?.Key ?? Guid.NewGuid().ToString("N");
            var problemProfile = new KnownProblem(key, learned);
            var updated = index with
            {
                Problems = existing is null ? [.. index.Problems, problemProfile]
                    : index.Problems.Select(problem => problem.Key == key ? problemProfile : problem).ToArray(),
                Etag = null,
            };
            var document = JsonSerializer.Serialize(updated, Options);
            var saved = json is null
                ? await gateway.CreateIfAbsentAsync(endpoint, database, container, product, id, document, cancellationToken)
                : await gateway.ReplaceIfMatchAsync(endpoint, database, container, product, id, document,
                    !string.IsNullOrWhiteSpace(index.Etag) ? index.Etag
                        : throw new InvalidOperationException("stored problem identity index has no concurrency version"),
                    cancellationToken);
            if (saved)
            {
                return key;
            }
        }
        throw new InvalidOperationException("could not persist a problem identity after repeated concurrent updates");
    }

    private static void ValidateEvidence(IReadOnlyList<EvidenceItem>? evidence)
    {
        if (evidence is null || evidence.Count == 0 || evidence.Any(item => item is null
            || string.IsNullOrWhiteSpace(item.SourceKind) || string.IsNullOrWhiteSpace(item.Reference)
            || string.IsNullOrWhiteSpace(item.Summary)))
        {
            throw new InvalidOperationException("problem identity requires complete evidence");
        }
    }

    private sealed record KnownProblem(
        [property: JsonRequired] string Key,
        [property: JsonRequired] IReadOnlyList<EvidenceItem> Evidence);

    private sealed record ProblemIndex(
        [property: JsonRequired] string Id,
        [property: JsonRequired] string Product,
        [property: JsonRequired] string Scope,
        [property: JsonRequired] IReadOnlyList<KnownProblem> Problems)
    {
        [JsonPropertyName("_etag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Etag { get; init; }
        [JsonPropertyName("ttl")] public int TimeToLive => -1;
    }
}
