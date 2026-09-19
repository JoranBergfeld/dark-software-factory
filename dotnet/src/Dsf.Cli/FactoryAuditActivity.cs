using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryAuditService
{
    internal async Task<bool> ReadFirstFilingAsync(FactoryReservationResult admission, FactoryPromotionApproval promotion,
        IReadOnlyDictionary<string, ConfigurationSetting> governance, CancellationToken token) =>
        (await ReadPublicationOutcomesAsync(admission, promotion, governance, token)).Any(p => p.State == FactoryPublicationRecoveryState.Confirmed);

    internal async Task<IReadOnlyList<FactoryPublicationInspection>> ReadPublicationOutcomesAsync(FactoryReservationResult admission,
        FactoryPromotionApproval promotion, IReadOnlyDictionary<string, ConfigurationSetting> governance, CancellationToken token)
    {
        var seedRecord = governance[promotion.NativeIndices!.Key];
        if (new AuditRecordIdentity(promotion.FactoryId, AuditStoreKind.Governance, seedRecord.Key, seedRecord.ETag.ToString(),
            FactoryAudit.TextHash(seedRecord.Value)) != promotion.NativeIndices) throw new JsonException();
        var seeds = JsonSerializer.Deserialize<FactoryPromotionSeeds>(seedRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var factory = admission.Identity!;
        var indexRecord = await ReadNativeAsync(admission, FactoryPublication.Container,
            FactoryActivityIndex.Name(factory.FactoryId, true), AuditStoreKind.Publication, token);
        if (indexRecord.Identity.NativeResourceId != seeds.Publications.Identity.NativeResourceId) throw new JsonException();
        var index = FactoryActivityIndex.Read(indexRecord.Content, factory.FactoryId, factory.ProductKey, promotion.OperationId, true, clock.GetUtcNow());
        var outcomes = new List<FactoryPublicationInspection>();
        foreach (var entry in index.Entries)
        {
            var native = await ReadNativeAsync(admission, FactoryPublication.Container, entry.Id, AuditStoreKind.Publication, token);
            if (entry.NativeResourceId is null || entry.NativeResourceId != native.Identity.NativeResourceId) throw new JsonException();
            var journal = FactoryPublication.ReadDocument(native.Content, clock.GetUtcNow());
            if (journal.Intent.FactoryId != factory.FactoryId || journal.Intent.RepositoryId != factory.RepositoryId
                || FactoryModelsValidation.Hash(journal.Intent) != entry.IntentSha256) throw new JsonException();
            var origin = await ReadNativeAsync(admission, FactoryPublication.Container, entry.Id + "-origin", AuditStoreKind.Publication, token);
            FactoryPublication.RequireOrigin(journal, origin.Content, origin.Identity);
            outcomes.Add(FactoryPublicationRecovery.Inspect(journal, native.Identity, governance.Keys.Any(FactoryPause.IsStopRecord)));
        }
        if ((await ReadNativeAsync(admission, FactoryPublication.Container, index.Id, AuditStoreKind.Publication, token)).Identity != indexRecord.Identity)
            throw new JsonException("Publication history changed during inspection.");
        return outcomes;
    }

    private async Task ReadActivityAsync(FactoryReservationResult admission, IReadOnlyDictionary<string, ConfigurationSetting> governance,
        List<FactoryAuditRecord> records, CancellationToken token)
    {
        if (!governance.Keys.Any(k => k.StartsWith(FactoryPromotion.Prefix, StringComparison.Ordinal)))
        {
            await RequireIndexedWriterAsync(admission, governance, token);
            return;
        }
        if (!governance.TryGetValue(FactoryPromotion.IntentKey, out var intentRecord))
            throw new FactoryAuditException(FactoryAuditState.Unknown, "Original promotion intent missing; native history cannot be recreated.");
        var intent = JsonSerializer.Deserialize<FactoryPromotionIntent>(intentRecord.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (!governance.TryGetValue(FactoryPromotion.SeedsKey(intent.Review.OperationId), out var proof))
        {
            var factory = admission.Identity!;
            var unacknowledged = new List<FactoryAuditRecord>();
            foreach (var (container, id, kind) in new[]
            {
                (FactoryPublication.Container, FactoryActivityIndex.Name(factory.FactoryId, true), AuditStoreKind.Publication),
                (FactoryPreviewValidation.PreviewContainer, FactoryActivityIndex.Name(factory.FactoryId, false), AuditStoreKind.Preview),
                (FactoryPreviewValidation.PreviewContainer, FactoryPromotion.ProblemIndex(factory.FactoryId, factory.RepositoryId), AuditStoreKind.Preview)
            })
            {
                try
                {
                    var observed = await ReadNativeAsync(admission, container, id, kind, token);
                    unacknowledged.Add(observed with { Status = AuditRetentionStatus.Retained });
                }
                catch (JsonException) { }
            }
            throw new FactoryAuditException(FactoryAuditState.Unknown,
                "Native promotion index creation is unconfirmed. Point observations are not original creation/ownership receipts; no adoption, replay or complete export.")
                { Records = [.. records.Where(r => r.Identity.Key == FactoryPromotion.IntentKey), .. unacknowledged] };
        }
        var seeds = JsonSerializer.Deserialize<FactoryPromotionSeeds>(proof.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(governance[ProductConfigurationKeys.FactoryManifest].Value,
            FactorySourceValidation.RecordJson) ?? throw new JsonException();
        FactoryPromotion.RequireSeeds(seeds, intent.Review, manifest, clock.GetUtcNow());
        var unconfirmed = false;
        var indices = new List<(FactoryAuditRecord Record, string Container, string Id)>();
        foreach (var (seed, publication) in new[] { (seeds.Decisions.Identity, false), (seeds.Publications.Identity, true) })
        {
            var container = FactoryActivityIndex.Container(publication);
            var id = FactoryActivityIndex.Name(manifest.Identity.FactoryId, publication);
            var record = await ReadNativeAsync(admission, container, id, seed.Store, token);
            if (record.Identity.NativeResourceId != seed.NativeResourceId) throw new JsonException("Original native activity index incarnation differs.");
            var index = FactoryActivityIndex.Read(record.Content, manifest.Identity.FactoryId, manifest.Identity.ProductKey,
                seeds.OperationId, publication, clock.GetUtcNow());
            records.Add(record);
            indices.Add((record, container, id));
            foreach (var entry in index.Entries)
            {
                if (publication)
                {
                    try { records.Add(await ReadNativeAsync(admission, container, entry.Id + "-origin", AuditStoreKind.Publication, token)); }
                    catch (JsonException) { unconfirmed = true; }
                }
                FactoryAuditRecord retained;
                try { retained = await ReadNativeAsync(admission, container, entry.Id, seed.Store, token); }
                catch (JsonException) { unconfirmed = true; continue; }
                records.Add(retained);
                if (entry.NativeResourceId is null || retained.Identity.NativeResourceId != entry.NativeResourceId)
                { unconfirmed = true; continue; }
                if (publication)
                {
                    var journal = FactoryPublication.ReadDocument(retained.Content, clock.GetUtcNow());
                    if (entry.IntentSha256 != FactoryModelsValidation.Hash(journal.Intent)) throw new JsonException("Original indexed publication intent differs.");
                    unconfirmed |= journal.State is FactoryPublicationState.Dispatched or FactoryPublicationState.Unknown;
                }
                else
                {
                    using var document = JsonDocument.Parse(retained.Content);
                    var evaluation = PreviewEvaluationDocument.ReadNative(document.RootElement);
                    if (evaluation.Kind != FactoryPromotion.DecisionKind || evaluation.FactoryId != manifest.Identity.FactoryId
                        || evaluation.DecisionScope is not { } scope || evaluation.Checksum != evaluation.CalculateChecksum()
                        || entry.IntentSha256 != FactoryModelsValidation.Hash(scope)
                        || evaluation.Id != FactoryPromotion.DecisionId(manifest.Identity.FactoryId, scope.InvocationId))
                        throw new JsonException("Original indexed promoted decision differs.");
                    unconfirmed |= evaluation.PendingRun is not null
                        || evaluation.Admission is { State: PreviewWorkState.Queued or PreviewWorkState.Running or PreviewWorkState.Unknown }
                        || evaluation.Dispatches.Any(d => d.State is PreviewModelDispatchState.Dispatched or PreviewModelDispatchState.Unknown);
                    if (evaluation.Run is not null || evaluation.PendingRun is not null)
                    {
                        try
                        {
                            var run = await ReadNativeAsync(admission, "runs", evaluation.Id + "-run", AuditStoreKind.Run, token);
                            records.Add(run);
                            using var body = JsonDocument.Parse(run.Content);
                            if (evaluation.PendingRun is null && evaluation.Run is { } receipt
                                && (run.Identity.NativeResourceId != receipt.ResourceId || run.Identity.NativeRevision != receipt.ETag
                                    || receipt.NativeDocumentSha256 != FactoryModelsValidation.Hash(body.RootElement)
                                    || receipt.Checksum != body.RootElement.GetProperty("onboardingChecksum").GetString()))
                                throw new JsonException("Original retained live run differs.");
                        }
                        catch (JsonException) { unconfirmed = true; }
                    }
                }
                if ((await ReadNativeAsync(admission, container, entry.Id, seed.Store, token)).Identity != retained.Identity)
                    unconfirmed = true;
            }
        }
        var problems = await ReadNativeAsync(admission, FactoryPreviewValidation.PreviewContainer,
            FactoryPromotion.ProblemIndex(manifest.Identity.FactoryId, manifest.Identity.RepositoryId), AuditStoreKind.Preview, token);
        if (problems.Identity.NativeResourceId != seeds.Problems.Identity.NativeResourceId) throw new JsonException("Original problem identity index differs.");
        records.Add(problems);
        foreach (var item in indices)
            if ((await ReadNativeAsync(admission, item.Container, item.Id, item.Record.Identity.Store, token)).Identity != item.Record.Identity)
                unconfirmed = true;
        if (unconfirmed)
            throw new FactoryAuditException(FactoryAuditState.Unknown, "Native admitted/effect history remains in flight, unconfirmed or changed; no complete export closure.")
                { Records = records.Where(r => r.Identity.Store is AuditStoreKind.Preview or AuditStoreKind.Run or AuditStoreKind.Publication)
                    .Take(FactoryAudit.PageSize).ToArray() };
    }

    private async Task RequireIndexedWriterAsync(FactoryReservationResult admission,
        IReadOnlyDictionary<string, ConfigurationSetting> governance, CancellationToken token)
    {
        if (!governance.Keys.Any(k => k.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal))) return;
        try
        {
            if (governance.TryGetValue(FactoryLive.RootKey, out var root)
                && JsonSerializer.Deserialize<FactoryLiveCoordinator>(root.Value, FactorySourceValidation.RecordJson) is { } coordinator
                && coordinator.State != FactoryLiveState.PreparedAwaitingPublication) return;
            var live = FactoryLive.ReadConfirmed(governance.Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => p.Value.Value), clock.GetUtcNow());
            var factory = admission.Identity!;
            var audience = "api://" + live.Plan.Selection.AudienceClientId;
            var credential = new OwnerReservationStore.OperatorCredential(azure, factory.TenantId, factory.SubscriptionId,
                factory.AzureActorId, audience);
            using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, live.Endpoint + "/activation-capability")
            { Content = JsonContent.Create(new FactoryLiveRequest(live.Plan.Selection.OperationId, live.Plan.Fingerprint),
                options: FactorySourceValidation.RecordJson) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
                new TokenRequestContext([audience + "/.default"]), pending.Token)).Token);
            using var response = await (http ?? NativeHttp).SendAsync(request, pending.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw new JsonException();
            await response.Content.LoadIntoBufferAsync(64 * 1024, pending.Token);
            var capability = await response.Content.ReadFromJsonAsync<FactoryDecisionCapability>(FactorySourceValidation.RecordJson, pending.Token)
                ?? throw new JsonException();
            if (capability.Protocol != 1 || capability.FactoryId != factory.FactoryId || capability.RolloutFingerprint != live.Plan.Fingerprint
                || capability.Workflow != FactoryPromotion.Workflow || capability.Policy != FactoryPreviewExecution.Policy) throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or PreviewCapabilityException or InvalidOperationException or ArgumentException
            or KeyNotFoundException or HttpRequestException or IOException or OwnerReservationStore.AuthenticationFailedException
            || e is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new FactoryAuditException(FactoryAuditState.Unknown,
                "Pinned live writer discovery protocol unconfirmed; arbitrary legacy executions cannot be declared reference-closed. No complete export.");
        }
    }
}
