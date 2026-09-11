using System.Text.Json.Nodes;

namespace Dsf.Runtime.Tests;

internal sealed class ProblemIdentityTestGateway : ICosmosDocumentGateway
{
    private readonly object sync = new();
    private readonly Dictionary<(string Product, string Id), string> documents = [];
    public int Conflicts { get; private set; }
    public bool RejectWrites { get; init; }
    public Func<string?, string?>? TransformRead { get; init; }

    public Task UpsertAsync(string endpoint, string database, string container, string partitionKey,
        string id, string json, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Problem identities require conditional writes.");

    public async Task<string?> ReadAsync(string endpoint, string database, string container, string partitionKey,
        string id, CancellationToken cancellationToken)
    {
        string? snapshot;
        lock (sync)
        {
            snapshot = documents.GetValueOrDefault((partitionKey, id));
        }
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return TransformRead is null ? snapshot : TransformRead(snapshot);
    }

    public Task<bool> CreateIfAbsentAsync(string endpoint, string database, string container, string partitionKey,
        string id, string json, CancellationToken cancellationToken) =>
        Write(partitionKey, id, json, null, cancellationToken);

    public Task<bool> ReplaceIfMatchAsync(string endpoint, string database, string container, string partitionKey,
        string id, string json, string etag, CancellationToken cancellationToken) =>
        Write(partitionKey, id, json, etag, cancellationToken);

    private Task<bool> Write(string product, string id, string json, string? etag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = documents.GetValueOrDefault((product, id));
            var currentEtag = current is null ? null : JsonNode.Parse(current)!["_etag"]!.GetValue<string>();
            if (RejectWrites || currentEtag != etag)
            {
                Conflicts++;
                return Task.FromResult(false);
            }
            var document = JsonNode.Parse(json)!;
            document["_etag"] = $"\"{Guid.NewGuid():N}\"";
            documents[(product, id)] = document.ToJsonString();
            return Task.FromResult(true);
        }
    }
}
