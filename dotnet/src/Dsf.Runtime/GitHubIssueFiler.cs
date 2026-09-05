using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;

namespace Dsf.Runtime;

/// <summary>
/// Files accepted proposals as GitHub issues over the REST API, authenticated
/// through an <see cref="IGitHubAuthProvider"/> -- production runs mint a GitHub
/// App installation token from the runtime's existing App settings, while a
/// documented local-dev override can supply a fixed token instead. Filing is
/// idempotent through the proposal's durable intent key: the key is stamped into
/// the issue body as an HTML comment and searched for before filing, so a scope
/// the council reaches the same conclusion about twice resolves to the issue that
/// already exists instead of filing a duplicate.
/// </summary>
internal sealed class GitHubIssueFiler : IIssueFiler
{
    private const string DefaultApiUrl = "https://api.github.com/";

    private readonly HttpClient httpClient;
    private readonly IGitHubAuthProvider authProvider;
    private readonly string repository;
    private readonly bool assignCloudAgent;

    public GitHubIssueFiler(HttpClient httpClient, string token, string repository)
        : this(httpClient, new StaticGitHubAuthProvider(token), repository)
    {
    }

    public GitHubIssueFiler(HttpClient httpClient, IGitHubAuthProvider authProvider, string repository)
        : this(httpClient, authProvider, repository, assignCloudAgent: false)
    {
    }

