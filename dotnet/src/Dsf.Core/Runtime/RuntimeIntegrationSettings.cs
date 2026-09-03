namespace Dsf.Core.Runtime;

/// <summary>
/// The environment settings that wire the runtime's real collaborators: where the
/// A2A source agents are served, where each source agent's upstream integration
/// lives, how the filing station authenticates to GitHub, and which Cosmos
/// database and container hold the run blackboard. Every name is defined here so a
/// missing dependency can be reported against the exact setting an operator has to
/// set, from any layer that discovers the absence.
/// </summary>
public static class RuntimeIntegrationSettings
{
    /// <summary>
    /// A single base-URL template for every source agent, with <c>{kind}</c>
    /// substituted per kind (e.g. <c>https://dsf-acme-{kind}.internal</c>).
    /// </summary>
    public const string SourceAgentEndpointTemplate = "DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE";

    /// <summary>Per-kind source agent base URL, overriding the template.</summary>
    public static string SourceAgentEndpoint(string kind) =>
        $"DSF_SOURCE_AGENT_ENDPOINT_{Normalize(kind)}";

    /// <summary>The upstream system a served source agent reads its evidence from.</summary>
    public static string SourceIntegrationEndpoint(string kind) =>
        $"DSF_SOURCE_{Normalize(kind)}_ENDPOINT";

    /// <summary>Bearer token for the upstream system, when it requires one.</summary>
    public static string SourceIntegrationToken(string kind) =>
        $"DSF_SOURCE_{Normalize(kind)}_TOKEN";

    /// <summary>The repository accepted proposals are filed into (<c>owner/name</c>).</summary>
    public const string GitHubRepository = "GITHUB_REPOSITORY";

    /// <summary>Overrides the GitHub REST API base URL (GitHub Enterprise).</summary>
    public const string GitHubApiUrl = "DSF_GITHUB_API_URL";

    /// <summary>Cosmos database holding the run blackboard.</summary>
    public const string CosmosDatabase = "DSF_COSMOS_DATABASE";

    /// <summary>Cosmos container holding the run blackboard documents.</summary>
    public const string CosmosContainer = "DSF_COSMOS_CONTAINER";

    /// <summary>Database used when <see cref="CosmosDatabase"/> is not set.</summary>
    public const string DefaultCosmosDatabase = "dsf";

    /// <summary>Container used when <see cref="CosmosContainer"/> is not set.</summary>
    public const string DefaultCosmosContainer = "runs";

    private static string Normalize(string kind) =>
        (kind ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_');
}
