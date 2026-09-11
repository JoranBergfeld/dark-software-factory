using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

internal sealed record FoundryIqResult(string Id, string? Title, string? Content);

internal interface IFoundryIqKnowledgeGateway
{
    Task<IReadOnlyList<FoundryIqResult>> QueryAsync(
        string searchEndpoint, string knowledgeBase, string query, CancellationToken cancellationToken);
}

/// <summary>
/// Foundry IQ's Azure AI Search knowledge-base retrieve API, GA 2026-04-01.
/// Uses semantic intents and extractive grounding, not preview answer synthesis.
/// https://learn.microsoft.com/rest/api/searchservice/knowledge-retrieval/retrieve?view=rest-searchservice-2026-04-01
/// </summary>
internal sealed class FoundryIqKnowledgeGateway(TokenCredential? credential = null, HttpClient? httpClient = null)
    : IFoundryIqKnowledgeGateway
{
    private const string ApiVersion = "2026-04-01";
    private static readonly string[] Scopes = ["https://search.azure.com/.default"];
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly TokenCredential credential = credential ?? new DefaultAzureCredential();
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public async Task<IReadOnlyList<FoundryIqResult>> QueryAsync(
        string searchEndpoint, string knowledgeBase, string query, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var uri = new Uri(new Uri(searchEndpoint.TrimEnd('/') + "/"),
            $"knowledgebases('{Uri.EscapeDataString(knowledgeBase.Replace("'", "''", StringComparison.Ordinal))}')"
            + $"/retrieve?api-version={ApiVersion}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(
                new { intents = new[] { new { type = "semantic", search = query } }, includeActivity = true },
                options: SerializerOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"FoundryIQ knowledge base '{knowledgeBase}' answered HTTP {(int)response.StatusCode}; "
                + "partial or failed retrieval cannot be used as evidence.");
        }

        try
        {
            var parsed = await response.Content.ReadFromJsonAsync<RetrievalResponse>(SerializerOptions, cancellationToken);
            if (parsed?.Response is null || parsed.Activity is null || parsed.References is null
                || Present(parsed.Error))
            {
                throw new InvalidOperationException("FoundryIQ returned an incomplete retrieval response.");
            }

            if (parsed.Activity.Any(activity => activity is null || Present(activity.Error)))
            {
                throw new InvalidOperationException("FoundryIQ reported a failed knowledge-source retrieval activity.");
            }

            var references = new Dictionary<string, KnowledgeReference>(StringComparer.Ordinal);
            foreach (var reference in parsed.References)
            {
                if (reference is null || string.IsNullOrWhiteSpace(reference.Id)
                    || !references.TryAdd(reference.Id, reference))
                {
                    throw new InvalidOperationException("FoundryIQ returned missing or duplicate reference IDs.");
                }
            }

            var results = new List<FoundryIqResult>();
            foreach (var message in parsed.Response)
            {
                if (message?.Content is null)
                {
                    throw new InvalidOperationException("FoundryIQ returned a message without content.");
                }

                foreach (var content in message.Content)
                {
                    if (content?.Type != "text" || string.IsNullOrWhiteSpace(content.Text))
                    {
                        throw new InvalidOperationException("FoundryIQ returned content other than extractive text.");
                    }

                    using var chunks = JsonDocument.Parse(content.Text);
                    if (chunks.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException("FoundryIQ extractive text must contain a JSON array.");
                    }

                    foreach (var chunk in chunks.RootElement.EnumerateArray())
                    {
                        if (chunk.ValueKind != JsonValueKind.Object
                            || !chunk.TryGetProperty("ref_id", out var id)
                            || id.ValueKind != JsonValueKind.String
                            || !references.TryGetValue(id.GetString()!, out var reference)
                            || !chunk.EnumerateObject().Any(property => property.Name != "ref_id" && HasGrounding(property.Value)))
                        {
                            throw new InvalidOperationException("FoundryIQ returned grounding without a complete reference.");
                        }

                        // Semantic fields are index-defined. Preserve them, rather than guessing "title"/"content" columns.
                        var fields = chunk.EnumerateObject().Where(property => property.Name != "ref_id")
                            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                        results.Add(new FoundryIqResult(
                            ReferenceFor(reference, parsed.Activity, searchEndpoint, knowledgeBase),
                            null, JsonSerializer.Serialize(fields, SerializerOptions)));
                    }
                }
            }

            // Retrieve has a bounded extractive response, not a nextLink/continuation API.
            return results;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("FoundryIQ returned invalid retrieval JSON.", exception);
        }
    }

    private static string ReferenceFor(
        KnowledgeReference reference, IReadOnlyList<KnowledgeActivity> activities,
        string searchEndpoint, string knowledgeBase)
    {
        var activity = activities.SingleOrDefault(item => item.Id == reference.ActivitySource);
        if (activity is null || activity.Type != reference.Type || string.IsNullOrWhiteSpace(activity.KnowledgeSourceName))
        {
            throw new InvalidOperationException("FoundryIQ reference has no matching knowledge-source activity.");
        }

        if (reference.Type == "searchIndex")
        {
            if (string.IsNullOrWhiteSpace(reference.DocKey))
            {
                throw new InvalidOperationException("FoundryIQ search-index reference has no document key.");
            }

            // Reference IDs are response-local. Use source-scoped document identity for evidence deduplication.
            return $"foundryiq:{searchEndpoint.TrimEnd('/')}#{Uri.EscapeDataString(knowledgeBase)}/"
                + $"{Uri.EscapeDataString(activity.KnowledgeSourceName)}/{Uri.EscapeDataString(reference.DocKey)}";
        }

        var sourceUrl = reference.Type switch
        {
            "azureBlob" => reference.BlobUrl,
            "indexedOneLake" => reference.DocUrl,
            "web" => reference.Url,
            _ => null,
        };
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"FoundryIQ '{reference.Type}' reference has no supported source URL.");
        }

        return url.AbsoluteUri;
    }

    private static bool Present(JsonElement value) =>
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool HasGrounding(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.EnumerateArray().Any(HasGrounding),
        JsonValueKind.Object => value.EnumerateObject().Any(property => HasGrounding(property.Value)),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
        _ => false,
    };

    private sealed record RetrievalResponse(
        [property: JsonRequired] IReadOnlyList<KnowledgeMessage>? Response,
        [property: JsonRequired] IReadOnlyList<KnowledgeActivity>? Activity,
        [property: JsonRequired] IReadOnlyList<KnowledgeReference>? References,
        JsonElement Error);

    private sealed record KnowledgeMessage([property: JsonRequired] IReadOnlyList<KnowledgeContent>? Content);
    private sealed record KnowledgeContent(string? Type, string? Text);
    private sealed record KnowledgeActivity(
        [property: JsonRequired] int Id, string? Type, string? KnowledgeSourceName, JsonElement Error);
    private sealed record KnowledgeReference(
        string? Id, string? Type, [property: JsonRequired] int ActivitySource,
        string? DocKey, string? BlobUrl, string? DocUrl, string? Url);
}

