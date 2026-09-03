using System.Net.Http.Headers;
using System.Text.Json;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// A served source agent's connection to the system it speaks for (Sentry,
/// Grafana, an incident tracker, ...). The agent host's <c>/gather</c> endpoint
/// delegates here, so the agent reports either real upstream evidence or the exact
/// reason it could not read any.
/// </summary>
public interface ISourceIntegration
{
    Task<IReadOnlyList<EvidenceItem>> GatherAsync(string kind, string product, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a source kind's evidence from the HTTP endpoint configured for it
/// (<c>DSF_SOURCE_&lt;KIND&gt;_ENDPOINT</c>, optionally with
/// <c>DSF_SOURCE_&lt;KIND&gt;_TOKEN</c> as a bearer token). The response is mapped
/// from the shapes upstream monitoring APIs actually return -- a top-level array,
/// or an object wrapping one under <c>items</c>/<c>data</c>/<c>results</c> -- onto
/// evidence items whose reference is the upstream identifier. An unconfigured kind
/// raises <see cref="RuntimeConfigurationException"/> naming the setting; an
/// upstream that fails raises, rather than answering with no evidence.
/// </summary>
internal sealed class HttpSourceIntegration(IReadOnlyDictionary<string, string?> env, HttpClient? httpClient = null)
    : ISourceIntegration
{
    private static readonly string[] ReferenceProperties = ["reference", "id", "key", "shortId", "uid", "number"];
    private static readonly string[] SummaryProperties = ["summary", "title", "message", "name", "description"];
    private static readonly string[] CollectionProperties = ["items", "data", "results", "evidence", "issues"];

    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public async Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        string kind, string product, CancellationToken cancellationToken)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        var endpointSetting = RuntimeIntegrationSettings.SourceIntegrationEndpoint(normalized);
        var endpoint = Read(endpointSetting);
        if (endpoint.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"the '{normalized}' source agent for product '{product}' has no upstream integration "
                + $"configured: set {endpointSetting} to the API this kind reads evidence from "
                + $"(and {RuntimeIntegrationSettings.SourceIntegrationToken(normalized)} if it needs a token).",
                [endpointSetting]);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var token = Read(RuntimeIntegrationSettings.SourceIntegrationToken(normalized));
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not read '{normalized}' evidence from {endpoint}: {exception.Message}", exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"the '{normalized}' integration at {endpoint} answered {(int)response.StatusCode}: {body}");
            }

            return Map(normalized, endpoint, body);
        }
    }

    private static IReadOnlyList<EvidenceItem> Map(string kind, string endpoint, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"the '{kind}' integration at {endpoint} answered unreadable JSON: {exception.Message}", exception);
        }

        using (document)
        {
            var array = document.RootElement;
            if (array.ValueKind == JsonValueKind.Object)
            {
                var collection = CollectionProperties
                    .Select(name => array.TryGetProperty(name, out var value) ? value : default)
                    .FirstOrDefault(value => value.ValueKind == JsonValueKind.Array);
                if (collection.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"the '{kind}' integration at {endpoint} answered no recognizable collection of "
                        + $"records (looked for {string.Join(", ", CollectionProperties)}).");
                }

                array = collection;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"the '{kind}' integration at {endpoint} answered {array.ValueKind}, not a list of records.");
            }

            return array.EnumerateArray()
                .Select(element => new EvidenceItem(
                    kind,
                    First(element, ReferenceProperties),
                    First(element, SummaryProperties)))
                .Where(item => item.Reference.Length > 0)
                .ToArray();
        }
    }

    private static string First(JsonElement element, IEnumerable<string> properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        }

        foreach (var property in properties)
        {
            if (!element.TryGetProperty(property, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }

                    break;
                case JsonValueKind.Number:
                    return value.GetRawText();
            }
        }

        return string.Empty;
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