    public GitHubIssueFiler(
        HttpClient httpClient, IGitHubAuthProvider authProvider, string repository, bool assignCloudAgent)
    {
        this.httpClient = httpClient;
        this.authProvider = authProvider;
        this.repository = repository.Trim();
        this.assignCloudAgent = assignCloudAgent;
        this.httpClient.BaseAddress ??= new Uri(DefaultApiUrl);
        this.httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dsf-runtime");
        this.httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>Builds a filer against the configured API base URL, authenticated with a fixed token.</summary>
    public static GitHubIssueFiler Create(string apiUrl, string token, string repository) =>
        Create(apiUrl, new StaticGitHubAuthProvider(token), repository);

    /// <summary>Builds a filer against the configured API base URL, authenticated through <paramref name="authProvider"/>.</summary>
    public static GitHubIssueFiler Create(
        string apiUrl, IGitHubAuthProvider authProvider, string repository, bool assignCloudAgent = false) =>
        new(
            new HttpClient
            {
                BaseAddress = new Uri(EnsureTrailingSlash(
                    string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : apiUrl)),
            },
            authProvider,
            repository,
            assignCloudAgent);

    /// <summary>The marker an issue carries so its filing intent can be recognized.</summary>
    public static string IntentMarker(string intentKey) => $"<!-- dsf-intent: {intentKey} -->";

    public async Task<string> FileAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (string.IsNullOrWhiteSpace(proposal.IntentKey))
        {
            throw new InvalidOperationException(
                $"proposal '{proposal.Id}' carries no filing intent key; refusing to file an issue that a "
                + "later run could not recognize as already filed.");
        }

        var existing = await FindExistingAsync(proposal.IntentKey, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        await AuthorizeAsync(cancellationToken);
        using var response = await httpClient.PostAsJsonAsync(
            $"repos/{repository}/issues",
            new
            {
                title = proposal.Title,
                body = Body(proposal),
                labels = proposal.Labels,
            },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub refused to file proposal '{proposal.Id}' in '{repository}' "
                + $"({(int)response.StatusCode}): {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("html_url", out var url))
        {
            throw new InvalidOperationException(
                $"GitHub accepted proposal '{proposal.Id}' but answered no issue URL: {body}");
        }

        if (assignCloudAgent
            && document.RootElement.TryGetProperty("node_id", out var nodeId)
            && nodeId.GetString() is { Length: > 0 } issueNodeId)
        {
            // Best effort: an issue that fails to auto-assign is still filed and still
            // carries the handoff label, exactly like the existing "Copilot not enabled"
            // fallback the Council's own filing documents -- a human can assign it instead.
            await TryAssignCloudAgentAsync(issueNodeId, cancellationToken);
        }

        return url.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Assigns the GitHub Coding Agent to an already-filed issue via GitHub's GraphQL
    /// <c>suggestedActors</c>/<c>replaceActorsForAssignable</c> pair -- the mechanism GitHub's
    /// own "Assign to Copilot" UI action uses. Any failure (Copilot not enabled on the repo,
    /// no bot actor offered, a transient API error) is swallowed: filing already succeeded,
    /// and an unassigned issue still carries the handoff label a human can act on.
    ///
    /// Authenticates with the same GitHub App installation token the filer already used to
    /// create the issue, not the shared user-to-server credential from
    /// <c>GitHubSettings.CloudAgentCredentialSecretName</c>. That credential is documented as a
    /// GitHub Actions repository secret consumed inside a workflow run (the Creation-phase
    /// retry workflow) -- a repo secret's value is never readable from outside an Actions run,
    /// so this runtime process (an Azure Container App) has no path to it. Public precedent
    /// confirms <c>replaceActorsForAssignable</c> accepts an installation token, so this stays
    /// the one credential mechanism this seam actually has today. If a later ticket seeds the
    /// shared credential's value somewhere this runtime can read it (e.g. mirrored into Key
    /// Vault), swapping this call over is a contained follow-up, not a redesign.
    /// </summary>
    private async Task TryAssignCloudAgentAsync(string issueNodeId, CancellationToken cancellationToken)
    {
        try
        {
            await AuthorizeAsync(cancellationToken);
            using var actorsResponse = await httpClient.PostAsJsonAsync(
                "graphql",
                new
                {
                    query = """
                        query($id: ID!) {
                          node(id: $id) {
                            ... on Issue {
                              suggestedActors(capabilities: [CAN_BE_ASSIGNED], first: 10) {
                                nodes { login id }
                              }
                            }
                          }
                        }
                        """,
                    variables = new { id = issueNodeId },
                },
                cancellationToken);
            if (!actorsResponse.IsSuccessStatusCode)
            {
                return;
            }

            using var actorsDocument = JsonDocument.Parse(
                await actorsResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!actorsDocument.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("node", out var node)
                || node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty("suggestedActors", out var suggestedActors)
                || !suggestedActors.TryGetProperty("nodes", out var actorNodes)
                || actorNodes.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            string? cloudAgentActorId = null;
            foreach (var actor in actorNodes.EnumerateArray())
            {
                if (string.Equals(
                        actor.TryGetProperty("login", out var login) ? login.GetString() : null,
                        CloudAgentBotLogin,
                        StringComparison.OrdinalIgnoreCase)
                    && actor.TryGetProperty("id", out var actorId))
                {
                    cloudAgentActorId = actorId.GetString();
                    break;
                }
            }

            if (cloudAgentActorId is null)
            {
                return;
            }

            await AuthorizeAsync(cancellationToken);
            using var ignored = await httpClient.PostAsJsonAsync(
                "graphql",
                new
                {
                    query = """
                        mutation($assignableId: ID!, $actorIds: [ID!]!) {
                          replaceActorsForAssignable(input: { assignableId: $assignableId, actorIds: $actorIds }) {
                            clientMutationId
                          }
                        }
                        """,
                    variables = new { assignableId = issueNodeId, actorIds = new[] { cloudAgentActorId } },
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Assignment is enrichment on top of a filing that already succeeded -- never
            // fail the run over it.
        }
    }

    /// <summary>The bot login GitHub's GraphQL API offers for the Coding Agent.</summary>
    private const string CloudAgentBotLogin = "copilot-swe-agent";

    /// <summary>
    /// Resolves an intent key to the issue already filed for it, if any. A search
    /// the API refuses is reported rather than treated as "nothing found" -- that
    /// would file a duplicate on every transient failure.
    /// </summary>
    private async Task<string?> FindExistingAsync(string intentKey, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"repo:{repository} in:body \"{intentKey}\"");
        await AuthorizeAsync(cancellationToken);
        using var response = await httpClient.GetAsync($"search/issues?q={query}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub refused the duplicate search for intent '{intentKey}' in '{repository}' "
                + $"({(int)response.StatusCode}): {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var marker = IntentMarker(intentKey);
        foreach (var item in items.EnumerateArray())
        {
            var issueBody = item.TryGetProperty("body", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            if (issueBody.Contains(marker, StringComparison.Ordinal)
                && item.TryGetProperty("html_url", out var url))
            {
                return url.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Refreshes the client's bearer token before each call so a GitHub App
    /// installation token minted per <see cref="GitHubAppAuthProvider"/> is
    /// re-fetched (or served from its cache) on every request rather than fixed
    /// once at construction, where it could go stale mid-run.
    /// </summary>
    private async Task AuthorizeAsync(CancellationToken cancellationToken) =>
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await authProvider.GetTokenAsync(cancellationToken));

    private static string Body(Proposal proposal) =>
        string.Join(
            "\n",
            IntentMarker(proposal.IntentKey),
            "",
            $"Filed by the Dark Software Factory Feature Council from '{proposal.SourceKind}' evidence.",
            "",
            $"- council confidence: {proposal.Confidence:F2}",
            $"- evidence: {string.Join(", ", proposal.EvidenceReferences)}");

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}
