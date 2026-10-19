using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal sealed class FactoryActivityStore(FactoryManifest manifest, FactoryPromotionApproval promotion, bool publication,
    AuditRecordIdentity seed, TokenCredential credential, HttpClient http, TimeProvider clock)
{
    private readonly AzureCosmosDocumentGateway gateway = new(credential, http);
    private readonly string endpoint = Endpoint(manifest);
    private string Product => manifest.Identity.ProductKey;
    private string Container => FactoryActivityIndex.Container(publication);
    private string Id(int page = 0) => FactoryActivityIndex.Name(manifest.Identity.FactoryId, publication, page);

    internal static async Task<FactoryNativeIndexReceipt> InitializeProblemsAsync(FactoryManifest manifest, TokenCredential credential,
        HttpClient http, CancellationToken token)
    {
        var factory = manifest.Identity;
        var id = FactoryPromotion.ProblemIndex(factory.FactoryId, factory.RepositoryId);
        var gateway = new AzureCosmosDocumentGateway(credential, http);
        if (await gateway.ReadCheckpointAsync(Endpoint(manifest), factory.ProductKey, FactoryPreviewValidation.PreviewContainer,
            factory.ProductKey, id, token) is not null)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalProblemIndexAlreadyIssuedNoRecreation");
        var body = JsonSerializer.Serialize(new { id, product = factory.ProductKey,
            scope = FactoryPromotion.ProblemScope(factory.FactoryId, factory.RepositoryId), problems = Array.Empty<object>(), ttl = -1 });
        var native = await gateway.WriteCheckpointAsync(Endpoint(manifest), factory.ProductKey, FactoryPreviewValidation.PreviewContainer,
            factory.ProductKey, id, body, null, token);
        return new(Identity(manifest, false, id, native), native.Json);
    }

    internal static async Task<FactoryNativeIndexReceipt> InitializeAsync(FactoryManifest manifest, FactoryPromotionIntent intent,
        bool publication, TokenCredential credential, HttpClient http, TimeProvider clock, CancellationToken token)
    {
        var factory = manifest.Identity;
        var id = FactoryActivityIndex.Name(factory.FactoryId, publication);
        var container = FactoryActivityIndex.Container(publication);
        var gateway = new AzureCosmosDocumentGateway(credential, http);
        if (await gateway.ReadCheckpointAsync(Endpoint(manifest), factory.ProductKey, container, factory.ProductKey, id, token) is not null)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIndexCreationAlreadyIssuedNoAdoptionOrReplay");
        var document = new FactoryActivityIndex(1, id, factory.ProductKey, factory.FactoryId, intent.Review.OperationId, clock.GetUtcNow(), []);
        document = document with { Checksum = document.CalculateChecksum() };
        var native = await gateway.WriteCheckpointAsync(Endpoint(manifest), factory.ProductKey, container, factory.ProductKey, id,
            JsonSerializer.Serialize(document, FactoryGitHubJson.Options), null, token);
        return new(Identity(manifest, publication, id, native), native.Json);
    }

    internal async Task<(FactoryActivityIndex Document, AuditRecordIdentity Identity, string SnapshotRevision)> ReadAsync(CancellationToken token)
    {
        var pages = await ReadPagesAsync(token);
        var root = pages[0];
        var entries = pages.SelectMany(page => page.Document.Entries).ToArray();
        if (entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new JsonException("Original native reference appears on multiple pages.");
        return (root.Document with { Entries = entries }, root.Identity,
            FactoryModelsValidation.Hash(pages.Select(page => page.Identity).ToArray()));
    }

    internal bool HasSourceWork { get; private set; }

    internal async Task RequireSettledAsync(string? inspecting, CancellationToken token)
    {
        var current = await ReadAsync(token);
        foreach (var entry in current.Document.Entries)
        {
            if (entry.NativeResourceId is null)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIndexedAdmissionUnconfirmedNoNewWork");
            var native = await gateway.ReadCheckpointAsync(endpoint, Product, Container, Product, entry.Id, token)
                ?? throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIndexedActivityMissingNoNewWork");
            if (Identity(manifest, publication, entry.Id, native).NativeResourceId != entry.NativeResourceId)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIndexedActivityIncarnationChanged");
            if (publication)
            {
                var journal = FactoryPublication.ReadDocument(native.Json, clock.GetUtcNow());
                if (entry.IntentSha256 != FactoryModelsValidation.Hash(journal.Intent)) throw new JsonException("Original indexed publication intent differs.");
                var originId = FactoryPublication.OriginId(journal.Intent);
                var origin = await gateway.ReadCheckpointAsync(endpoint, Product, Container, Product, originId, token)
                    ?? throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIndexedPublicationWitnessMissing");
                FactoryPublication.RequireOrigin(journal, origin.Json, Identity(manifest, true, originId, origin));
                if (journal.Intent.InvocationId.ToString("N") != inspecting
                    && journal.State is FactoryPublicationState.Dispatched or FactoryPublicationState.Unknown)
                    throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalIssuedPublicationUnconfirmedNoNewWork");
            }
            else
            {
                using var body = JsonDocument.Parse(native.Json, new() { AllowDuplicateProperties = false });
                var decision = PreviewEvaluationDocument.ReadNative(body.RootElement);
                if (decision.Kind != FactoryPromotion.DecisionKind || decision.Checksum != decision.CalculateChecksum()
                    || decision.DecisionScope is null || entry.IntentSha256 != FactoryModelsValidation.Hash(decision.DecisionScope))
                    throw new JsonException("Original indexed decision required.");
                HasSourceWork |= decision.SourceRefresh is { Count: > 0 };
                if (entry.Id != inspecting && (decision.PendingRun is not null
                    || decision.Admission is { State: PreviewWorkState.Queued or PreviewWorkState.Running or PreviewWorkState.Unknown }
                    || decision.Dispatches.Any(d => d.State is PreviewModelDispatchState.Dispatched or PreviewModelDispatchState.Unknown)
                    || PreviewExecutionStore.HasUnconfirmedSourceWork(decision)))
                    throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalDecisionAdmissionInFlightOrUnconfirmedNoNewWork");
            }
        }
    }

    internal async Task<bool> RegisterAsync(string id, string intentHash, Func<CancellationToken, Task> requireCurrent, CancellationToken token,
        FactoryPublicationIntent? intendedPublication = null, FactoryPublicationDecisionScope? intendedDecision = null)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var pages = await ReadPagesAsync(token);
            var entry = pages.SelectMany(page => page.Document.Entries).SingleOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                if (entry.NativeResourceId is null)
                    throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalAdmissionCreationUnconfirmedNoTakeover");
                return false;
            }
            var current = pages[^1];
            if (current.Document.Entries.Count == FactoryActivityIndex.MaximumEntries)
            {
                await requireCurrent(token);
                await EnsureNextPageAsync(current, token);
                continue;
            }
            var next = current.Document with { Entries = [.. current.Document.Entries,
                new(id, intentHash, clock.GetUtcNow()) { Publication = intendedPublication, Decision = intendedDecision }] };
            await requireCurrent(token);
            try { await WriteAsync(next, current.Identity.NativeRevision, token); return true; }
            catch (CosmosCheckpointException e) when (e.Failure == CosmosCheckpointFailure.Conflict) { }
        }
        throw new PreviewCapabilityException(FactoryCharterState.Unknown, "NativeReferenceContentionNoUnindexedAdmission");
    }

    internal async Task ConfirmAsync(string id, string nativeResource, CancellationToken token)
    {
        _ = NativeCosmosResourceId.RequireDocument(nativeResource);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var pages = await ReadPagesAsync(token);
            var current = pages.Single(page => page.Document.Entries.Any(entry => entry.Id == id));
            var entry = current.Document.Entries.Single(e => e.Id == id);
            if (entry.NativeResourceId is { } original)
            {
                if (original != nativeResource) throw new JsonException("Original indexed native document incarnation changed.");
                return;
            }
            try
            {
                await WriteAsync(current.Document with { Entries = current.Document.Entries.Select(e => e.Id == id
                    ? e with { NativeResourceId = nativeResource } : e).ToArray() }, current.Identity.NativeRevision, token);
                return;
            }
            catch (CosmosCheckpointException e) when (e.Failure == CosmosCheckpointFailure.Conflict) { }
        }
        throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalAdmissionIndexCloseoutUnconfirmed");
    }

    private Task<CosmosCheckpoint> WriteAsync(FactoryActivityIndex document, string etag, CancellationToken token)
    {
        document = document with { Checksum = document.CalculateChecksum() };
        return gateway.WriteCheckpointAsync(endpoint, Product, Container, Product, Id(document.Page),
            JsonSerializer.Serialize(document, FactoryGitHubJson.Options), etag, token);
    }

    private async Task<IReadOnlyList<(FactoryActivityIndex Document, AuditRecordIdentity Identity)>> ReadPagesAsync(CancellationToken token)
    {
        var pages = new List<(FactoryActivityIndex Document, AuditRecordIdentity Identity)>();
        string? expectedNativeResourceId = seed.NativeResourceId;
        for (var page = 0; ; page++)
        {
            var id = Id(page);
            var native = await gateway.ReadCheckpointAsync(endpoint, Product, Container, Product, id, token)
                ?? throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalNativeReferenceIndexMissingNoRecreation");
            var identity = Identity(manifest, publication, id, native);
            if (expectedNativeResourceId != identity.NativeResourceId || seed.FactoryId != identity.FactoryId
                || page == 0 && seed.Key != identity.Key)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "OriginalNativeReferenceIndexIncarnationChanged");
            var document = FactoryActivityIndex.Read(native.Json, manifest.Identity.FactoryId, Product,
                promotion.OperationId, publication, clock.GetUtcNow(), page);
            pages.Add((document, identity));
            if (document.NextPageNativeResourceId is null) return pages;
            expectedNativeResourceId = document.NextPageNativeResourceId;
        }
    }

    private async Task EnsureNextPageAsync((FactoryActivityIndex Document, AuditRecordIdentity Identity) current, CancellationToken token)
    {
        var page = checked(current.Document.Page + 1);
        var id = Id(page);
        var native = await gateway.ReadCheckpointAsync(endpoint, Product, Container, Product, id, token);
        if (native is null)
        {
            var document = new FactoryActivityIndex(1, id, Product, manifest.Identity.FactoryId,
                promotion.OperationId, clock.GetUtcNow(), []) { Page = page };
            document = document with { Checksum = document.CalculateChecksum() };
            try
            {
                native = await gateway.WriteCheckpointAsync(endpoint, Product, Container, Product, id,
                    JsonSerializer.Serialize(document, FactoryGitHubJson.Options), null, token);
            }
            catch (CosmosCheckpointException e) when (e.Failure == CosmosCheckpointFailure.Conflict)
            {
                native = await gateway.ReadCheckpointAsync(endpoint, Product, Container, Product, id, token);
            }
        }
        if (native is null)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "NativeReferencePageCreationUnconfirmed");
        var nextIdentity = Identity(manifest, publication, id, native);
        _ = FactoryActivityIndex.Read(native.Json, manifest.Identity.FactoryId, Product,
            promotion.OperationId, publication, clock.GetUtcNow(), page);
        try
        {
            await WriteAsync(current.Document with { NextPageNativeResourceId = nextIdentity.NativeResourceId },
                current.Identity.NativeRevision, token);
        }
        catch (CosmosCheckpointException e) when (e.Failure == CosmosCheckpointFailure.Conflict) { }
    }

    internal static AuditRecordIdentity Identity(FactoryManifest manifest, bool publication, string id, CosmosCheckpoint native)
    {
        using var document = JsonDocument.Parse(native.Json, new() { AllowDuplicateProperties = false });
        return new(manifest.Identity.FactoryId, publication ? AuditStoreKind.Publication : AuditStoreKind.Preview,
            $"dbs/{manifest.Identity.ProductKey}/colls/{FactoryActivityIndex.Container(publication)}/docs/{id}", native.ETag,
            FactoryAudit.TextHash(native.Json))
        { NativeResourceId = NativeCosmosResourceId.RequireDocument(document.RootElement.GetProperty("_rid").GetString()) };
    }
    private static string Endpoint(FactoryManifest manifest) =>
        "https://" + FactoryPreviewValidation.Prefix(manifest.Identity.FactoryId) + "-cosmos.documents.azure.com";
}
