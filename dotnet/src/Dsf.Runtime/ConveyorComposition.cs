using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Builds the conveyor's collaborators for a product. Composition is the boundary
/// where an incomplete factory is caught: a runtime with no source agents to
/// gather from, nothing to file through, or nowhere to persist what it decided is
/// reported by the settings that are unset, before any run can finish looking
/// successful while having done none of those things.
/// </summary>
public interface IConveyorComposer
{
    ConveyorServices ComposeFor(RuntimeSettings settings);
}

/// <summary>
/// The production composer: reads the runtime's environment and settings and wires
/// the real adapters -- an A2A gatherer per configured source agent endpoint, the
/// GitHub REST issue filer, and the Cosmos-backed run store. Anything unset raises
/// <see cref="RuntimeConfigurationException"/> naming every missing setting at
/// once.
/// </summary>
internal sealed class EnvironmentConveyorComposer(
    IReadOnlyDictionary<string, string?> env,
    HttpClient? httpClient = null,
    ICosmosDocumentGateway? cosmosGateway = null) : IConveyorComposer
{
    private const string KindPlaceholder = "{kind}";

    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public ConveyorServices ComposeFor(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var missing = new List<string>();
        var gatherers = ComposeGatherers(missing);
        var filer = ComposeFiler(settings, missing);
        var runStore = ComposeRunStore(settings, missing);

        if (missing.Count > 0)
        {
            throw new RuntimeConfigurationException(
                $"the runtime for product '{settings.Product}' cannot be composed: "
                + string.Join("; ", Explain(settings)),
                missing);
        }

        return new ConveyorServices(settings.Product, gatherers, filer, runStore!);
    }

    /// <summary>One gatherer per source kind that has an agent endpoint configured.</summary>
    private IReadOnlyList<IEvidenceGatherer> ComposeGatherers(List<string> missing)
    {
        var template = Read(RuntimeIntegrationSettings.SourceAgentEndpointTemplate);
        var gatherers = new List<IEvidenceGatherer>();
        foreach (var kind in SourceAgentKinds.Known)
        {
            var endpoint = Read(RuntimeIntegrationSettings.SourceAgentEndpoint(kind));
            if (endpoint.Length == 0 && template.Length > 0)
            {
                endpoint = template.Replace(KindPlaceholder, kind, StringComparison.OrdinalIgnoreCase);
            }

            if (endpoint.Length > 0)
            {
                gatherers.Add(new SourceAgentEvidenceGatherer(kind, new Uri(EnsureTrailingSlash(endpoint)), httpClient));
            }
        }

        if (gatherers.Count == 0)
        {
            missing.Add(RuntimeIntegrationSettings.SourceAgentEndpointTemplate);
            missing.AddRange(SourceAgentKinds.Known.Select(RuntimeIntegrationSettings.SourceAgentEndpoint));
        }

        return gatherers;
    }

    private IIssueFiler? ComposeFiler(RuntimeSettings settings, List<string> missing)
    {
        var token = Read(RuntimeIntegrationSettings.GitHubToken);
        if (token.Length == 0)
        {
            token = Read(RuntimeIntegrationSettings.GitHubTokenAlternative);
        }

        if (token.Length == 0)
        {
            missing.Add(RuntimeIntegrationSettings.GitHubToken);
        }

        if (settings.GitHubRepository.Trim().Length == 0)
        {
            missing.Add(RuntimeIntegrationSettings.GitHubRepository);
        }

        return token.Length > 0 && settings.GitHubRepository.Trim().Length > 0
            ? GitHubIssueFiler.Create(Read(RuntimeIntegrationSettings.GitHubApiUrl), token, settings.GitHubRepository)
            : null;
    }

    private IRunStore? ComposeRunStore(RuntimeSettings settings, List<string> missing)
    {
        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureCosmosEndpoint);
            return null;
        }

        var database = Read(RuntimeIntegrationSettings.CosmosDatabase);
        var container = Read(RuntimeIntegrationSettings.CosmosContainer);
        return new CosmosRunStore(
            settings.CosmosEndpoint.Trim(),
            database.Length > 0 ? database : RuntimeIntegrationSettings.DefaultCosmosDatabase,
            container.Length > 0 ? container : RuntimeIntegrationSettings.DefaultCosmosContainer,
            settings.Product,
            cosmosGateway ?? new AzureCosmosDocumentGateway());
    }

    /// <summary>Human-readable reasons, one per unmet requirement.</summary>
    private IEnumerable<string> Explain(RuntimeSettings settings)
    {
        var template = Read(RuntimeIntegrationSettings.SourceAgentEndpointTemplate);
        if (template.Length == 0
            && SourceAgentKinds.Known.All(kind => Read(RuntimeIntegrationSettings.SourceAgentEndpoint(kind)).Length == 0))
        {
            yield return "no source agent endpoint is configured (set "
                + $"{RuntimeIntegrationSettings.SourceAgentEndpointTemplate} to a base URL containing "
                + $"'{KindPlaceholder}', or a per-kind endpoint such as "
                + $"{RuntimeIntegrationSettings.SourceAgentEndpoint("sentry")})";
        }

        if (Read(RuntimeIntegrationSettings.GitHubToken).Length == 0
            && Read(RuntimeIntegrationSettings.GitHubTokenAlternative).Length == 0)
        {
            yield return $"no GitHub token is configured (set {RuntimeIntegrationSettings.GitHubToken})";
        }

        if (settings.GitHubRepository.Trim().Length == 0)
        {
            yield return "no repository is configured to file into (set "
                + $"{RuntimeIntegrationSettings.GitHubRepository})";
        }

        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            yield return "no blackboard persistence is configured (set "
                + $"{RuntimeSettingsComposer.AzureCosmosEndpoint})";
        }
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
