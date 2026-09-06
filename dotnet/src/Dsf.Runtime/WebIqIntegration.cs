using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;

namespace Dsf.Runtime;

/// <summary>One web result the WebIQ search endpoint answered.</summary>
internal sealed record WebIqResult(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("content")] string? Content);

/// <summary>The shape of a WebIQ <c>/search/web</c> response.</summary>
internal sealed record WebIqSearchResponse(
    [property: JsonPropertyName("webResults")] IReadOnlyList<WebIqResult>? WebResults);

/// <summary>
/// Runs one web search against WebIQ and hands back its typed results. The real
/// implementation (<see cref="WebIqSearchGateway"/>) calls the Microsoft WebIQ
/// SDK's REST endpoint directly (ADR 0020: no stable .NET SDK exists yet, so
/// this follows the same typed-REST-adapter approach as FoundryIQ); tests
/// substitute a scripted double instead of a live call.
/// </summary>
internal interface IWebIqSearchGateway
{
    Task<IReadOnlyList<WebIqResult>> SearchAsync(string apiKey, string query, CancellationToken cancellationToken);
}

/// <summary>
/// Calls the Microsoft WebIQ web-search endpoint (<c>POST {baseUrl}/search/web</c>),
/// API-key authenticated via the <c>x-apikey</c> header -- the exact base URL,
/// path, and auth header the Python <c>webiq</c> SDK (ADR 0020) uses, so this
/// adapter reads the same real API a served <c>webiq</c> agent talks to.
/// </summary>
internal sealed class WebIqSearchGateway(HttpClient? httpClient = null) : IWebIqSearchGateway
{
    private const string DefaultBaseUrl = "https://api.microsoft.ai/v3";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri(DefaultBaseUrl) };

    public async Task<IReadOnlyList<WebIqResult>> SearchAsync(
        string apiKey, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "search/web")
        {
            Content = JsonContent.Create(new { query }, options: SerializerOptions),
        };
        request.Headers.TryAddWithoutValidation("x-apikey", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not run a WebIQ web search for query '{query}': {exception.Message}", exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"WebIQ answered {(int)response.StatusCode} for query '{query}': {body}");
            }

            WebIqSearchResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<WebIqSearchResponse>(body, SerializerOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"WebIQ answered unreadable JSON for query '{query}': {exception.Message}", exception);
            }

            return parsed?.WebResults ?? [];
        }
    }
}

/// <summary>
/// The typed <c>webiq</c> source integration: reads evidence from a real
/// Microsoft WebIQ web search (ADR 0020) rather than the generic
/// JSON-shape-guessing <see cref="HttpSourceIntegration"/> fallback. The API
/// key is resolved the same way the Python runtime resolves it: <see
/// cref="RuntimeIntegrationSettings.WebIqApiKey"/> as a local/dev override,
/// else the <see cref="RuntimeIntegrationSettings.WebIqApiKeySecret"/> secret
/// read from the product Key Vault via the runtime's managed identity. Each
/// result's title (falling back to its content) becomes the evidence summary,
/// and its URL becomes the reference.
/// </summary>
internal sealed class WebIqIntegration(
    IReadOnlyDictionary<string, string?> env,
    IWebIqSearchGateway? gateway = null,
    IPrivateKeySecretReader? secretReader = null)
    : ISourceIntegration
{
    private readonly IWebIqSearchGateway gateway = gateway ?? new WebIqSearchGateway();
    private readonly IPrivateKeySecretReader secretReader = secretReader ?? new AzureKeyVaultPrivateKeySecretReader();

    public async Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        string kind, string product, CancellationToken cancellationToken)
    {
        var query = Read(RuntimeIntegrationSettings.WebIqQuery);
        if (query.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'webiq' source agent for product '{product}' has no search query configured: set "
                + $"{RuntimeIntegrationSettings.WebIqQuery} to the query it runs against WebIQ.",
                [RuntimeIntegrationSettings.WebIqQuery]);
        }

        var apiKey = await ResolveApiKeyAsync(product, cancellationToken);
        var results = await gateway.SearchAsync(apiKey, query, cancellationToken);

        return results
            .Select(result => new EvidenceItem(
                "webiq", result.Url ?? string.Empty,
                string.IsNullOrWhiteSpace(result.Title) ? result.Content ?? string.Empty : result.Title))
            .Where(item => item.Reference.Length > 0)
            .ToArray();
    }

    private async Task<string> ResolveApiKeyAsync(string product, CancellationToken cancellationToken)
    {
        var directKey = Read(RuntimeIntegrationSettings.WebIqApiKey);
        if (directKey.Length > 0)
        {
            return directKey;
        }

        var keyVaultUriValue = Read(RuntimeSettingsComposer.AzureKeyVaultUri);
        var secretName = Read(RuntimeIntegrationSettings.WebIqApiKeySecret);
        var missing = new List<string>();
        if (keyVaultUriValue.Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureKeyVaultUri);
        }

        if (secretName.Length == 0)
        {
            missing.Add(RuntimeIntegrationSettings.WebIqApiKeySecret);
        }

        if (missing.Count > 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'webiq' source agent for product '{product}' has no API key configured: set "
                + $"{RuntimeIntegrationSettings.WebIqApiKey} directly, or both "
                + $"{RuntimeSettingsComposer.AzureKeyVaultUri} and {RuntimeIntegrationSettings.WebIqApiKeySecret} "
                + "to read it from Key Vault.",
                missing);
        }

        try
        {
            var vaultUri = new Uri(keyVaultUriValue);
            var secret = await secretReader.GetSecretAsync(vaultUri, secretName, cancellationToken);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    $"Key Vault secret '{secretName}' at '{keyVaultUriValue}' is empty.");
            }

            return secret.Trim();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RuntimeConfigurationException)
        {
            throw new InvalidOperationException(
                $"could not read the WebIQ API key from Key Vault secret '{secretName}' at "
                + $"'{keyVaultUriValue}': {exception.Message}", exception);
        }
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
