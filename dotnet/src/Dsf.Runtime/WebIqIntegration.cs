using System.Net;
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
    [property: JsonPropertyName("webResults"), JsonRequired] IReadOnlyList<WebIqResult>? WebResults,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

internal sealed record WebIqApiError(
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("retryAfter")] string? RetryAfter,
    [property: JsonPropertyName("traceId")] string? TraceId);

/// <summary>
/// Runs one web search against WebIQ and hands back its typed results. The real
/// implementation (<see cref="WebIqSearchGateway"/>) calls the Microsoft WebIQ
/// SDK's REST endpoint directly, following the published <c>webiq</c> 0.1.8
/// SDK wire contract (ADR 0020); tests
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

    private readonly HttpClient httpClient = httpClient ?? new HttpClient();
    private readonly Uri searchEndpoint = new(
        (httpClient?.BaseAddress?.AbsoluteUri ?? DefaultBaseUrl).TrimEnd('/') + "/search/web");

    public async Task<IReadOnlyList<WebIqResult>> SearchAsync(
        string apiKey, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, searchEndpoint)
        {
            Content = JsonContent.Create(new { query, contentFormat = "text" }, options: SerializerOptions),
        };
        request.Headers.TryAddWithoutValidation("x-apikey", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"could not run a WebIQ web search for query '{query}': {exception.Message}", exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                WebIqApiError? error;
                try
                {
                    error = JsonSerializer.Deserialize<WebIqApiError>(body, SerializerOptions);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        $"WebIQ answered HTTP {(int)response.StatusCode} with an unreadable error response.", exception);
                }

                throw new InvalidOperationException($"WebIQ answered HTTP {(int)response.StatusCode}"
                    + $" (code: {error?.ErrorCode}, retryAfter: {error?.RetryAfter}, traceId: {error?.TraceId}).");
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

            if (parsed is null || !string.IsNullOrEmpty(parsed.ErrorCode))
            {
                throw new InvalidOperationException("WebIQ answered an error or null response, not web search results.");
            }

            // WebRequest/WebResponse have no continuation parameter or token in SDK 0.1.8.
            return parsed.WebResults ?? [];
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
/// result's title and content become the evidence summary, and its URL becomes
/// the reference.
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

        if (query.EnumerateRunes().Count() > 1000)
        {
            throw new RuntimeConfigurationException(
                $"{RuntimeIntegrationSettings.WebIqQuery} must contain at most 1000 characters.",
                [RuntimeIntegrationSettings.WebIqQuery]);
        }

        var apiKey = await ResolveApiKeyAsync(product, cancellationToken);
        var results = await gateway.SearchAsync(apiKey, query, cancellationToken);

        var evidence = new List<EvidenceItem>();
        foreach (var result in results)
        {
            if (result is null
                || !Uri.TryCreate(result.Url, UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
                || (string.IsNullOrWhiteSpace(result.Title) && string.IsNullOrWhiteSpace(result.Content)))
            {
                throw new InvalidOperationException("WebIQ returned a result without a source URL or usable content.");
            }

            evidence.Add(new EvidenceItem("webiq", url.AbsoluteUri,
                string.Join("\n", new[] { result.Title, result.Content }
                    .Where(text => !string.IsNullOrWhiteSpace(text)))));
        }

        return evidence;
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
        if (secretName.Length == 0)
        {
            secretName = RuntimeIntegrationSettings.DefaultWebIqApiKeySecret;
        }

        var missing = new List<string>();
        if (keyVaultUriValue.Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureKeyVaultUri);
        }

        if (missing.Count > 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'webiq' source agent for product '{product}' has no API key configured: set "
                + $"{RuntimeIntegrationSettings.WebIqApiKey} directly, or {RuntimeSettingsComposer.AzureKeyVaultUri} "
                + $"to read {RuntimeIntegrationSettings.WebIqApiKeySecret} "
                + $"(default '{RuntimeIntegrationSettings.DefaultWebIqApiKeySecret}') from Key Vault.",
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
        catch (Azure.RequestFailedException exception)
        {
            throw new InvalidOperationException(
                $"could not read the WebIQ API key from Key Vault secret '{secretName}' at "
                + $"'{keyVaultUriValue}': {exception.Message}", exception);
        }
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