internal sealed class FoundryIqIntegration(IReadOnlyDictionary<string, string?> env, IFoundryIqKnowledgeGateway? gateway = null)
    : ISourceIntegration
{
    private readonly IFoundryIqKnowledgeGateway gateway = gateway ?? new FoundryIqKnowledgeGateway();

    public async Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        string kind, string product, CancellationToken cancellationToken)
    {
        var searchEndpoint = Required(RuntimeIntegrationSettings.FoundryIqSearchEndpoint, product);
        var knowledgeBase = Required(RuntimeIntegrationSettings.FoundryIqKnowledgeBase, product);
        var query = Required(RuntimeIntegrationSettings.FoundryIqQuery, product);
        if (!Uri.TryCreate(searchEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
        {
            throw new RuntimeConfigurationException(
                $"{RuntimeIntegrationSettings.FoundryIqSearchEndpoint} must be the HTTPS Azure AI Search service root, "
                + "not a Foundry project endpoint.", [RuntimeIntegrationSettings.FoundryIqSearchEndpoint]);
        }

        var results = await gateway.QueryAsync(searchEndpoint, knowledgeBase, query, cancellationToken);
        return results.Select(result =>
        {
            if (result is null || string.IsNullOrWhiteSpace(result.Id)
                || (string.IsNullOrWhiteSpace(result.Title) && string.IsNullOrWhiteSpace(result.Content)))
            {
                throw new InvalidOperationException("FoundryIQ returned evidence without a reference or grounding.");
            }

            return new EvidenceItem("foundryiq", result.Id,
                string.Join("\n", new[] { result.Title, result.Content }.Where(text => !string.IsNullOrWhiteSpace(text))));
        }).ToArray();
    }

    private string Required(string name, string product)
    {
        var value = (env.TryGetValue(name, out var configured) ? configured : null)?.Trim();
        return !string.IsNullOrEmpty(value) ? value
            : throw new RuntimeConfigurationException(
                $"the 'foundryiq' source agent for product '{product}' requires {name}.", [name]);
    }
}
