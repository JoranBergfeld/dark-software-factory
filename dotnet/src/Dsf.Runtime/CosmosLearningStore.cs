using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// The learning-loop persistence adapter, backed by the product's Cosmos
/// account: each (intent key, verdict) pair is recorded under a stable document
/// id, so recording the same still-labelled issue again on a later poll is
/// recognized as already recorded and skipped rather than duplicated. Reuses
/// <see cref="ICosmosDocumentGateway"/>, the same real Entra-authenticated gateway
/// <see cref="CosmosRunStore"/> persists the run blackboard through.
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

        string? existing;
        try
        {
            existing = await gateway.ReadAsync(endpoint, database, container, product, id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not read the learning record for intent '{record.IntentKey}' verdict '{record.Verdict}' "
                + $"from the Cosmos container '{database}/{container}' at '{endpoint}': {exception.Message}",
                exception);
        }

        if (existing is not null)
        {
            return false;
        }

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
            await gateway.UpsertAsync(endpoint, database, container, product, id, document, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not persist the learning record for intent '{record.IntentKey}' verdict "
                + $"'{record.Verdict}' to the Cosmos container '{database}/{container}' at '{endpoint}': "
                + $"{exception.Message}",
                exception);
        }

        return true;
    }

    /// <summary>
    /// A stable, Cosmos-id-safe document id for the (intent key, verdict) pair --
    /// hashed rather than used raw because an intent key may contain characters
    /// Cosmos rejects in document ids (<c>/</c>, <c>\</c>, <c>?</c>, <c>#</c>).
    /// </summary>
    private static string DocumentId(string intentKey, string verdict) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{intentKey}::{verdict}")))[..32];
}
