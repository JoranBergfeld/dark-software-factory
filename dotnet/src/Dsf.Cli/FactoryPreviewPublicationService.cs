using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    internal async Task<PreviewPreparationResult> ConfigureAsync(FactoryReservationResult admission, string? approval, bool inspect, CancellationToken token)
    {
        var store = Store(admission);
        var preparation = await ReadJournalAsync(admission, store, token) ?? throw Unknown("ConfirmedNativePreparationRequired");
        PreviewPublicationReview? review = null;
        PreviewPublicationJournal? publication = null;
        ConfigurationSetting? root = null;
        Guid owner = Guid.Empty;
        var dispatched = false;
        ConfigurationSetting? activeRecord = null;
        ConfigurationSetting? acceptedTarget = null;
        try
        {
            var records = await store.ReadNamespaceAsync("dsf/factory/", token);
            var values = records.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal);
            var manifest = Parse<FactoryManifest>(values[ProductConfigurationKeys.FactoryManifest]);
            var confirmed = FactoryPreviewPublication.ReadConfirmed(manifest, values, clock.GetUtcNow());
            root = records.GetValueOrDefault(FactoryPreviewPublication.RootKey);
            if (root is null)
            {
                if (values.Keys.Any(k => k.StartsWith("dsf/factory/preview", StringComparison.Ordinal)
                    && !FactoryPreviewPublication.IsPreparationRecord(k)))
                    throw Unknown("OrphanedPreviewPublicationHistory; no initialization or reset.");
            }
            else
            {
                if (root.ContentType != FactoryPreviewValidation.RecordContentType) throw new JsonException();
                publication = Parse<PreviewPublicationJournal>(root.Value);
                if (publication.Protocol != 1 || publication.FactoryId != manifest.Identity.FactoryId
                    || publication.OperationId != preparation.Plan.Selection.OperationId || publication.PreparationRevision != preparation.Plan.Fingerprint
                    || publication.ApprovedBy != manifest.Identity.AzureActorId || publication.ApprovedAt < manifest.Identity.CreatedAt
                    || publication.ApprovedAt > clock.GetUtcNow() || publication.Owner == Guid.Empty)
                    throw Unknown("PreviewPublicationAuthorityChanged");
                if (publication.Phase is not (PreviewPublicationPhase.SeedAwaitingApproval or PreviewPublicationPhase.ConfiguredAwaitingEvaluation))
                    return Result(preparation, publication.Phase == PreviewPublicationPhase.KnownFailed ? PreviewPreparationState.KnownFailed
                        : PreviewPreparationState.Unknown, "HeldOrUnknownPublication; no seed replay or automatic takeover.")
                        with
                    { PublicationState = publication };
            }
            await RequirePublicationCurrentAsync(admission, preparation, store, token);
            review = PublicationReview(confirmed, values, publication?.Seed, final: publication?.Seed is not null);
            CheckPublicationCapacity(review, manifest, preparation.Plan);
            if (!inspect && publication?.Phase != PreviewPublicationPhase.ConfiguredAwaitingEvaluation
                && (records.Count + review.Writes.Count * 2 + 9 > FactoryPreviewPublication.Policy.MaximumRecords
                || records.Values.Sum(r => System.Text.Encoding.UTF8.GetByteCount(r.Key + r.Value) + 512)
                    + review.Writes.Sum(w => System.Text.Encoding.UTF8.GetByteCount(w.Key + w.Value) + 2048) + 32 * 1024
                        > FactoryPreviewPublication.Policy.NamespaceBytes))
                throw Failure("CompleteProtectedPublicationNamespaceCapacityExceeded");
            if (publication is not null)
            {
                await VerifyPublicationAsync(store, confirmed, publication, values, token);
                var currentCapability = Parse<FactoryPreviewCapability>(values[ProductConfigurationKeys.FactoryPreview]);
                var retainedSeed = await RequestSeedAsync(admission, review with
                { SeedRequest = review.SeedRequest with { ApprovalFingerprint = currentCapability.ApprovalFingerprint } }, "preparation", token);
                if (retainedSeed != publication.Seed) throw Unknown("OriginalNativeSeedReceiptChanged");
            }
            if (publication?.Phase == PreviewPublicationPhase.ConfiguredAwaitingEvaluation)
            {
                if (approval is not null && approval != publication.ApprovalFingerprint) throw Failure("ExactConfirmedPublicationApprovalRequired");
                return PublicationResult(PreviewPreparationState.ConfiguredAwaitingEvaluation, "ConfiguredAwaitingEvaluation; no readiness, evaluation or activation.", review);
            }
            if (approval is null)
                return PublicationResult(publication is null ? PreviewPreparationState.Reviewed : PreviewPreparationState.SeedAwaitingApproval,
                    inspect ? "Read-only current configuration/seed review; explicit fingerprint approval required."
                        : "Approve this exact protected publication and native seed step; execution requires a separate returned-receipt approval.", review);
            if (approval != review.ApprovalFingerprint) throw Failure("ExactPreviewPublicationApprovalRequired; no effects.");
            owner = Guid.NewGuid();
            publication = new(1, manifest.Identity.FactoryId, preparation.Plan.Selection.OperationId, preparation.Plan.Fingerprint,
                manifest.Identity.AzureActorId, clock.GetUtcNow(), owner,
                review.Execution is null ? PreviewPublicationPhase.PublishingSeedCapability : PreviewPublicationPhase.PublishingExecution,
                approval, review.Writes.Count, publication?.Seed, null);
            root = await PublishRecordAsync(store, FactoryPreviewPublication.RootKey, publication, root, token);
            await PublishRecordAsync(store, FactoryPreviewPublication.ApprovalKey(publication.OperationId, review.Stage),
                new PreviewPublicationApproval(publication.OperationId, review.Stage, preparation.Plan.Fingerprint,
                    approval, publication.ApprovedBy, publication.ApprovedAt, owner), null, token);
            var stages = new List<ConfigurationSetting>();
            foreach (var (write, index) in review.Writes.Select((w, i) => (w, i)))
                stages.Add(await PublishRecordAsync(store, FactoryPreviewPublication.StepKey(publication.OperationId, review.Stage, index),
                    new PreviewPublicationStep(index, write.Key, FactoryReservationRecords.Hash(write.Value),
                        FactoryPreviewPublication.RequestId(publication.OperationId, review.Stage + ":" + index),
                        PreviewPreparationEffectState.Intended, null), null, token));
            foreach (var (write, index) in review.Writes.Select((w, i) => (w, i)))
            {
                await RequireHeldAsync(token);
                var previous = await store.ReadAsync(write.Key, token);
                if (write.PreviousSha256 is null ? previous is not null : previous is null || FactoryReservationRecords.Hash(previous.Value) != write.PreviousSha256)
                    throw Unknown("ProtectedPublicationTargetChanged; no overwrite.");
                var step = Parse<PreviewPublicationStep>(stages[index].Value);
                stages[index] = await PublishRecordAsync(store, stages[index].Key, step with { State = PreviewPreparationEffectState.Dispatched }, stages[index], token);
                activeRecord = stages[index];
                dispatched = true;
                acceptedTarget = await store.WriteAsync(write.Key, write.Value, previous?.ETag, token,
                    correlation: step.RequestId,
                    contentType: write.Key == ProductConfigurationKeys.FactoryManifest ? previous!.ContentType : FactoryPreviewValidation.RecordContentType);
                stages[index] = await PublishRecordAsync(store, stages[index].Key,
                    step with { State = PreviewPreparationEffectState.Confirmed, NativeRevision = acceptedTarget.ETag.ToString() }, stages[index], token);
                dispatched = false;
                activeRecord = null;
                acceptedTarget = null;
            }
            await RequirePublicationCurrentAsync(admission, preparation, store, token);
            await RequireHeldAsync(token);
            if (review.Execution is null)
            {
                publication = publication with { Phase = PreviewPublicationPhase.SeedDispatched };
                root = await PublishRecordAsync(store, FactoryPreviewPublication.RootKey, publication, root, token);
                dispatched = true;
                var seed = await RequestSeedAsync(admission, review, "prepare-evaluation", token);
                publication = publication with { Phase = PreviewPublicationPhase.SeedAwaitingApproval, Seed = seed };
                root = await PublishRecordAsync(store, FactoryPreviewPublication.RootKey, publication, root, token);
                dispatched = false;
                await RequirePublicationCurrentAsync(admission, preparation, store, token);
                values = (await store.ReadNamespaceAsync("dsf/factory/", token)).ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal);
                review = PublicationReview(confirmed, values, seed, final: true);
                CheckPublicationCapacity(review, manifest, preparation.Plan);
                return PublicationResult(PreviewPreparationState.SeedAwaitingApproval, "Native conditional seed confirmed; approve the exact returned identity, revision and checksum before evaluation.", review);
            }
            publication = publication with { Phase = PreviewPublicationPhase.ConfiguredAwaitingEvaluation };
            root = await PublishRecordAsync(store, FactoryPreviewPublication.RootKey, publication, root, token);
            var actual = await RequestSeedAsync(admission, review, "preparation", token);
            if (actual != publication.Seed) throw Unknown("OriginalNativeSeedReceiptChanged");
            await RequirePublicationCurrentAsync(admission, preparation, store, token);
            return PublicationResult(PreviewPreparationState.ConfiguredAwaitingEvaluation, "ConfiguredAwaitingEvaluation; no evaluation, readiness or activation.", review);
        }
        catch (Exception e) when (e is ReservationStoreException or PreviewCapabilityException or JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or FormatException or HttpRequestException or IOException or OperationCanceledException
            or CharterAdoptionEvidenceException or CharterOperationException or ModelCapabilityException)
        {
            var rejected = acceptedTarget is null && e is ReservationStoreException { State: FactoryReservationState.Failed };
            var state = rejected ? PreviewPreparationState.KnownFailed
                : owner != Guid.Empty || dispatched || e is JsonException or KeyNotFoundException or InvalidOperationException
                    ? PreviewPreparationState.Unknown : FailureState(e);
            var detail = rejected ? "ConditionalPublicationRejectedWithoutAcceptance; prior confirmed effects retained; no retry."
                : owner != Guid.Empty ? "PreviewPublicationOutcomeUnknown; retained native seed and protected history forbid replay."
                : e is ReservationStoreException or PreviewCapabilityException ? e.Message : "CurrentPublicationAuthorityUnconfirmed";
            if (owner != Guid.Empty && publication is not null && (!rejected || root is not null))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(FactoryPreviewPublication.Policy.CleanupSeconds));
                try
                {
                    var observed = await store.ReadAsync(FactoryPreviewPublication.RootKey, cleanup.Token);
                    if (observed is null || Parse<PreviewPublicationJournal>(observed.Value).Owner != owner)
                        throw Unknown("PublicationAccountingOwnershipUnconfirmed");
                    if (activeRecord is not null)
                        await PublishRecordAsync(store, activeRecord.Key, Parse<PreviewPublicationStep>(activeRecord.Value) with
                        {
                            State = rejected ? PreviewPreparationEffectState.KnownFailed : PreviewPreparationEffectState.Unknown,
                            NativeRevision = acceptedTarget?.ETag.ToString()
                        }, activeRecord, cleanup.Token);
                    publication = publication with { Phase = rejected ? PreviewPublicationPhase.KnownFailed : PreviewPublicationPhase.Unknown, Reason = detail };
                    await PublishRecordAsync(store, observed.Key, publication, observed, cleanup.Token);
                }
                catch (Exception accounting) when (accounting is ReservationStoreException or OperationCanceledException or JsonException)
                { state = PreviewPreparationState.Unknown; detail = "PublicationQuarantineAcknowledgmentUnknown; no replay or root reset."; }
            }
            return PublicationResult(state, detail, review);
        }

        PreviewPreparationResult PublicationResult(PreviewPreparationState state, string detail, PreviewPublicationReview? current) =>
            Result(preparation, state, detail) with { Publication = current, PublicationState = publication };
        async Task RequireHeldAsync(CancellationToken pending)
        {
            var current = await store.ReadAsync(FactoryPreviewPublication.RootKey, pending);
            if (root is null || current?.ETag != root.ETag || current.Value != root.Value
                || Parse<PreviewPublicationJournal>(current.Value).Owner != owner) throw Unknown("PublicationCoordinatorChanged");
        }
    }

    private static PreviewPublicationReview PublicationReview(ConfirmedPreviewPreparation preparation, IReadOnlyDictionary<string, string> values,
        PreviewExecutionSeed? seed, bool final)
    {
        var plan = preparation.Plan;
        var models = FactoryModelsValidation.ReadPreparation(plan.Manifest, values);
        var github = FactoryGitHubAuthorityValidation.ReadPreparation(plan.Manifest, values);
        return FactoryPreviewPublication.Review(preparation, models, github, seed, final);
    }

    private async Task RequirePublicationCurrentAsync(FactoryReservationResult admission, Journal preparation, OwnerReservationStore store,
        CancellationToken token, bool configuredEvaluation = false)
    {
        // After protected configuration, these proofs are reviewed provenance; the runtime re-observes every selected capability.
        var current = await ReadPlanAsync(admission, preparation.Plan.Selection, store, token, preparation.Plan,
            approvedPreparation: configuredEvaluation ? preparation : null);
        if (!SameReviewedInputs(preparation.Plan, current))
            throw Failure("PreviewPublicationPredecessorOrNativeAuthorityChanged");
        await VerifyConfirmedAsync(preparation, token);
        var identity = preparation.Plan.Manifest.Identity;
        var owner = await new FactoryReservationService(reviewer, azure, http, clock)
            .StatusAsync(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId, identity.ProductKey, token);
        if (owner.Identity != identity || owner.State != FactoryReservationState.Reserved || owner.Foundation?.State != FactoryFoundationState.Inactive)
            throw Unknown("CurrentPublicationOwnerAuthorityRequired");
    }

    private async Task<PreviewExecutionSeed> RequestSeedAsync(FactoryReservationResult admission, PreviewPublicationReview review, string route, CancellationToken token)
    {
        var identity = admission.Identity!;
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "api://" + review.Configuration.AudienceClientId);
        using var request = new HttpRequestMessage(HttpMethod.Post, review.SeedEndpoint + "/" + route)
        { Content = JsonContent.Create(review.SeedRequest, options: FactoryGitHubJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(["api://" + review.Configuration.AudienceClientId + "/.default"]), token)).Token);
        using var response = await (http ?? throw Failure("PreviewNativeTransportRequired")).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await response.Content.LoadIntoBufferAsync(FactoryPreviewPublication.Policy.ResponseBytes, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var failed = Parse<PreviewContextFailure>(body);
            throw new PreviewCapabilityException(failed.State, failed.Reason.Length <= 256 ? failed.Reason : "PreviewNativeSeedUnconfirmed");
        }
        var seed = Parse<PreviewExecutionSeed>(body);
        _ = NativeCosmosResourceId.RequireDocument(seed.ResourceId);
        using var intended = JsonDocument.Parse(review.SeedIntent.Document);
        if (seed.Id != FactoryPreviewExecution.Id(identity.FactoryId, review.SeedRequest.OperationId)
            || seed.Checksum != intended.RootElement.GetProperty("checksum").GetString()
            || seed.ETag.Length is < 3 or > 256 || seed.ETag[0] != '"' || seed.ETag[^1] != '"')
            throw Unknown("NativeSeedReceiptMalformed");
        return seed;
    }

    private async Task VerifyPublicationAsync(OwnerReservationStore store, ConfirmedPreviewPreparation preparation,
        PreviewPublicationJournal journal, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        _ = FactoryPreviewPublication.ValidatePublished(preparation,
            FactoryModelsValidation.ReadPreparation(preparation.Plan.Manifest, values),
            FactoryGitHubAuthorityValidation.ReadPreparation(preparation.Plan.Manifest, values), values, clock.GetUtcNow());
        var seed = PublicationReview(preparation, values, null, false);
        var execution = PublicationReview(preparation, values, journal.Seed ?? throw new JsonException(), true);
        var final = journal.Phase == PreviewPublicationPhase.ConfiguredAwaitingEvaluation;
        if (journal.ApprovalFingerprint != (final ? execution : seed).ApprovalFingerprint
            || journal.WriteCount != (final ? execution : seed).Writes.Count) throw new JsonException();
        var records = await store.ReadNamespaceAsync(FactoryPreviewPublication.Namespace, token);
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal) { FactoryPreviewPublication.RootKey };
        foreach (var stage in final ? new[] { seed, execution } : new[] { seed })
            expectedKeys.Add(FactoryPreviewPublication.ApprovalKey(journal.OperationId, stage.Stage));
        foreach (var stage in final ? new[] { seed, execution } : new[] { seed })
            foreach (var (write, index) in stage.Writes.Select((w, i) => (w, i)))
            {
                var key = FactoryPreviewPublication.StepKey(journal.OperationId, stage.Stage, index);
                expectedKeys.Add(key);
                var record = records[key];
                var step = Parse<PreviewPublicationStep>(record.Value);
                if (record.ContentType != FactoryPreviewValidation.RecordContentType || step.Index != index || step.Target != write.Key
                    || step.PayloadSha256 != FactoryReservationRecords.Hash(write.Value) || step.State != PreviewPreparationEffectState.Confirmed
                    || step.RequestId != FactoryPreviewPublication.RequestId(journal.OperationId, stage.Stage + ":" + index)
                    || string.IsNullOrEmpty(step.NativeRevision)) throw new JsonException();
                if (final && stage == seed && execution.Writes.Any(w => w.Key == write.Key)) continue;
                var current = await store.ReadAsync(write.Key, token);
                if (current is null || current.Value != write.Value || current.ETag.ToString() != step.NativeRevision) throw new JsonException();
            }
        if (!expectedKeys.SetEquals(records.Keys)) throw new JsonException();
    }

    private static void CheckPublicationCapacity(PreviewPublicationReview review, FactoryManifest manifest, PreviewPreparationPlan plan)
    {
        if (review.Policy != FactoryPreviewPublication.Policy || System.Text.Encoding.UTF8.GetByteCount(Serialize(review)) > review.Policy.ReviewBytes
            || review.Writes.Count < 1 || review.Writes.Count > review.Policy.MaximumWrites)
            throw Failure("CompletePublicationLifecycleCapacityExceeded");
        foreach (var write in review.Writes)
        {
            OwnerReservationStore.CheckSize(write.Key, "", write.Value, FactoryPreviewValidation.RecordContentType);
            OwnerReservationStore.CheckSize(FactoryPreviewPublication.StepKey(plan.Selection.OperationId, review.Stage, 95), "",
                Serialize(new PreviewPublicationStep(95, write.Key, new string('f', 64), Guid.Empty, PreviewPreparationEffectState.Unknown, new string('f', 256))),
                FactoryPreviewValidation.RecordContentType);
            if (review.Execution is null && write.Key == ProductConfigurationKeys.FactoryPreview)
            {
                var future = System.Text.Json.Nodes.JsonNode.Parse(write.Value)!;
                future["configuration"]!["executionRevision"] = new string('f', 64);
                OwnerReservationStore.CheckSize(write.Key, "", future.ToJsonString(RecordJson), FactoryPreviewValidation.RecordContentType);
            }
        }
        var maximumSeed = new PreviewExecutionSeed(FactoryPreviewExecution.Id(manifest.Identity.FactoryId, plan.Selection.OperationId),
            new string('a', 512), "\"" + new string('a', 254) + "\"", new string('f', 64));
        OwnerReservationStore.CheckSize(FactoryPreviewPublication.RootKey, "", Serialize(new PreviewPublicationJournal(1, manifest.Identity.FactoryId,
            plan.Selection.OperationId, plan.Fingerprint, manifest.Identity.AzureActorId, DateTimeOffset.MaxValue, Guid.NewGuid(),
            PreviewPublicationPhase.Unknown, new string('f', 64), 96, maximumSeed, new string('x', 1024))), FactoryPreviewValidation.RecordContentType);
        OwnerReservationStore.CheckSize(FactoryPreviewExecution.ConfigurationKey(plan.Selection.OperationId), "",
            Serialize(new PreviewExecutionConfiguration(FactoryPreviewExecution.Policy, maximumSeed)), FactoryPreviewValidation.RecordContentType);
        OwnerReservationStore.CheckSize(FactoryPreviewPublication.ApprovalKey(plan.Selection.OperationId, "execution"), "",
            Serialize(new PreviewPublicationApproval(plan.Selection.OperationId, "execution", plan.Fingerprint, new string('f', 64),
                manifest.Identity.AzureActorId, DateTimeOffset.MaxValue, Guid.Empty)), FactoryPreviewValidation.RecordContentType);
        if (System.Text.Encoding.UTF8.GetByteCount(review.SeedIntent.Document) > review.Policy.SeedBytes || review.SeedIntent.Policy != FactoryPreviewExecution.Policy)
            throw Failure("ExactBoundedNativeSeedLifecycleRequired");
    }

    private static Task<ConfigurationSetting> PublishRecordAsync<T>(OwnerReservationStore store, string key, T value, ConfigurationSetting? previous, CancellationToken token) =>
        store.WriteAsync(key, Serialize(value), previous?.ETag, token, contentType: FactoryPreviewValidation.RecordContentType);
}
