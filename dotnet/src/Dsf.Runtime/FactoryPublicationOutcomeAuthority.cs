using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

// Outcome-only authority never calls admission or grants a dispatch. AUTH alone can replace the original journal.
internal sealed class FactoryPublicationOutcomeAuthority(FactoryHostSettings settings, bool contextHost,
    TokenCredential credential, HttpClient http, TimeProvider clock)
{
    private readonly FactoryInspectionAuthority access = new();
    internal bool AdmissionClosed => access.AdmissionClosed;

    internal Task<PublisherAuthoritySnapshot> ReadAsync(CancellationToken token) =>
        new FactoryPublisherAuthority(settings, contextHost, credential, http, clock, access).CheckAsync(token);

    internal async Task RequireSameAsync(PublisherAuthoritySnapshot original, CancellationToken token)
    {
        if ((await ReadAsync(token)).Revision != original.Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalPublicationOutcomeAuthorityChanged");
    }

    internal async Task<FactoryPublicationJournal> OpenAsync(PublisherAuthoritySnapshot original,
        FactoryPublicationRecoveryRequest request, CancellationToken token)
    {
        var proof = original.Capability.Coordinator.NativeProviderProof;
        if (proof is null || JsonSerializer.Deserialize<FactoryPublisherObservation>(original.Records[proof.Key].Value,
                FactorySourceValidation.RecordJson)?.RecoveryProtocol != FactoryPublicationRecovery.Protocol)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "SeparatelyReviewedNativePublicationRecoveryProtocolRequired");
        var values = original.Records.ToDictionary(p => p.Key, p => p.Value.Value);
        var promotion = FactoryPromotion.RequireApproved(values, original.Records.Keys.ToDictionary(k => k, original.Identity),
            original.Capability.Plan, clock.GetUtcNow());
        var seeds = JsonSerializer.Deserialize<FactoryPromotionSeeds>(values[promotion.NativeIndices!.Key], FactorySourceValidation.RecordJson)
            ?? throw new JsonException();
        var activity = new FactoryActivityStore(original.Manifest, promotion, true, seeds.Publications.Identity, original.Credential, http, clock);
        var index = await activity.ReadAsync(token);
        var entry = index.Document.Entries.SingleOrDefault(e => e.Id == request.Operation);
        if (entry?.NativeResourceId is null)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIndexedPublicationRequiredNoCreation");
        var factory = original.Manifest.Identity;
        var journal = await FactoryPublicationJournal.ReadExistingAsync(factory.FactoryId, factory.ProductKey, factory.RepositoryId,
            request.Operation, original.Credential, http, clock, token);
        if (journal.NativeIdentity!.NativeResourceId != entry.NativeResourceId
            || entry.IntentSha256 != FactoryModelsValidation.Hash(journal.Document.Intent))
            throw new JsonException("Original indexed native publication incarnation or intent differs.");
        await RequireSameAsync(original, token);
        return journal;
    }

    internal async Task<FactoryPublicationRequest> RetryRequestAsync(PublisherAuthoritySnapshot original, FactoryPublicationJournal journal,
        FactoryPublicationRecoveryRequest request, CancellationToken token)
    {
        var retry = new FactoryPublicationRetry(request.Operation, request.Revision!, request.Attempt!.Value);
        journal.RequireRetry(retry);
        var intent = journal.Document.Intent;
        var id = FactoryPromotion.DecisionId(intent.FactoryId, intent.InvocationId) + "-run";
        var reference = intent.References.Single(r => r.Store == AuditStoreKind.Run
            && r.Key == $"dbs/{intent.Product}/colls/runs/docs/{id}");
        var run = await new AzureCosmosDocumentGateway(original.Credential, http).ReadCheckpointAsync(
            "https://" + FactoryPreviewValidation.Prefix(intent.FactoryId) + "-cosmos.documents.azure.com",
            intent.Product, "runs", intent.Product, id, token) ?? throw new JsonException();
        using var body = JsonDocument.Parse(run.Json, new() { AllowDuplicateProperties = false });
        if (run.ETag != reference.NativeRevision || FactoryAudit.TextHash(run.Json) != reference.Sha256
            || body.RootElement.GetProperty("_rid").GetString() != reference.NativeResourceId)
            throw new JsonException("Original native decision run changed.");
        var candidate = body.RootElement.GetProperty("proposals").EnumerateArray()
            .Single(p => p.GetProperty("intentKey").GetString() == intent.LogicalIntent).GetProperty("id").GetString()!;
        return new(intent.InvocationId, candidate) { Retry = retry };
    }
}
