using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// The learning-loop persistence adapter, backed by the product's Cosmos
/// account: each (intent key, verdict) pair is recorded under a stable document
/// id via the gateway's atomic create-if-absent primitive (<see
/// cref="ICosmosDocumentGateway.CreateIfAbsentAsync"/>), so recording the same
/// still-labelled issue again -- even from a poll racing concurrently with this
/// one -- is recognized as already recorded and skipped rather than duplicated.
/// Reuses <see cref="ICosmosDocumentGateway"/>, the same real Entra-authenticated
/// gateway <see cref="CosmosRunStore"/> persists the run blackboard through.
/// </summary>
internal sealed class CosmosLearningStore(
    string endpoint,
    string database,
    string container,
    string product,
    ICosmosDocumentGateway gateway) : ILearningStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<bool> RecordAsync(LearningRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = DocumentId(record.IntentKey, record.Verdict);
        var document = JsonSerializer.Serialize(
            new
            {
                id,
                product,
                intentKey = record.IntentKey,
                verdict = record.Verdict,
                issueUrl = record.IssueUrl,
                title = record.Title,
                observedAt = record.ObservedAt,
            },
            SerializerOptions);

        try
        {
            // Delegates the atomicity to the gateway's create-if-absent primitive
            // rather than reading first and upserting second here: two polls
            // racing to record the exact same (intent key, verdict) pair must
            // never both observe "not there yet" and both write -- a race a
            // separate read-then-write in this class could lose.
            return await gateway.CreateIfAbsentAsync(endpoint, database, container, product, id, document, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not persist the learning record for intent '{record.IntentKey}' verdict "
                + $"'{record.Verdict}' to the Cosmos container '{database}/{container}' at '{endpoint}': "
                + $"{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Retrieves every outcome ever recorded for <paramref name="intentKey"/>, so a
    /// later run whose synthesis reaches the exact same scope fingerprint and
    /// source kind (the same intent key) can draw on what a human verdict on
    /// that same conclusion actually was, instead of reasoning blind to its own
    /// history. Point-reads the (intent key, verdict) document for each
    /// canonical outcome label rather than querying the container: with only
    /// three possible verdicts (<see cref="OutcomeLabels.All"/>), this is exact
    /// and needs no query capability the gateway does not already expose.
    /// </summary>
    public async Task<IReadOnlyList<LearningRecord>> RetrieveAsync(string intentKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intentKey);
        var lessons = new List<LearningRecord>();
        foreach (var verdict in OutcomeLabels.All)
        {
            var id = DocumentId(intentKey, verdict);
            string? json;
            try
            {
                json = await gateway.ReadAsync(endpoint, database, container, product, id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"could not read learning lessons for intent '{intentKey}' from the Cosmos container "
                    + $"'{database}/{container}' at '{endpoint}': {exception.Message}",
                    exception);
            }

            if (json is null)
            {
                continue;
            }

            lessons.Add(Deserialize(json));
        }

        return lessons;
    }

    private static LearningRecord Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new LearningRecord(
            root.GetProperty("intentKey").GetString() ?? string.Empty,
            root.GetProperty("verdict").GetString() ?? string.Empty,
            root.TryGetProperty("issueUrl", out var issueUrl) ? issueUrl.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("observedAt", out var observedAt) && observedAt.ValueKind != JsonValueKind.Null
                ? observedAt.GetDateTimeOffset()
                : DateTimeOffset.MinValue);
    }

    /// <summary>
    /// A stable, Cosmos-id-safe document id for the (intent key, verdict) pair --
    /// hashed rather than used raw because an intent key may contain characters
    /// Cosmos rejects in document ids (<c>/</c>, <c>\</c>, <c>?</c>, <c>#</c>).
    /// </summary>
    private static string DocumentId(string intentKey, string verdict) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{intentKey}::{verdict}")))[..32];
}
