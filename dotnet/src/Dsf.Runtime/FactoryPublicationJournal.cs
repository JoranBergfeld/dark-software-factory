using Azure.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal sealed class FactoryPublicationJournal(FactoryPublicationIntent intent, TokenCredential credential, HttpClient http, TimeProvider clock)
{
    private readonly AzureCosmosDocumentGateway gateway = new(credential, http);
    private readonly string endpoint = "https://" + FactoryPreviewValidation.Prefix(intent.FactoryId) + "-cosmos.documents.azure.com";
    private readonly string id = FactoryPublication.Id(intent);
    private FactoryPublicationDocument? document;
    private string? etag;
    private string? resourceId;
    private Guid issued;
    private bool poisoned;
    internal FactoryPublicationDocument Document => document ?? throw new InvalidOperationException("Original native journal must be opened.");
    internal AuditRecordIdentity? NativeIdentity { get; private set; }

    internal async Task<FactoryPublicationDocument> OpenAsync(CancellationToken token)
    {
        if (poisoned || document is not null) throw new InvalidOperationException("A journal instance cannot be reopened.");
        try
        {
            FactoryPublication.Validate(intent);
            var stored = await gateway.ReadCheckpointAsync(endpoint, intent.Product, FactoryPublication.Container, intent.Product, id, token);
            var originId = FactoryPublication.OriginId(intent);
            var origin = await gateway.ReadCheckpointAsync(endpoint, intent.Product, FactoryPublication.Container, intent.Product, originId, token);
            if ((stored is null) != (origin is null))
                throw new InvalidOperationException("Incomplete original publication history; no recreation or takeover.");
            if (stored is null)
            {
                var witness = new FactoryPublicationOrigin(1, originId, intent.Product, intent.FactoryId, intent.RepositoryId,
                    intent.LogicalIntent, FactoryModelsValidation.Hash(intent), clock.GetUtcNow());
                origin = await gateway.WriteCheckpointAsync(endpoint, intent.Product, FactoryPublication.Container, intent.Product, originId,
                    JsonSerializer.Serialize(witness, FactoryGitHubJson.Options), null, token);
                var initial = new FactoryPublicationDocument(1, id, intent.Product, intent, witness.CreatedAt,
                    FactoryPublicationState.Intended, [], null) { Origin = Identity(origin, originId) };
                stored = await gateway.WriteCheckpointAsync(endpoint, intent.Product, FactoryPublication.Container, intent.Product, id,
                    Serialize(initial), null, token);
            }
            Accept(stored);
            var original = Payload(origin!.Json).Deserialize<FactoryPublicationOrigin>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (original.Protocol != 1 || original.Id != originId || original.Product != intent.Product || original.FactoryId != intent.FactoryId
                || original.RepositoryId != intent.RepositoryId || original.LogicalIntent != intent.LogicalIntent
                || original.IntentSha256 != FactoryModelsValidation.Hash(Document.Intent) || original.CreatedAt != Document.IntendedAt
                || Document.Origin != Identity(origin, originId)) throw new JsonException("Original native publication witness changed.");
            if (Document.State is FactoryPublicationState.Intended or FactoryPublicationState.Rejected or FactoryPublicationState.NotDispatched
                && FactoryModelsValidation.Hash(Document.Intent) != FactoryModelsValidation.Hash(intent))
                throw new InvalidOperationException("Original logical intent differs; no replacement or dispatch.");
            return Document;
        }
        catch { poisoned = true; throw; }
    }

    internal async Task<FactoryPublicationDocument> DispatchAsync(CancellationToken token, string? mutationReviewSha256 = null)
    {
        if (poisoned || Document.State is not (FactoryPublicationState.Intended or FactoryPublicationState.Rejected or FactoryPublicationState.NotDispatched)
            || Document.Attempts.Count >= FactoryPublication.MaximumAttempts)
            throw new InvalidOperationException("Publication is not eligible for another dispatch.");
        if (mutationReviewSha256 is not null && !FactorySourceValidation.Hash(mutationReviewSha256)) throw new JsonException();
        var attempt = new FactoryPublicationAttempt(Guid.NewGuid(), clock.GetUtcNow(), FactoryPublicationState.Dispatched)
            { MutationReviewSha256 = mutationReviewSha256 };
        await WriteAsync(Document with { State = FactoryPublicationState.Dispatched, Attempts = [.. Document.Attempts, attempt] }, token);
        issued = attempt.Id;
        return Document;
    }

    internal Task UnknownAsync(CancellationToken token) => FinishAsync(FactoryPublicationState.Unknown, null, null, null, token);
    internal Task NotDispatchedAsync(CancellationToken token) => FinishAsync(FactoryPublicationState.NotDispatched, null, null, null, token);

    internal Task RejectedAsync(int status, string requestId, CancellationToken token)
    {
        if (status is not (401 or 403 or 404 or 422) || !ValidRequestId(requestId)) throw new InvalidOperationException("Native known-no-effect rejection required.");
        return FinishAsync(FactoryPublicationState.Rejected, status, requestId, null, token);
    }

    internal Task ConfirmAsync(FactoryPublicationReceipt receipt, CancellationToken token)
    {
        FactoryPublication.ValidateReceipt(Document.Intent, Document.Attempts.Last(), receipt, clock.GetUtcNow());
        return FinishAsync(FactoryPublicationState.Confirmed, 201, receipt.RequestId, receipt, token);
    }

    private Task FinishAsync(FactoryPublicationState state, int? status, string? requestId, FactoryPublicationReceipt? receipt, CancellationToken token)
    {
        if (poisoned || issued == Guid.Empty || Document.State != FactoryPublicationState.Dispatched || Document.Attempts.Last().Id != issued)
            throw new InvalidOperationException("Only the original acknowledged dispatcher may retain its response.");
        return WriteAsync(Document with { State = state, Receipt = receipt, Attempts = [
            .. Document.Attempts.SkipLast(1), Document.Attempts.Last() with { State = state, StatusCode = status, RequestId = requestId }] }, token);
    }

    private async Task WriteAsync(FactoryPublicationDocument next, CancellationToken token)
    {
        if (poisoned || etag is null) throw new InvalidOperationException("Unconfirmed native writer; no retry.");
        try
        {
            var saved = await gateway.WriteCheckpointAsync(endpoint, intent.Product, FactoryPublication.Container, intent.Product,
                id, Serialize(next), etag, token);
            Accept(saved);
        }
        catch { poisoned = true; throw; }
    }

    private static string Serialize(FactoryPublicationDocument value)
    {
        value = value with { Checksum = value.CalculateChecksum() };
        var json = JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
        if (Encoding.UTF8.GetByteCount(json) + (value.Receipt is null ? 6 * 256 * 1024 + 16384 : 16384) > AzureCosmosDocumentGateway.MaximumCheckpointBytes)
            throw new CosmosCheckpointException(CosmosCheckpointFailure.Capacity);
        return json;
    }

    private void Accept(CosmosCheckpoint stored)
    {
        var node = JsonNode.Parse(stored.Json, documentOptions: new() { AllowDuplicateProperties = false })!.AsObject();
        var native = NativeCosmosResourceId.RequireDocument(node["_rid"]?.GetValue<string>());
        if (resourceId is not null && resourceId != native) throw new InvalidOperationException("Original native publication incarnation changed.");
        var parsed = FactoryPublication.ReadDocument(stored.Json, clock.GetUtcNow());
        if (parsed.Id != id || parsed.Product != intent.Product)
            throw new JsonException("Complete original native publication journal required.");
        resourceId = native;
        etag = stored.ETag;
        document = parsed;
        NativeIdentity = Identity(stored, id);
    }

    private AuditRecordIdentity Identity(CosmosCheckpoint value, string documentId)
    {
        using var body = JsonDocument.Parse(value.Json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        return new(intent.FactoryId, AuditStoreKind.Publication,
            $"dbs/{intent.Product}/colls/{FactoryPublication.Container}/docs/{documentId}", value.ETag, FactoryAudit.TextHash(value.Json))
            { NativeResourceId = NativeCosmosResourceId.RequireDocument(body.RootElement.GetProperty("_rid").GetString()) };
    }

    private static JsonObject Payload(string json)
    {
        var value = JsonNode.Parse(json, documentOptions: new() { AllowDuplicateProperties = false })!.AsObject();
        foreach (var property in AzureCosmosDocumentGateway.SystemProperties) value.Remove(property);
        return value;
    }

    private static bool ValidRequestId(string value) => value.Length is > 0 and <= 256 && !value.Any(char.IsControl);
}
