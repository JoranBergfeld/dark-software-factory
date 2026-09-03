namespace Dsf.Core.Products;

/// <summary>Product-scoped configuration stored authoritatively in App Configuration.</summary>
public sealed record ProductRecord(
    string Key,
    string GitHubRepository,
    IReadOnlyDictionary<string, IReadOnlyList<string>> LabelTaxonomy,
    string FoundryIqScope,
    IReadOnlyList<string> SentryProjects,
    IReadOnlyList<string> GrafanaDashboards,
    string AzureMonitorScope,
    double ConfidenceThreshold);
