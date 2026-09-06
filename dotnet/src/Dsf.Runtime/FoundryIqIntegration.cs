using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>One knowledge base result FoundryIQ's query endpoint answered.</summary>
internal sealed record FoundryIqResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("content")] string? Content);

/// <summary>The shape of a FoundryIQ knowledge base query response.</summary>
internal sealed record FoundryIqQueryResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<FoundryIqResult> Value);

/// <summary>
/// Runs one query against a FoundryIQ knowledge base and hands back its typed
/// results. The real implementation (<see cref="FoundryIqKnowledgeGateway"/>)
/// calls the Azure AI Foundry project's knowledge base query REST endpoint;
/// tests substitute a scripted double instead of a live project.
/// </summary>
internal interface IFoundryIqKnowledgeGateway
{
    Task<IReadOnlyList<FoundryIqResult>> QueryAsync(
        string projectEndpoint, string knowledgeBase, string query, CancellationToken cancellationToken);
}

/// <summary>
/// Calls an Azure AI Foundry project's FoundryIQ knowledge base query endpoint
/// (<c>{projectEndpoint}/knowledgebases/{knowledgeBase}:query</c>), Entra
/// authenticated via <see cref="DefaultAzureCredential"/> -- the same
/// managed-identity-capable pattern every other real Azure adapter in this
/// project uses (ADR 0014: no offline fallback).
/// </summary>
internal sealed class FoundryIqKnowledgeGateway(TokenCredential? credential = null, HttpClient? httpClient = null)
    : IFoundryIqKnowledgeGateway
{
    private const string ApiVersion = "2025-05-01";
    private static readonly string[] Scopes = ["https://ai.azure.com/.default"];
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly TokenCredential credential = credential ?? new DefaultAzureCredential();
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public async Task<IReadOnlyList<FoundryIqResult>> QueryAsync(
        string projectEndpoint, string knowledgeBase, string query, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var uri = new Uri(
            new Uri(EnsureTrailingSlash(projectEndpoint)),
            $"knowledgebases/{Uri.EscapeDataString(knowledgeBase)}:query?api-version={ApiVersion}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new { query }, options: SerializerOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not query FoundryIQ knowledge base '{knowledgeBase}' at {projectEndpoint}: "
                + exception.Message, exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"FoundryIQ knowledge base '{knowledgeBase}' at {projectEndpoint} answered "
                    + $"{(int)response.StatusCode}: {body}");
            }

            FoundryIqQueryResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<FoundryIqQueryResponse>(body, SerializerOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"FoundryIQ knowledge base '{knowledgeBase}' at {projectEndpoint} answered unreadable "
                    + $"JSON: {exception.Message}", exception);
            }

            return parsed?.Value ?? [];
        }
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

/// <summary>
/// The typed <c>foundryiq</c> source integration: reads evidence from a real
/// FoundryIQ knowledge base -- internal/company context, prior decisions, and
/// roadmap fit -- rather than the generic JSON-shape-guessing
/// <see cref="HttpSourceIntegration"/> fallback. Each result's title (falling
/// back to its content) becomes the evidence summary, and its id becomes the
/// reference.
/// </summary>
internal sealed class FoundryIqIntegration(IReadOnlyDictionary<string, string?> env, IFoundryIqKnowledgeGateway? gateway = null)
    : ISourceIntegration
{
    private readonly IFoundryIqKnowledgeGateway gateway = gateway ?? new FoundryIqKnowledgeGateway();

    public async Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        string kind, string product, CancellationToken cancellationToken)
    {
        var projectEndpoint = Read(RuntimeIntegrationSettings.FoundryIqProjectEndpoint);
        if (projectEndpoint.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'foundryiq' source agent for product '{product}' has no Azure AI Foundry project "
                + $"endpoint configured: set {RuntimeIntegrationSettings.FoundryIqProjectEndpoint} to the "
                + "project it queries.",
                [RuntimeIntegrationSettings.FoundryIqProjectEndpoint]);
        }

        var knowledgeBase = Read(RuntimeIntegrationSettings.FoundryIqKnowledgeBase);
        if (knowledgeBase.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'foundryiq' source agent for product '{product}' has no knowledge base configured: "
                + $"set {RuntimeIntegrationSettings.FoundryIqKnowledgeBase} to the knowledge base it queries.",
                [RuntimeIntegrationSettings.FoundryIqKnowledgeBase]);
        }

        var query = Read(RuntimeIntegrationSettings.FoundryIqQuery);
        if (query.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'foundryiq' source agent for product '{product}' has no query configured: set "
                + $"{RuntimeIntegrationSettings.FoundryIqQuery} to the query it runs against knowledge "
                + $"base '{knowledgeBase}'.",
                [RuntimeIntegrationSettings.FoundryIqQuery]);
        }

        var results = await gateway.QueryAsync(projectEndpoint, knowledgeBase, query, cancellationToken);

        return results
            .Select(result => new EvidenceItem(
                "foundryiq", result.Id, string.IsNullOrWhiteSpace(result.Title) ? result.Content ?? string.Empty : result.Title))
            .Where(item => item.Reference.Length > 0)
            .ToArray();
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
