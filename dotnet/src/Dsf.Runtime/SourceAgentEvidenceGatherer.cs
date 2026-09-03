using System.Net.Http.Json;
using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Gathers evidence from a source agent served over A2A: posts the run's scope to
/// the agent's <c>/gather</c> skill endpoint and reads back the evidence it
/// answered with. This is the orchestrator side of the same protocol
/// <see cref="RuntimeVerbs.BuildSourceAgentHost"/> serves. An agent that cannot be
/// reached, refuses, or answers something unreadable throws -- the investigation
/// station turns that into an audited, failed run rather than silent emptiness.
/// </summary>
internal sealed class SourceAgentEvidenceGatherer(string sourceKind, Uri endpoint, HttpClient httpClient)
    : IEvidenceGatherer
{
    public string SourceKind { get; } = sourceKind.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        ConveyorRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var uri = new Uri(endpoint, SourceAgentCard.GatherRoute);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(
                uri,
                new
                {
                    kind = SourceKind,
                    runId = run.Id,
                    product = run.ProductHints.FirstOrDefault() ?? string.Empty,
                    sourceKinds = run.SourceKinds,
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not reach the '{SourceKind}' source agent at {uri}: {exception.Message}", exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"the '{SourceKind}' source agent at {uri} refused to gather "
                    + $"({(int)response.StatusCode}): {body}");
            }

            return Parse(body, uri);
        }
    }

    private IReadOnlyList<EvidenceItem> Parse(string body, Uri uri)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("evidence", out var evidence)
                || evidence.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"the '{SourceKind}' source agent at {uri} answered without an 'evidence' array.");
            }

            return evidence.EnumerateArray()
                .Select(item => new EvidenceItem(
                    SourceKind,
                    Text(item, "reference"),
                    Text(item, "summary")))
                .Where(item => item.Reference.Length > 0)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"the '{SourceKind}' source agent at {uri} answered unreadable JSON: {exception.Message}", exception);
        }
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
