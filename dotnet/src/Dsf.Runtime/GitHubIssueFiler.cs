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

    public GitHubIssueFiler(HttpClient httpClient, string token, string repository)
        : this(httpClient, new StaticGitHubAuthProvider(token), repository)
    {
    }

    public GitHubIssueFiler(HttpClient httpClient, IGitHubAuthProvider authProvider, string repository)
    {
        this.httpClient = httpClient;
        this.authProvider = authProvider;
        this.repository = repository.Trim();
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
    public static GitHubIssueFiler Create(string apiUrl, IGitHubAuthProvider authProvider, string repository) =>
        new(
            new HttpClient
            {
                BaseAddress = new Uri(EnsureTrailingSlash(
                    string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : apiUrl)),
            },
            authProvider,
            repository);

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
        return document.RootElement.TryGetProperty("html_url", out var url)
            ? url.GetString() ?? string.Empty
            : throw new InvalidOperationException(
                $"GitHub accepted proposal '{proposal.Id}' but answered no issue URL: {body}");
    }

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
