using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsf.Runtime.Tests;

internal sealed class SweepLeaseTestGateway : ICosmosDocumentGateway
{
    private readonly ConcurrentDictionary<(string PartitionKey, string Id), string> documents = new();
    public ConcurrentQueue<(string Endpoint, string Database, string Container, string PartitionKey, string Id, string Json)>
        Creates { get; } = new();
    public ConcurrentQueue<string> Deletes { get; } = new();
    public Exception? DeleteFailure { get; init; }
    public Exception? CreateFailureAfterCommit { get; init; }
    public bool HangReads { get; init; }
    public bool HangCreates { get; init; }
    public TaskCompletionSource Conflict { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task UpsertAsync(
        string endpoint, string database, string container, string partitionKey, string id, string json,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A lease must use atomic creation, never upsert.");

    public Task<string?> ReadAsync(
        string endpoint, string database, string container, string partitionKey, string id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HangReads)
        {
            return new TaskCompletionSource<string?>().Task;
        }

        return Task.FromResult(documents.TryGetValue((partitionKey, id), out var json) ? json : null);
    }

    public Task<bool> CreateIfAbsentAsync(
        string endpoint, string database, string container, string partitionKey, string id, string json,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HangCreates)
        {
            return new TaskCompletionSource<bool>().Task;
        }

        var document = JsonNode.Parse(json)!.AsObject();
        document["_etag"] = $"\"{Guid.NewGuid():N}\"";
        if (!documents.TryAdd((partitionKey, id), document.ToJsonString()))
        {
            Conflict.TrySetResult();
            return Task.FromResult(false);
        }

        Creates.Enqueue((endpoint, database, container, partitionKey, id, json));
        return CreateFailureAfterCommit is null
            ? Task.FromResult(true)
            : Task.FromException<bool>(CreateFailureAfterCommit);
    }

    public Task<bool> DeleteIfMatchAsync(
        string endpoint, string database, string container, string partitionKey, string id, string etag,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DeleteFailure is not null)
        {
            return Task.FromException<bool>(DeleteFailure);
        }

        if (!documents.TryGetValue((partitionKey, id), out var json))
        {
            return Task.FromResult(false);
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("_etag").GetString() != etag
            || !documents.TryRemove(new KeyValuePair<(string, string), string>((partitionKey, id), json)))
        {
            return Task.FromResult(false);
        }

        Deletes.Enqueue(etag);
        return Task.FromResult(true);
    }
}
