using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;

namespace Dsf.Runtime;

/// <summary>
/// Polls GitHub for issues a human has labelled with a canonical outcome
/// (<see cref="OutcomeLabels"/>) and reports the intent key each one carries, so
/// the learning loop can record what verdict the council's proposal actually
/// received. A single search query ORs every outcome label together (GitHub's
/// search API supports OR across labels within one <c>label:</c> qualifier), so
/// one poll finds every outcome-labelled issue in the repository regardless of
/// which verdict it carries.
/// </summary>
internal sealed partial class GitHubOutcomePoller : IOutcomeSource
{
    private const string DefaultApiUrl = "https://api.github.com/";

    private readonly HttpClient httpClient;
    private readonly IGitHubAuthProvider authProvider;
    private readonly string repository;

    public GitHubOutcomePoller(HttpClient httpClient, IGitHubAuthProvider authProvider, string repository)
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

    /// <summary>Builds a poller against the configured API base URL, authenticated through <paramref name="authProvider"/>.</summary>
    public static GitHubOutcomePoller Create(string apiUrl, IGitHubAuthProvider authProvider, string repository) =>
        new(
            new HttpClient
            {
                BaseAddress = new Uri(EnsureTrailingSlash(
                    string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : apiUrl)),
            },
            authProvider,
            repository);

    public async Task<IReadOnlyList<OutcomeSignal>> PollAsync(CancellationToken cancellationToken)
    {
        var labelClause = string.Join(",", OutcomeLabels.All.Select(label => $"\"{label}\""));
        var query = Uri.EscapeDataString($"repo:{repository} is:issue label:{labelClause}");
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await authProvider.GetTokenAsync(cancellationToken));
        using var response = await httpClient.GetAsync($"search/issues?q={query}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub refused the outcome poll for '{repository}' ({(int)response.StatusCode}): {body}");
        }

        using var document = JsonDocument.Parse(body);
        var signals = new List<OutcomeSignal>();
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return signals;
        }

        foreach (var item in items.EnumerateArray())
        {
            var issueBody = item.TryGetProperty("body", out var bodyValue)
                ? bodyValue.GetString() ?? string.Empty
                : string.Empty;
            var intentKey = ExtractIntentKey(issueBody);
            var verdict = ExtractVerdict(item);
            if (intentKey is null || verdict is null)
            {
                continue;
            }

            var url = item.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() ?? string.Empty : string.Empty;
            var title = item.TryGetProperty("title", out var titleValue) ? titleValue.GetString() ?? string.Empty : string.Empty;
            signals.Add(new OutcomeSignal(intentKey, verdict, url, title));
        }

        return signals;
    }

    /// <summary>Extracts the intent key from the <c>&lt;!-- dsf-intent: ... --&gt;</c> marker <see cref="GitHubIssueFiler"/> stamps into a filed issue's body.</summary>
    private static string? ExtractIntentKey(string body)
    {
        var match = IntentMarkerPattern().Match(body);
        return match.Success ? match.Groups["key"].Value : null;
    }

    /// <summary>The first canonical outcome label present on the issue, or <c>null</c> if none is.</summary>
    private static string? ExtractVerdict(JsonElement item)
    {
        if (!item.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = labels.EnumerateArray()
            .Select(label => label.ValueKind == JsonValueKind.Object && label.TryGetProperty("name", out var name)
                ? name.GetString()
                : null)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        return OutcomeLabels.All.FirstOrDefault(names.Contains);
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";

    [GeneratedRegex(@"<!--\s*dsf-intent:\s*(?<key>.+?)\s*-->")]
    private static partial Regex IntentMarkerPattern();
}
