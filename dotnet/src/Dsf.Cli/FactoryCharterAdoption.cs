using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryCharterService
{
    private const string AdoptionKey = ProductConfigurationKeys.CharterAdopted;
    private const string AdoptionOperationKey = ProductConfigurationKeys.CharterAdoptionOperation;
    private static string AdoptionPayloadKey(Guid revision, int index) => ProductConfigurationKeys.CharterAdoptionPayload(revision, index);

    internal async Task<FactoryCharterResult> ReviewAdoptionAsync(FactoryReservationResult admission, bool existing, CancellationToken token)
    {
        var review = await PreviewAsync(admission, true, token);
        var store = ProductStore(admission);
        var record = await store.ReadAsync(PublicationKey, token);
        if (record is null) await store.RequirePristineAsync(PublicationPrefix, token);
        if (existing)
        {
            if (review.Evidence!.Source.AnalyzedCharterBlob != review.Review!.AfterBlob)
                throw new CharterOperationException(FactoryCharterState.Blocked, "This charter was not unchanged in the complete analyzed source. Review its exact human-merged PR, not --existing.");
            if (record is not null)
            {
                var prior = ReadPublication(record, admission);
                var previousPayload = await ReadPayloadAsync(store, prior, token);
                if (previousPayload.Review.AfterBlob == review.Review.AfterBlob)
                {
                    if (prior.PullRequest is null || prior.Effects.Any(e => e.State != CharterEffectState.Confirmed))
                        throw new CharterOperationException(FactoryCharterState.Unknown, "Matching publication remains unconfirmed; --existing cannot bypass original receipts.");
                    _ = ReadMerge(review.Review, prior, previousPayload.Review, token);
                }
            }
            return review with { Detail = "Review exact unchanged existing charter and complete analysis. No empty PR, adoption or activation." };
        }
        if (record is null) throw new CharterOperationException(FactoryCharterState.Blocked,
            "No trusted publication receipt. Use explicit --existing only to review an existing default-branch charter.");
        var publication = ReadPublication(record, admission);
        if (publication.State != FactoryCharterState.Published || publication.Effects.Count != 3
            || publication.Effects.Any(e => e.State != CharterEffectState.Confirmed || e.Receipt is null)
            || publication.PullRequest is null)
            throw new CharterOperationException(FactoryCharterState.Unknown, "Original publication unconfirmed; no search-based ownership or adoption.");
        var payload = await ReadPayloadAsync(store, publication, token);
        var merge = ReadMerge(review.Review!, publication, payload.Review, token);
        await FencePublicationAsync(store, record, token);
        return review with { Publication = publication, Merge = merge, Detail = "Exact human-merged charter and complete source reconciliation; explicit adoption approval still required.",
            Revision = AdoptionFingerprint(review.Review!, merge, publication.Operation) };
    }

    private CharterMergeReceipt ReadMerge(CharterReview current, CharterPublication publication, CharterReview original, CancellationToken token)
    {
        var native = new OperatorCharterGitHub(reviewer.OperatorGitHub);
        var trusted = publication.PullRequest!;
        var response = native.Request("GET", OperatorCharterGitHub.Route(current, "pulls/" + trusted.Number), null, token);
        var json = response.Body;
        if (response.Status != 200 || response.Receipt.RequestId.Length == 0) throw new JsonException();
        var head = json.GetProperty("head");
        var basis = json.GetProperty("base");
        if (trusted.Id is { } id && json.GetProperty("id").GetInt64() != id || json.GetProperty("node_id").GetString() != trusted.NodeId
            || json.GetProperty("number").GetInt32() != trusted.Number
            || head.GetProperty("repo").GetProperty("id").GetInt64() != current.RepositoryId
            || head.GetProperty("ref").GetString() != trusted.Head
            || basis.GetProperty("repo").GetProperty("id").GetInt64() != current.RepositoryId
            || basis.GetProperty("ref").GetString() != current.DefaultBranch || current.DefaultBranch != original.DefaultBranch)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Native PR identity/default branch changed; no retargeting.");
        if (json.GetProperty("state").GetString() != "closed" || json.GetProperty("merged").ValueKind != JsonValueKind.True
            || json.GetProperty("merged_by").ValueKind != JsonValueKind.Object
            || json.GetProperty("merged_by").GetProperty("type").GetString() != "User"
            || json.TryGetProperty("auto_merge", out var automatic) && automatic.ValueKind != JsonValueKind.Null)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Exact PR is not confirmed human-merged; pending draft cannot govern.");
        var merged = OperatorCharterGitHub.GitId(json.GetProperty("merge_commit_sha"));
        var actor = json.GetProperty("merged_by").GetProperty("id").GetInt64();
        var at = json.GetProperty("merged_at").GetDateTimeOffset();
        if (actor <= 0 || at == default || at > clock.GetUtcNow()) throw new JsonException();
        native.CheckBase(current, token);
        native.CheckFile(current, merged, current.After, current.AfterBlob, token);
        if (merged != current.Commit)
        {
            var ancestry = native.Get(OperatorCharterGitHub.Route(current, "compare/" + merged + "..." + current.Commit), token);
            if (ancestry.GetProperty("behind_by").GetInt32() != 0
                || ancestry.GetProperty("status").GetString() is not ("ahead" or "identical"))
                throw new CharterOperationException(FactoryCharterState.Blocked, "Merged revision is not on the current default branch.");
        }
        return new(json.GetProperty("id").GetInt64(), trusted.NodeId, trusted.Number, merged, actor, at, response.Receipt);
    }

    internal async Task<FactoryCharterResult> AdoptAsync(FactoryReservationResult admission, FactoryCharterResult reviewed,
        bool existing, string approval, CancellationToken token)
    {
        if (reviewed.Review is not { } review || approval != reviewed.Revision)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Exact current adoption review fingerprint required; no authority recorded.");
        var store = ProductStore(admission);
        var previous = await store.ReadAsync(AdoptionKey, token);
        var prior = previous is null ? null : ReadAdoption(previous, admission);
        var held = await store.ReadAsync(AdoptionOperationKey, token);
        if (held is null)
        {
            if (prior is not null) throw new CharterOperationException(FactoryCharterState.Unknown, "Adoption coordinator missing; surviving authority is history.");
            await store.RequirePristineAsync(ProductConfigurationKeys.CharterAdoptionsPrefix, token);
        }
        var heldOperation = held is null ? null : JsonSerializer.Deserialize<CharterAdoptionOperation>(held.Value, FactorySourceValidation.RecordJson)
            ?? throw new JsonException();
        if (heldOperation is not null && (heldOperation.State != CharterEffectState.Confirmed || prior is null
            || FactoryModelsValidation.Hash(heldOperation.Adoption) != FactoryModelsValidation.Hash(prior)))
            throw new CharterOperationException(FactoryCharterState.Unknown, "Original adoption staging/promotion held; inspect the same revision. No replacement or forced retry.");
        if (prior is not null)
        {
            await VerifyAdoptionPayloadAsync(store, prior, token);
            if (prior.ReviewFingerprint == approval) return reviewed with { State = FactoryCharterState.Adopted, Adoption = prior };
            if (prior.Sequence >= FactoryCharterLimits.MaximumRevisions) throw new CharterOperationException(FactoryCharterState.Partial, "Adoption revision limit; no unbounded history or effects.");
        }
        var revision = Guid.NewGuid();
        var payload = FactoryReservationRecords.Serialize(new CharterPublicationPayload(review, reviewed.Evidence!));
        var parts = Enumerable.Range(0, (payload.Length + PayloadChunkSize - 1) / PayloadChunkSize)
            .Select(i => payload.Substring(i * PayloadChunkSize, Math.Min(PayloadChunkSize, payload.Length - i * PayloadChunkSize))).ToArray();
        if (parts.Length is < 1 or > MaximumPayloadRecords)
            throw new CharterOperationException(FactoryCharterState.Partial, "Complete adoption evidence exceeds bounded budget; no truncation or authority recorded.");
        var adoption = new CharterAdoption(1, revision, review.FactoryId, (prior?.Sequence ?? 0) + 1, existing ? "UnchangedExisting" : "HumanMerged", approval,
            review.AfterBlob, FactoryModelsValidation.Hash(review.After), review.Commit, review.RootTree, review.AnalysisRevision,
            review.AnalysisChain, review.AnalysisSource, review.SourceIntegrity, review.ModelRevision, review.OperatorId, clock.GetUtcNow(),
            reviewed.Publication?.Operation, reviewed.Merge, parts.Length, FactoryModelsValidation.Hash(payload));
        OwnerReservationStore.CheckSize(AdoptionKey, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(adoption));
        for (var i = 0; i < parts.Length; i++)
            OwnerReservationStore.CheckSize(AdoptionPayloadKey(revision, i), ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(parts[i]));
        var operation = new CharterAdoptionOperation(adoption, previous?.ETag.ToString(), CharterEffectState.Intended);
        OwnerReservationStore.CheckSize(AdoptionOperationKey, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(operation));
        await CheckAdoptionCurrentAsync();
        var operationRecord = await store.WriteAsync(AdoptionOperationKey, FactoryReservationRecords.Serialize(operation), held?.ETag, token);
        await store.WriteAsync(AdoptionReceiptKey(revision, "intent"), FactoryReservationRecords.Serialize(operation), null, token);
        for (var i = 0; i < parts.Length; i++)
        {
            await FencePublicationAsync(store, operationRecord, token);
            await store.WriteAsync(AdoptionPayloadKey(revision, i), FactoryReservationRecords.Serialize(parts[i]), null, token);
        }
        await CheckAdoptionCurrentAsync();
        await FencePublicationAsync(store, operationRecord, token);
        operationRecord = await store.WriteAsync(AdoptionOperationKey, FactoryReservationRecords.Serialize(operation with
            { State = CharterEffectState.Dispatched }), operationRecord.ETag, token);
        await CheckAdoptionCurrentAsync();
        var record = await store.WriteAsync(AdoptionKey, FactoryReservationRecords.Serialize(adoption), previous?.ETag, token);
        await CheckAdoptionCurrentAsync();
        await store.WriteAsync(AdoptionReceiptKey(revision, "receipt"), record.Value, null, token);
        await store.WriteAsync(AdoptionOperationKey, FactoryReservationRecords.Serialize(operation with
            { State = CharterEffectState.Confirmed }), operationRecord.ETag, token);
        return reviewed with { State = FactoryCharterState.Adopted, Adoption = adoption, Revision = record.ETag.ToString(),
            Detail = "Reviewed intent adopted with exact charter/source/analysis revisions. Factory remains inactive; no self-approval or activation.",
            NextAction = "Adoption is inspectable intent only; no live publication is authorized." };

        async Task CheckAdoptionCurrentAsync()
        {
            var owner = await new FactoryReservationService(reviewer, azure, http, clock).StatusAsync(admission.Identity!.OwnerEndpoint,
                admission.Identity.TenantId, admission.Identity.SubscriptionId, admission.Identity.ProductKey, token);
            if (owner.State != FactoryReservationState.Reserved || owner.Identity != admission.Identity
                || owner.Foundation?.State != FactoryFoundationState.Inactive) throw new JsonException();
            var fresh = await ReviewAdoptionAsync(owner, existing, token);
            if (fresh.Revision != approval) throw new CharterOperationException(FactoryCharterState.Blocked, "Adoption review changed; no current authority asserted.");
        }
    }

    private static string AdoptionFingerprint(CharterReview review, CharterMergeReceipt? merge, Guid? operation) =>
        FactoryCharterAdoptionReader.Fingerprint(review, merge, operation);

    private static CharterAdoption ReadAdoption(ConfigurationSetting record, FactoryReservationResult admission)
    {
        return FactoryCharterAdoptionReader.ReadRecord(
            new(record.Key, record.Label, record.Value, record.ETag.ToString(), record.ContentType), admission.Identity!.FactoryId);
    }

    private async Task<FactoryCharterResult?> AdoptionStatusAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var store = ProductStore(admission);
        var record = await store.ReadAsync(AdoptionKey, token);
        var held = await store.ReadAsync(AdoptionOperationKey, token);
        if (held is null && record is null) await store.RequirePristineAsync(ProductConfigurationKeys.CharterAdoptionsPrefix, token);
        var operation = held is null ? null : JsonSerializer.Deserialize<CharterAdoptionOperation>(held.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (operation is not null && operation.State != CharterEffectState.Confirmed)
            return new(FactoryCharterState.Unknown, "Adoption staging/promotion unconfirmed. Inspect original revision; no replacement.")
                { Revision = held!.ETag.ToString(), HistoricalAdoption = record is null ? null : ReadAdoption(record, admission) };
        if (record is null)
            return operation is null ? null : new(FactoryCharterState.Unknown, "Adopted authority missing despite surviving operation history.");
        var adoption = ReadAdoption(record, admission);
        try
        {
            if (operation is null || FactoryModelsValidation.Hash(operation.Adoption) != FactoryModelsValidation.Hash(adoption))
                throw new CharterOperationException(FactoryCharterState.Unknown, "Adoption operation/receipt mismatch; retained record is history.");
            await VerifyAdoptionPayloadAsync(store, adoption, token);
            var current = await PreviewAsync(admission, true, token);
            if (current.Review!.AfterBlob != adoption.CharterBlob || current.Review.ModelRevision != adoption.ModelRevision)
                return current with { State = FactoryCharterState.Blocked, HistoricalAdoption = adoption,
                    Detail = "Current charter/model differs; retained adoption is historical, not current authorization." };
            FactoryCharterAdoptionReader.RequireCurrent(adoption, current.Evidence!, admission.Identity!.ProductKey);
            return current with { State = FactoryCharterState.Adopted, Adoption = adoption, Revision = record.ETag.ToString(),
                Detail = "Current charter/source/model checked; reviewed intent remains adopted. Activation remains blocked." };
        }
        catch (CharterOperationException e)
        { return new(e.State, e.Message) { HistoricalAdoption = adoption, Revision = record.ETag.ToString() }; }
    }

    private static string AdoptionReceiptKey(Guid revision, string kind) => ProductConfigurationKeys.CharterAdoptionReceipt(revision, kind);
    private static async Task VerifyAdoptionPayloadAsync(OwnerReservationStore store, CharterAdoption adoption, CancellationToken token)
    {
        try
        {
            var retained = await FactoryCharterAdoptionReader.ReadAsync(adoption.FactoryId, store, token);
            if (retained is null || FactoryModelsValidation.Hash(retained.Adoption) != FactoryModelsValidation.Hash(adoption))
                throw new CharterOperationException(FactoryCharterState.Unknown, "Adoption changed during evidence verification.");
        }
        catch (CharterAdoptionEvidenceException e)
        { throw new CharterOperationException(FactoryCharterState.Unknown, e.Message); }
    }
}
