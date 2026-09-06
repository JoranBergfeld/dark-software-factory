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

    /// <summary>
    /// The Log Analytics workspace ID the typed Azure Monitor source integration
    /// queries (managed-identity authenticated; no ****** setting, unlike the
    /// generic HTTP fallback).
    /// </summary>
    public const string AzureMonitorWorkspaceId = "DSF_AZUREMONITOR_WORKSPACE_ID";

    /// <summary>
    /// The KQL query the typed Azure Monitor source integration runs against
    /// <see cref="AzureMonitorWorkspaceId"/> to read evidence rows.
    /// </summary>
    public const string AzureMonitorQuery = "DSF_AZUREMONITOR_QUERY";

    /// <summary>
    /// The Azure AI Foundry project endpoint the typed FoundryIQ source
    /// integration queries (e.g. <c>https://&lt;resource&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>),
    /// managed-identity authenticated.
    /// </summary>
    public const string FoundryIqProjectEndpoint = "DSF_FOUNDRYIQ_PROJECT_ENDPOINT";

    /// <summary>The FoundryIQ knowledge base the typed integration queries.</summary>
    public const string FoundryIqKnowledgeBase = "DSF_FOUNDRYIQ_KNOWLEDGE_BASE";

    /// <summary>The natural-language query the typed FoundryIQ integration runs against its knowledge base.</summary>
    public const string FoundryIqQuery = "DSF_FOUNDRYIQ_QUERY";

    /// <summary>
    /// The search query the typed WebIQ source integration runs against the
    /// Microsoft WebIQ web-search API (ADR 0020).
    /// </summary>
    public const string WebIqQuery = "DSF_WEBIQ_QUERY";

    /// <summary>
    /// The WebIQ API key, read directly (local/dev override) before falling
    /// back to <see cref="WebIqApiKeySecret"/> in Key Vault -- mirrors the
    /// Python runtime's <c>WEBIQ_API_KEY</c> env override (ADR 0020). Not
    /// <c>DSF_</c>-prefixed: this is the exact name the existing bicep/runtime
    /// convention already uses.
    /// </summary>
    public const string WebIqApiKey = "WEBIQ_API_KEY";

    /// <summary>
    /// The Key Vault secret name holding the WebIQ API key, read via <see
    /// cref="RuntimeSettingsComposer.AzureKeyVaultUri"/> and the runtime's
    /// managed identity when <see cref="WebIqApiKey"/> is not set directly
    /// (ADR 0020: seeded by <c>dsf new</c>'s <c>seed_webiq_key</c> step).
    /// </summary>
    public const string WebIqApiKeySecret = "WEBIQ_API_KEY_SECRET";

    /// <summary>The repository accepted proposals are filed into (<c>owner/name</c>).</summary>
    public const string GitHubRepository = "GITHUB_REPOSITORY";

    /// <summary>Overrides the GitHub REST API base URL (GitHub Enterprise).</summary>
    public const string GitHubApiUrl = "DSF_GITHUB_API_URL";

    /// <summary>
    /// Creation-phase autonomy dial for this product's factory (<c>low</c>,
    /// <c>medium</c>, or <c>high</c>), the same value <c>dsf new</c> captures as
    /// <c>InstanceDefinition.Product.CreationMaturity</c>. S5 council's jury
    /// verdict rules read this: at <c>low</c> maturity, every proposal the lens
    /// synthesizer would otherwise proceed with is escalated to a human instead,
    /// regardless of what the jury panel concludes. Unset resolves to <c>low</c>
    /// -- the safe default, since a factory a human has not yet dialed up
    /// autonomy for should never file without one in the loop.
    /// </summary>
    public const string CreationMaturity = "DSF_CREATION_MATURITY";

    /// <summary>
    /// Set to <c>true</c> at medium/high Operation maturity: the issue filer assigns the
    /// GitHub Coding Agent to every issue it files (SRE-Agent-to-Cloud-Agent
    /// auto-assignment), rather than leaving a freshly filed incident unassigned until a
    /// human notices. Unset or any other value preserves today's behavior: file only, assign
    /// nothing.
    /// </summary>
    public const string AssignCloudAgentToFiledIssues = "DSF_ASSIGN_CLOUD_AGENT";

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
