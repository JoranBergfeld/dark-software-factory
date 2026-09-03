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

    /// <summary>Cosmos container holding audited human-outcome learning records.</summary>
    public const string CosmosLearningContainer = "DSF_COSMOS_LEARNING_CONTAINER";

    /// <summary>Container used when <see cref="CosmosLearningContainer"/> is not set.</summary>
    public const string DefaultCosmosLearningContainer = "learning";

    /// <summary>
    /// The manual gate a live (non-<c>--dry-run</c>) outcome poll requires, in
    /// addition to <c>--live</c>: an operator must set this to <c>true</c> before
    /// the runtime will record real learning data against a live GitHub
    /// repository and Cosmos account. An accidental live invocation without this
    /// set fails loudly rather than recording anything.
    /// </summary>
    public const string ConfirmLiveOutcomes = "DSF_CONFIRM_LIVE_OUTCOMES";

    /// <summary>
    /// The manual gate a live (non-<c>--dry-run</c>) <c>run</c> or <c>sweep</c>
    /// requires before it may reach S7 filing for real: an operator must set this
    /// to <c>true</c> before the runtime will file real GitHub issues from a
    /// signal or scheduled sweep. An accidental live invocation without this set
    /// fails loudly before the line files anything, exactly like <see
    /// cref="ConfirmLiveOutcomes"/> gates a live outcome poll. A <c>--dry-run</c>
    /// invocation never needs this: it never reaches filing.
    /// </summary>
    public const string ConfirmLiveFiling = "DSF_CONFIRM_LIVE_FILING";

    private static string Normalize(string kind) =>
        (kind ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_');
}
