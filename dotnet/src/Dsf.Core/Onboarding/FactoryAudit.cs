using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public enum FactoryAuditState { Inspected, Reviewed, Exported, Verified, Expired, Partial, Blocked, Failed, Canceled, Unknown }
public enum AuditStoreKind { Governance, Analysis, Preview, Run, Publication }
public enum AuditRetentionStatus { Protected, Retained, Eligible }
public sealed record AuditRecordIdentity(Guid FactoryId, AuditStoreKind Store, string Key, string NativeRevision, string Sha256)
{
    public string? NativeResourceId { get; init; }
}
public sealed record FactoryAuditRecord(AuditRecordIdentity Identity, DateTimeOffset ModifiedAt, string Content,
    IReadOnlyList<AuditRecordIdentity> References, AuditRetentionStatus Status);
public sealed record AuditGraph(int Protocol, Guid FactoryId, string Policy, IReadOnlyList<AuditRecordIdentity> Roots,
    IReadOnlyList<FactoryAuditRecord> Records)
{
    public string Revision => FactoryModelsValidation.Hash(new { Protocol, FactoryId, Policy, Roots,
        Records = Records.Select(r => new { r.Identity, r.References }) });
}
public sealed record FactoryAuditResult(FactoryAuditState State, string Detail)
{
    public string? Revision { get; init; }
    public string? ApprovalFingerprint { get; init; }
    public AuditExportReceipt? Receipt { get; init; }
    public int ProtectedRecords { get; init; }
    public int RetainedRecords { get; init; }
    public int EligibleRecords { get; init; }
    public IReadOnlyList<FactoryAuditRecord> Records { get; init; } = [];
    public int? NextPage { get; init; }
    public bool CurrentAuthorization => false;
    public bool DeletionAuthorized => false;
    public string BlockedOperation => "Activation / audit-store deletion";
    public string NextActor => "Operator / auditor";
    public string NextAction { get; init; } = "Inspect retained history; current readiness and activation require separate evidence and approval.";
}
public sealed class FactoryAuditException(FactoryAuditState state, string detail) : Exception(detail)
{
    public FactoryAuditState State { get; } = state;
    public IReadOnlyList<FactoryAuditRecord> Records { get; init; } = [];
}

/// <summary>Bounded, native-revision-pinned evidence graph; historical inspection never establishes current authority.</summary>
public static class FactoryAudit
{
    public const string Namespace = "dsf/factory/audit/";
    public const string Policy = "unused-analysis-90-days-v1";
    public const int MaximumRecords = 81920;
    public const int MaximumBytes = 96 * 1024 * 1024;
    public const int PageSize = 128;
    public const int GovernanceHistoryRecords = FactoryAuditRetention.MaximumRecords + 40 + FactoryLive.MaximumRecords + FactoryPublisher.MaximumRecords;
    public static TimeSpan UnusedAnalysisRetention => TimeSpan.FromDays(90);

    public static AuditGraph Build(Guid factory, IReadOnlyList<FactoryAuditRecord> input, DateTimeOffset now)
    {
        if (factory == Guid.Empty || input.Count > MaximumRecords || input.Sum(r => (long)Encoding.UTF8.GetByteCount(r.Content)) > MaximumBytes)
            throw new FactoryAuditException(FactoryAuditState.Partial, "Complete audit graph exceeds its bounded record/content budget; nothing truncated.");
        var records = input.ToDictionary(r => (r.Identity.Store, r.Identity.Key));
        var edges = input.ToDictionary(r => r.Identity, _ => new HashSet<AuditRecordIdentity>());
        var roots = new HashSet<AuditRecordIdentity>();
        foreach (var record in input)
        {
            if (record.Identity.FactoryId != factory || record.Identity.NativeRevision.Length == 0
                || record.Identity.Sha256 != TextHash(record.Content) || record.ModifiedAt == default || record.ModifiedAt > now)
                throw new JsonException("Invalid audit record identity or time.");
            if (FactorySourceEvidenceIntegrity.Sensitive(record.Content))
                throw new FactoryAuditException(FactoryAuditState.Blocked, "Sensitive retained content withheld; operator must reconcile redaction before audit disclosure/export.");
            if (record.Identity.Store != AuditStoreKind.Analysis || !record.Identity.Key.Contains("/steps/", StringComparison.Ordinal))
                roots.Add(record.Identity);
        }
        var manifest = Parse<FactoryManifest>(Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryManifest));
        if (manifest.Identity.FactoryId != factory) throw new JsonException();
        _ = Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryProfile);
        for (var i = 0; i < manifest.PlanChunks; i++)
            Link(Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryManifest),
                Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryPlanChunk(i)));
        var values = input.Where(r => r.Identity.Store == AuditStoreKind.Governance).ToDictionary(r => r.Identity.Key, r => r.Content);
        var pauses = values.Keys.Where(k => k.StartsWith(FactoryPause.Key, StringComparison.Ordinal)
            || k.StartsWith(FactoryPause.HistoryPrefix, StringComparison.Ordinal)).ToArray();
        if (pauses.Length != 0)
        {
            var root = Require(AuditStoreKind.Governance, FactoryPause.Key);
            var pause = FactoryPause.Read(root.Content, manifest);
            if (pause.RequestedAt > now || pauses.Length > 2) throw new JsonException("Protected pause namespace is inconsistent.");
            var history = records.GetValueOrDefault((AuditStoreKind.Governance, FactoryPause.HistoryKey(pause.OperationId)));
            if (history is null)
                throw new FactoryAuditException(FactoryAuditState.Partial, "Pause closes admission but its audit copy is unconfirmed; no complete closure receipt.");
            if (history.Content != root.Content) throw new JsonException("Protected pause audit copy differs.");
            Link(root, history);
            var profile = Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryProfile);
            if (TextHash(profile.Content) != pause.ProfilePin) throw new JsonException("Reviewed pause profile missing.");
            Link(history, profile);
            for (var i = 0; i < pause.ReviewedManifest.PlanChunks; i++)
                Link(history, Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryPlanChunk(i)));
        }
        var reconfigurationValues = values.Where(p => FactoryReconfigurationOperationRecords.IsRecord(p.Key)).ToDictionary();
        if (reconfigurationValues.Count != 0)
        {
            var root = Require(AuditStoreKind.Governance, FactoryReconfigurationOperationRecords.RootKey);
            var operation = FactoryReconfigurationOperationRecords.Read(root.Content, manifest.Identity, now);
            if (operation.Phase == FactoryReconfigurationPhase.Quiescing
                && (operation.ReviewedManifest != manifest || operation.ManifestRevision !=
                    Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryManifest).Identity.NativeRevision))
                throw new JsonException("Reviewed reconfiguration manifest differs.");
            var manifestRecord = Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryManifest);
            Link(root, manifestRecord);
            if (operation.ConfirmedChunks == operation.InputChunks)
            {
                var archive = FactoryReconfigurationOperationRecords.ValidateArchive(operation, reconfigurationValues);
                if (operation.Phase == FactoryReconfigurationPhase.Quiescing
                    && reconfigurationValues.Count != archive.Count + 1)
                    throw new JsonException("Unexpected reconfiguration operation record.");
                foreach (var key in archive) Link(root, Require(AuditStoreKind.Governance, key));
                foreach (var key in reconfigurationValues.Keys.Except(archive.Append(root.Identity.Key), StringComparer.Ordinal))
                    Link(root, Require(AuditStoreKind.Governance, key));
            }
            else
            {
                var operationPrefix = FactoryReconfigurationOperationRecords.Prefix + operation.OperationId.ToString("N") + "/";
                if (reconfigurationValues.Keys.Any(k => k != root.Identity.Key
                    && !k.StartsWith(operationPrefix, StringComparison.Ordinal)))
                    throw new JsonException("Unexpected reconfiguration operation record.");
                foreach (var key in reconfigurationValues.Keys.Where(k => k != root.Identity.Key))
                    Link(root, Require(AuditStoreKind.Governance, key));
            }
        }
        if (manifest.GitHubAuthorityOperationId is not null || values.ContainsKey(ProductConfigurationKeys.FactoryGitHubAuthority))
        {
            var github = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
            _ = FactoryGitHubAuthorityValidation.ReadCompletedEffects(github, values);
            if (manifest.SourceOperationId is not null || values.ContainsKey(ProductConfigurationKeys.FactorySource))
                _ = FactorySourceValidation.Read(manifest, github, values, requirePrepared: false);
        }
        if (manifest.ModelsOperationId is not null || values.ContainsKey(ProductConfigurationKeys.FactoryModels))
        {
            var models = FactoryModelsValidation.ReadPreparation(manifest, values);
            if (models.LastProofReference is { } proof)
            {
                var target = Require(AuditStoreKind.Governance, proof);
                if (FactoryModelsValidation.Hash(
                    JsonSerializer.Deserialize<ModelCapabilityProof>(target.Content, FactorySourceValidation.RecordJson)) != models.LastProofSha256)
                    throw new JsonException("Model proof integrity differs.");
                Link(Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactoryModels), target);
            }
        }
        if (manifest.TelemetryOperationId is not null || values.ContainsKey(ProductConfigurationKeys.FactoryTelemetry))
        {
            var telemetry = FactoryTelemetryValidation.ReadPreparation(manifest, values, requireChecking: false);
            _ = FactoryTelemetryValidation.ReadCompletedEffects(telemetry, values);
            if (telemetry.Stage == "observed") _ = FactoryTelemetryValidation.RequireVerifiedHistory(telemetry, values);
        }
        foreach (var proof in input.Where(r => r.Identity.Store == AuditStoreKind.Analysis
            && r.Identity.Key.StartsWith("source/", StringComparison.Ordinal) && r.Identity.Key.Contains("/integrity/", StringComparison.Ordinal)))
        {
            using var content = JsonDocument.Parse(proof.Content);
            var snapshot = content.RootElement.GetProperty("snapshotId").GetString();
            var inventory = input.Where(r => r.Identity.Store == AuditStoreKind.Analysis
                && (r.Identity.Key.StartsWith($"source/{snapshot}/nodes/", StringComparison.Ordinal)
                    || r.Identity.Key.StartsWith($"source/{snapshot}/results/", StringComparison.Ordinal)
                    || r.Identity.Key.StartsWith($"source/{snapshot}/chunks/", StringComparison.Ordinal))).ToArray();
            if (inventory.Length == 0 || content.RootElement.GetProperty("sha256").GetString() != FactorySourceEvidenceIntegrity.InventoryDigest(
                    inventory.Select(r => (r.Identity.Key, r.Identity.NativeRevision, r.Content))))
                throw new JsonException("Retained source inventory integrity differs.");
            foreach (var record in inventory) Link(proof, record);
        }
        var sources = input.Where(r => r.Identity.Store == AuditStoreKind.Analysis && r.Identity.Key.StartsWith("source/", StringComparison.Ordinal)).ToArray();
        if (sources.Length != 0)
        {
            var source = Parse<FactorySource>(Require(AuditStoreKind.Governance, ProductConfigurationKeys.FactorySource));
            var root = Require(AuditStoreKind.Analysis, $"source/{source.OperationId:N}/current");
            var progress = Parse<SourceProgress>(root);
            var initialized = Require(AuditStoreKind.Analysis, $"source/{source.OperationId:N}/initialized");
            if (progress.Snapshot.FactoryId != factory || progress.Snapshot.OperationId != source.OperationId
                || progress.Snapshot.ConfigurationRevision != source.ApprovalFingerprint
                || Parse<SourceProgress>(initialized).Snapshot.FactoryId != factory) throw new JsonException();
            Link(root, initialized);
            if (!progress.CompleteRootTree || progress.IntegritySha256 is null)
                throw new FactoryAuditException(FactoryAuditState.Partial, "Source acquisition remains incomplete; retained metadata is inspectable, not an export closure.")
                    { Records = sources.Take(PageSize).ToArray() };
            var proof = Require(AuditStoreKind.Analysis, $"source/{progress.Snapshot.Id}/integrity/00000000");
            using var json = JsonDocument.Parse(proof.Content);
            if (json.RootElement.GetProperty("sha256").GetString() != progress.IntegritySha256) throw new JsonException();
            Link(root, proof);
        }

        var current = records.GetValueOrDefault((AuditStoreKind.Analysis, "intent/current"));
        var intent = input.Where(r => r.Identity.Store == AuditStoreKind.Analysis && r.Identity.Key.StartsWith("intent/", StringComparison.Ordinal)).ToArray();
        var revisions = new Dictionary<Guid, (FactoryAuditRecord Record, IntentProgress Progress)>();
        if (intent.Length != 0)
        {
            if (current is null) throw new JsonException("Missing authoritative analysis checkpoint.");
            var progress = Parse<IntentProgress>(current);
            var initial = Parse<IntentProgress>(Require(AuditStoreKind.Analysis, "intent/initialized"));
            if (progress.RevisionNumber is < 1 or > FactoryIntentLimits.MaximumRevisions || initial.RevisionNumber != 1)
                throw new JsonException();
            revisions.Add(progress.Revision, (current, progress));
            var next = progress;
            for (var i = progress.RevisionNumber - 1; i >= 1; i--)
            {
                var historical = Require(AuditStoreKind.Analysis, "intent/history/" + i.ToString("D2"));
                var prior = Parse<IntentProgress>(historical);
                if (prior.RevisionNumber != i || prior.PendingSelection?.Revision != next.Revision || prior.Dispatch is not null)
                    throw new JsonException("Invalid retained analysis lineage.");
                revisions.Add(prior.Revision, (historical, prior));
                next = prior;
            }
            if (next.Revision != initial.Revision) throw new JsonException();
        }
        foreach (var (_, (record, progress)) in revisions)
        {
            if (progress.Protocol != FactoryIntentLimits.Protocol || progress.Snapshot.FactoryId != factory
                || progress.AvailableChunks is < 1 or > FactoryIntentLimits.MaximumChunks
                || progress.AnalyzedChunks < 0 || progress.AnalyzedChunks > progress.AvailableChunks
                || progress.CheckedChunks < 0 || progress.CheckedChunks > progress.AvailableChunks)
                throw new JsonException();
            var sourceProof = Require(AuditStoreKind.Analysis, $"source/{progress.Snapshot.Id}/integrity/00000000");
            using (var content = JsonDocument.Parse(sourceProof.Content))
                if (content.RootElement.GetProperty("sha256").GetString() != progress.SourceIntegritySha256) throw new JsonException();
            Link(record, sourceProof);
            var count = progress.AnalyzedChunks + progress.CheckedChunks;
            var held = records.GetValueOrDefault((AuditStoreKind.Governance, FactoryAuditRetention.ClaimKey(progress.Revision)));
            var claim = held is null ? null : Parse<AnalysisRetentionClaim>(held);
            if (claim is not null && (claim.Protocol != 1 || claim.FactoryId != factory || claim.Revision != progress.Revision
                || claim.ChainSha256 != progress.ChainSha256 || !Enum.IsDefined(claim.Kind)))
                throw new JsonException("Invalid protected retention claim.");
            var evidence = records.GetValueOrDefault((AuditStoreKind.Analysis, FactoryAuditRetention.EvidenceKey(progress.Revision)));
            var evidenceClaim = evidence is null ? null : Parse<AnalysisEvidenceClaim>(evidence);
            if (evidenceClaim is not null)
            {
                FactoryAuditRetention.RequireEvidence(evidenceClaim, factory, progress.Revision, progress.ChainSha256!, count);
                if (evidenceClaim.Kind == AnalysisRetentionKind.Expiring
                    && (claim is null || claim.Kind == AnalysisRetentionKind.Protected || claim.Operation != evidenceClaim.Operation))
                    throw new JsonException("Analysis expiry interlock has no matching protected disposition.");
                if (evidenceClaim.Kind == AnalysisRetentionKind.Protected && claim?.Kind == AnalysisRetentionKind.Expired)
                    throw new JsonException("Retained runtime reference conflicts with expiry.");
            }
            if (claim?.Kind == AnalysisRetentionKind.Expiring)
                throw new FactoryAuditException(FactoryAuditState.Partial, "Analysis expiry has incomplete or uncertain effects; retain original protected claim, no receipt or retry.")
                { Records = input.Where(r => r.Identity.Store == AuditStoreKind.Governance
                    && r.Identity.Key.StartsWith(FactoryAuditRetention.ClaimKey(progress.Revision), StringComparison.Ordinal)).ToArray() };
            if (claim?.Kind == AnalysisRetentionKind.Expired)
            {
                if (record == current || claim.Records != count) throw new JsonException();
                for (var i = 0; i < count; i++)
                {
                    var effectRecord = Require(AuditStoreKind.Governance, FactoryAuditRetention.EffectKey(progress.Revision, i));
                    var effect = Parse<AuditDeleteEffect>(effectRecord);
                    if (effect.Operation != claim.Operation || effect.Index != i || effect.State != AuditDeleteState.Confirmed
                        || effect.Target.FactoryId != factory || effect.Target.Store != AuditStoreKind.Analysis
                        || effect.Target.Key != StepKey(progress.Revision, i)
                        || records.ContainsKey((AuditStoreKind.Analysis, effect.Target.Key))) throw new JsonException();
                    Link(held!, effectRecord);
                }
                continue;
            }
            FactoryAuditRecord? previous = null;
            string? chain = null;
            for (var i = 0; i < count; i++)
            {
                var step = Require(AuditStoreKind.Analysis, StepKey(progress.Revision, i));
                var analysis = Parse<IntentChunkAnalysis>(step);
                if (analysis.Revision != progress.Revision || analysis.PreviousSha256 != chain
                    || analysis.Source.SnapshotId != progress.Snapshot.Id || analysis.Dispatch.Index != i % progress.AvailableChunks
                    || analysis.Dispatch.Checking != (i >= progress.AvailableChunks))
                    throw new JsonException("Analysis chain is incomplete or corrupt.");
                var modelProof = Require(AuditStoreKind.Governance, analysis.Dispatch.Capability.ProofReference);
                if (FactoryModelsValidation.Hash(Parse<ModelCapabilityProof>(modelProof)) != analysis.Dispatch.Capability.ProofSha256)
                    throw new JsonException("Original analysis model proof is missing or corrupt.");
                Link(step, modelProof);
                chain = FactoryModelsValidation.Hash(new { Key = step.Identity.Key, Value = step.Content, ETag = step.Identity.NativeRevision });
                if (previous is not null) Link(step, previous);
                foreach (var dependency in analysis.Dependencies)
                {
                    var prefix = "source/" + dependency.SnapshotId + "/";
                    var chunk = Require(AuditStoreKind.Analysis, prefix + "chunks/" + dependency.PathIndex.ToString("D8") + "/" + dependency.ChunkIndex.ToString("D4"));
                    if (Parse<SourceChunk>(chunk).Sha256 != dependency.Sha256) throw new JsonException();
                    Link(step, chunk);
                    Link(chunk, Require(AuditStoreKind.Analysis, prefix + "nodes/" + dependency.PathIndex.ToString("D8")));
                    Link(chunk, Require(AuditStoreKind.Analysis, prefix + "results/" + dependency.PathIndex.ToString("D8")));
                }
                previous = step;
            }
            if (chain != progress.ChainSha256) throw new JsonException("Analysis chain checksum differs.");
            if (record == current && previous is not null) Link(record, previous);
            if (claim?.Kind == AnalysisRetentionKind.Protected && previous is not null) Link(held!, previous);
            if (evidenceClaim?.Kind == AnalysisRetentionKind.Protected && previous is not null) Link(evidence!, previous);
        }
        foreach (var evidence in intent.Where(r => r.Identity.Key.EndsWith("/retention", StringComparison.Ordinal)))
        {
            var claim = Parse<AnalysisEvidenceClaim>(evidence);
            if (evidence.Identity.Key != FactoryAuditRetention.EvidenceKey(claim.Revision) || !revisions.ContainsKey(claim.Revision))
                throw new JsonException("Orphaned analysis retention interlock.");
        }
        FactorySourceRefreshAudit.Link(factory, input, Require, Link, ProtectAnalysis);
        foreach (var record in input.Where(r => r.Identity.Store == AuditStoreKind.Governance))
        {
            var key = record.Identity.Key;
            if (key == ProductConfigurationKeys.CharterAdopted
                || key.StartsWith(ProductConfigurationKeys.CharterAdoptionsPrefix, StringComparison.Ordinal) && key.EndsWith("/receipt", StringComparison.Ordinal))
            {
                var adoption = Parse<CharterAdoption>(record);
                Payload(record, adoption.PayloadRecords, adoption.PayloadSha256,
                    i => ProductConfigurationKeys.CharterAdoptionPayload(adoption.Revision, i), adoption.AnalysisRevision);
            }
            else if (key == ProductConfigurationKeys.CharterAdoptionOperation
                || key.StartsWith(ProductConfigurationKeys.CharterAdoptionsPrefix, StringComparison.Ordinal) && key.EndsWith("/intent", StringComparison.Ordinal))
            {
                var adoption = Parse<CharterAdoptionOperation>(record).Adoption;
                Payload(record, adoption.PayloadRecords, adoption.PayloadSha256,
                    i => ProductConfigurationKeys.CharterAdoptionPayload(adoption.Revision, i), adoption.AnalysisRevision);
            }
            else if (key == "dsf/factory/charter/publication" || key.StartsWith("dsf/factory/charter/publications/history/", StringComparison.Ordinal))
            {
                var publication = Parse<CharterPublication>(record);
                Payload(record, publication.PayloadRecords, publication.PayloadSha256,
                    i => $"dsf/factory/charter/publications/{publication.Operation:N}/payload/{i:D4}", null);
                foreach (var effect in publication.Effects.Where(e => e.ResponseRecord is not null))
                {
                    var response = Require(AuditStoreKind.Governance, FactoryCharterResponses.Key(publication.Operation, effect.Attempt));
                    _ = FactoryCharterResponses.Read(publication, effect, response.Content, response.Identity.NativeRevision);
                    Link(record, response);
                }
            }
        }
        if (FactoryAuditDocuments.ReadPreviewPreparation(input.Where(r => r.Identity.Store == AuditStoreKind.Governance)
            .ToDictionary(r => r.Identity.Key, StringComparer.Ordinal)) is { } prepared)
        {
            foreach (var chunk in prepared.Chunks) Link(prepared.Owner, chunk);
            ProtectAnalysis(prepared.Owner, prepared.Plan.Source.AnalysisRevision);
        }
        foreach (var record in input.Where(r => r.Identity.Store == AuditStoreKind.Preview
            && !r.Identity.Key.Contains("/docs/decision-index-", StringComparison.Ordinal)
            && !r.Identity.Key.Contains("/docs/problem-index-", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(record.Content);
            var body = document.RootElement;
            var preview = PreviewEvaluationDocument.ReadNative(body);
            if (preview.Kind == FactoryPromotion.DecisionKind)
            {
                var scope = preview.DecisionScope ?? throw new JsonException("Promoted native decision scope required.");
                foreach (var reference in new[] { scope.Promotion, scope.Publisher })
                {
                    var target = Require(reference.Store, reference.Key);
                    if (target.Identity != reference) throw new JsonException("Original promoted decision authority differs.");
                    Link(record, target);
                }
                Link(record, Require(AuditStoreKind.Preview, $"dbs/{manifest.Identity.ProductKey}/colls/{FactoryPreviewValidation.PreviewContainer}/docs/"
                    + FactoryPromotion.ProblemIndex(factory, manifest.Identity.RepositoryId)));
            }
            var operation = body.GetProperty("operationId").GetGuid();
            Link(Require(AuditStoreKind.Governance, FactoryPreviewExecution.ConfigurationKey(operation)), record);
            foreach (var field in new[] { "inputs", "finalInputs" })
            {
                if (body.GetProperty(field).ValueKind == JsonValueKind.Null) continue;
                var inputs = body.GetProperty(field).Deserialize<PreviewInputSnapshot>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (inputs.Revision != inputs.CalculateRevision() || inputs.Context.FactoryId != factory
                    || inputs.Context.Intent.Analysis.Progress is not { } progress) throw new JsonException();
                FactorySourceRefreshValidation.RequireCurrent(inputs.Source.Context, inputs.Manifest.Identity.ProductKey);
                if (inputs.Source.Context.Refresh is { } refresh)
                {
                    var checkpoint = Require(refresh.Checkpoint.Store, refresh.Checkpoint.Key);
                    var response = Require(refresh.Receipt.Store, refresh.Receipt.Key);
                    if (preview.DecisionScope is null || checkpoint.Identity != refresh.Checkpoint || response.Identity != refresh.Receipt
                        || FactoryModelsValidation.Hash(Parse<SourceRefreshProgress>(checkpoint)) != FactoryModelsValidation.Hash(refresh.Progress)
                        || Parse<SourceRefreshReceipt>(response).Progress != refresh.Checkpoint)
                        throw new JsonException("Captured original compatibility qualification differs.");
                    Link(record, checkpoint);
                    Link(record, response);
                }
                ProtectAnalysis(record, progress.Revision);
                Link(record, Require(AuditStoreKind.Governance, ProductConfigurationKeys.CharterAdoptionReceipt(inputs.Context.Adoption.Revision, "receipt")));
            }
            if (body.GetProperty("run").ValueKind != JsonValueKind.Null)
            {
                var run = body.GetProperty("run");
                Link(record, input.Single(r => r.Identity.Store == AuditStoreKind.Run
                    && r.Identity.NativeResourceId == run.GetProperty("resourceId").GetString()
                    && r.Identity.NativeRevision == run.GetProperty("eTag").GetString()));
            }
        }
        foreach (var record in input.Where(r => r.Identity.Store == AuditStoreKind.Publication
            && !r.Identity.Key.Contains("/docs/publication-index-", StringComparison.Ordinal)))
        {
            using var native = JsonDocument.Parse(record.Content, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (NativeCosmosResourceId.RequireDocument(native.RootElement.GetProperty("_rid").GetString()) != record.Identity.NativeResourceId
                || native.RootElement.GetProperty("_etag").GetString() != record.Identity.NativeRevision
                || record.Identity.Key != $"dbs/{native.RootElement.GetProperty("product").GetString()}/colls/{FactoryPublication.Container}/docs/{native.RootElement.GetProperty("id").GetString()}")
                throw new JsonException("Original native publication identity required.");
            if (record.Identity.Key.EndsWith("-origin", StringComparison.Ordinal))
            {
                if (native.RootElement.GetProperty("protocol").GetInt32() != 1 || native.RootElement.GetProperty("factoryId").GetGuid() != factory
                    || native.RootElement.GetProperty("createdAt").GetDateTimeOffset() > now)
                    throw new JsonException("Original native publication witness required.");
                continue;
            }
            var publication = FactoryPublication.ReadDocument(record.Content, now);
            if (publication.Intent.FactoryId != factory) throw new JsonException();
            foreach (var reference in publication.Intent.References.Append(publication.Origin))
            {
                var target = Require(reference.Store, reference.Key);
                if (target.Identity != reference) throw new JsonException("Original publication evidence missing or changed.");
                Link(record, target);
            }
            using var witness = JsonDocument.Parse(Require(publication.Origin.Store, publication.Origin.Key).Content);
            if (witness.RootElement.GetProperty("intentSha256").GetString() != FactoryModelsValidation.Hash(publication.Intent)
                || witness.RootElement.GetProperty("createdAt").GetDateTimeOffset() != publication.IntendedAt
                || witness.RootElement.GetProperty("repositoryId").GetInt64() != publication.Intent.RepositoryId
                || witness.RootElement.GetProperty("logicalIntent").GetString() != publication.Intent.LogicalIntent)
                throw new JsonException("Original publication creation witness differs.");
        }
        var activationValues = values.Where(p => p.Key.StartsWith(FactoryActivation.Prefix, StringComparison.Ordinal)).ToDictionary();
        if (activationValues.Count != 0)
        {
            var activation = FactoryActivation.Read(activationValues, factory, now);
            var root = Require(AuditStoreKind.Governance, FactoryActivation.RootKey);
            if (activation.Review is not { } review)
                throw new FactoryAuditException(FactoryAuditState.Partial, "Approval admission has no confirmed review closure; retain original operation.");
            var reviewed = Require(AuditStoreKind.Governance, FactoryActivation.ReviewKey(activation.Operation.OperationId));
            Link(root, reviewed);
            var targets = new List<FactoryAuditRecord>();
            foreach (var reference in review.References)
            {
                var target = Require(reference.Store, reference.Key);
                if (reference != target.Identity) throw new JsonException("Original activation evidence revision missing or changed.");
                Link(reviewed, target);
                targets.Add(target);
            }
            FactoryActivation.RequireEvidence(review, targets);
            ProtectAnalysis(reviewed, review.AnalysisRevision);
            if (activation.Approval is not null)
            {
                var receipt = Require(AuditStoreKind.Governance, FactoryActivation.ApprovalKey(activation.Operation.OperationId));
                Link(root, receipt);
                Link(receipt, reviewed);
            }
        }
        var liveValues = values.Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary();
        if (liveValues.Count != 0)
        {
            var root = Require(AuditStoreKind.Governance, FactoryLive.RootKey);
            var coordinator = Parse<FactoryLiveCoordinator>(root);
            var live = FactoryLive.ReadPlan(coordinator, liveValues);
            var approval = Require(live.Approval.Store, live.Approval.Key);
            if (approval.Identity != live.Approval) throw new JsonException("Original rollout approval receipt changed.");
            Link(root, approval);
            for (var i = 0; i < coordinator.PlanChunks; i++)
            {
                var chunk = Require(AuditStoreKind.Governance, FactoryLive.ChunkKey(coordinator.OperationId, i));
                Link(root, chunk);
                Link(chunk, approval);
            }
            var review = Require(AuditStoreKind.Governance, FactoryActivation.ReviewKey(live.Activation.OperationId));
            if (Parse<FactoryActivationReview>(review).Fingerprint != live.Activation.Fingerprint) throw new JsonException();
            Link(root, review);
            foreach (var step in live.Deployment.Steps)
            {
                if (!records.TryGetValue((AuditStoreKind.Governance, FactoryLive.EffectKey(coordinator.OperationId, step.Index)), out var effectRecord))
                {
                    if (step.Index < coordinator.ConfirmedEffects) throw new JsonException();
                    continue;
                }
                var effect = Parse<PreviewPreparationEffect>(effectRecord);
                if (effect.PlanFingerprint != live.Fingerprint || effect.OperationId != coordinator.OperationId || effect.FactoryId != factory
                    || effect.Index != step.Index || effect.Target != step.Target || effect.BodySha256 != step.BodySha256) throw new JsonException();
                Link(root, effectRecord);
                for (var i = 0; i < effect.ReceiptHashes.Count; i++)
                {
                    var receipt = Require(AuditStoreKind.Governance, FactoryLive.ReceiptKey(coordinator.OperationId, step.Index, i));
                    if (FactoryModelsValidation.Hash(Parse<PreviewPreparationReceipt>(receipt)) != effect.ReceiptHashes[i]) throw new JsonException();
                    Link(effectRecord, receipt);
                }
            }
            if (coordinator.State == FactoryLiveState.PreparedAwaitingPublication)
                _ = FactoryLive.ReadConfirmed(liveValues, now);
        }
        var publisherValues = values.Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal)).ToDictionary();
        if (publisherValues.Count != 0)
        {
            var root = Require(AuditStoreKind.Governance, FactoryPublisher.RootKey);
            var coordinator = Parse<FactoryPublisherCoordinator>(root);
            var publisher = FactoryPublisher.ReadPlan(coordinator, publisherValues);
            var publisherFingerprint = publisher.Fingerprint;
            var references = new[] { publisher.Approval, publisher.Live, publisher.GitHub }.Select(reference =>
            {
                var record = Require(reference.Store, reference.Key);
                if (record.Identity != reference) throw new JsonException("Original publication rollout authority reference changed.");
                Link(root, record);
                return record;
            }).ToArray();
            var review = Require(AuditStoreKind.Governance, FactoryActivation.ReviewKey(publisher.Activation.OperationId));
            if (Parse<FactoryActivationReview>(review).Fingerprint != publisher.Activation.Fingerprint) throw new JsonException();
            Link(root, review);
            for (var i = 0; i < coordinator.PlanChunks; i++)
            {
                var chunk = Require(AuditStoreKind.Governance, FactoryPublisher.ChunkKey(coordinator.OperationId, i));
                Link(root, chunk);
                foreach (var reference in references) Link(chunk, reference);
            }
            foreach (var step in publisher.Deployment.Steps)
            {
                if (!records.TryGetValue((AuditStoreKind.Governance, FactoryPublisher.EffectKey(coordinator.OperationId, step.Index)), out var effectRecord))
                {
                    if (step.Index < coordinator.ConfirmedEffects) throw new JsonException();
                    continue;
                }
                var effect = Parse<PreviewPreparationEffect>(effectRecord);
                if (effect.PlanFingerprint != publisherFingerprint || effect.OperationId != coordinator.OperationId || effect.FactoryId != factory
                    || effect.Index != step.Index || effect.Target != step.Target || effect.BodySha256 != step.BodySha256) throw new JsonException();
                Link(root, effectRecord);
                for (var i = 0; i < effect.ReceiptHashes.Count; i++)
                {
                    var receipt = Require(AuditStoreKind.Governance, FactoryPublisher.ReceiptKey(coordinator.OperationId, step.Index, i));
                    if (FactoryModelsValidation.Hash(Parse<PreviewPreparationReceipt>(receipt)) != effect.ReceiptHashes[i]) throw new JsonException();
                    Link(effectRecord, receipt);
                }
            }
            if (coordinator.State == FactoryPublisherState.PublicationPreparedAwaitingPromotion)
                _ = FactoryPublisher.ReadConfirmed(publisherValues, now);
            if (records.TryGetValue((AuditStoreKind.Governance, FactoryPublisher.ObservationKey(coordinator.OperationId)), out var observed))
            {
                FactoryPublisher.ValidateObservation(Parse<FactoryPublisherObservation>(observed), publisher, coordinator.ApprovedAt, now);
                if (coordinator.NativeProviderProof is { } proof && proof != observed.Identity)
                    throw new JsonException("Original native publisher proof revision changed.");
                Link(root, observed);
                foreach (var reference in references) Link(observed, reference);
            }
        }
        if (values.Keys.Any(k => k.StartsWith(FactoryPromotion.Prefix, StringComparison.Ordinal)))
        {
            var publisher = FactoryPublisher.ReadConfirmed(publisherValues, now);
            var identities = input.Where(r => r.Identity.Store == AuditStoreKind.Governance).ToDictionary(r => r.Identity.Key, r => r.Identity);
            var promotion = FactoryPromotion.RequireApproved(values, identities, publisher.Plan, now);
            var root = Require(AuditStoreKind.Governance, FactoryPromotion.RootKey);
            foreach (var reference in promotion.References) Link(root, Require(reference.Store, reference.Key));
            Link(root, Require(AuditStoreKind.Governance, FactoryPromotion.IntentKey));
            var completed = Require(AuditStoreKind.Governance, FactoryPromotion.CompletionKey(promotion.OperationId));
            Link(root, completed);
            Link(completed, root);
            var proof = Require(AuditStoreKind.Governance, promotion.NativeIndices!.Key);
            if (proof.Identity != promotion.NativeIndices) throw new JsonException();
            Link(root, proof);
            var seeds = Parse<FactoryPromotionSeeds>(proof);
            foreach (var (seed, publication) in new[] { (seeds.Decisions.Identity, false), (seeds.Publications.Identity, true) })
            {
                var currentIndex = Require(seed.Store, seed.Key);
                if (currentIndex.Identity.NativeResourceId != seed.NativeResourceId) throw new JsonException();
                Link(proof, currentIndex);
                var entries = new HashSet<string>(StringComparer.Ordinal);
                for (var page = 0; ; page = checked(page + 1))
                {
                    var index = FactoryActivityIndex.Read(currentIndex.Content, factory, manifest.Identity.ProductKey,
                        promotion.OperationId, publication, now, page);
                    foreach (var entry in index.Entries)
                    {
                        if (!entries.Add(entry.Id)) throw new JsonException("Original activity appears on multiple index pages.");
                        foreach (var reference in entry.Publication?.References
                            ?? (entry.Decision is { } scope ? new[] { scope.Promotion, scope.Publisher } : []))
                        {
                            var original = Require(reference.Store, reference.Key);
                            if (original.Identity != reference) throw new JsonException("Original indexed admission evidence differs.");
                            Link(currentIndex, original);
                        }
                        var target = Require(seed.Store, $"dbs/{manifest.Identity.ProductKey}/colls/{FactoryActivityIndex.Container(publication)}/docs/{entry.Id}");
                        if (entry.NativeResourceId is null || target.Identity.NativeResourceId != entry.NativeResourceId) throw new JsonException();
                        Link(currentIndex, target);
                    }
                    if (index.NextPageNativeResourceId is null) break;
                    var next = Require(seed.Store, FactoryActivityIndex.Key(manifest.Identity.ProductKey, factory, publication, checked(page + 1)));
                    if (next.Identity.NativeResourceId != index.NextPageNativeResourceId)
                        throw new JsonException("Original activity index page incarnation differs.");
                    Link(currentIndex, next);
                    currentIndex = next;
                }
            }
            var problemIndex = Require(seeds.Problems.Identity.Store, seeds.Problems.Identity.Key);
            if (problemIndex.Identity.NativeResourceId != seeds.Problems.Identity.NativeResourceId) throw new JsonException();
            using var problems = JsonDocument.Parse(problemIndex.Content, new() { AllowDuplicateProperties = false });
            if (problems.RootElement.GetProperty("scope").GetString() != FactoryPromotion.ProblemScope(factory, manifest.Identity.RepositoryId)
                || problems.RootElement.GetProperty("product").GetString() != manifest.Identity.ProductKey
                || problems.RootElement.GetProperty("id").GetString() != FactoryPromotion.ProblemIndex(factory, manifest.Identity.RepositoryId))
                throw new JsonException("Original native logical-problem scope differs.");
            Link(proof, problemIndex);
        }
        var protectedIds = new HashSet<AuditRecordIdentity>();
        var pending = new Queue<AuditRecordIdentity>(roots);
        while (pending.TryDequeue(out var id))
        {
            if (!protectedIds.Add(id)) continue;
            foreach (var reference in edges[id]) pending.Enqueue(reference);
        }
        return new(1, factory, Policy, roots.OrderBy(Id, StringComparer.Ordinal).ToArray(), input
            .OrderBy(r => Id(r.Identity), StringComparer.Ordinal).Select(r => r with
            {
                References = edges[r.Identity].OrderBy(Id, StringComparer.Ordinal).ToArray(),
                Status = protectedIds.Contains(r.Identity) ? AuditRetentionStatus.Protected
                    : now >= Expiry(r) ? AuditRetentionStatus.Eligible : AuditRetentionStatus.Retained,
            }).ToArray());

        DateTimeOffset Expiry(FactoryAuditRecord record)
        {
            var analysis = Parse<IntentChunkAnalysis>(record);
            if (!revisions.TryGetValue(analysis.Revision, out var revision)) throw new JsonException("Orphaned analysis record.");
            return (record.ModifiedAt > revision.Record.ModifiedAt ? record.ModifiedAt : revision.Record.ModifiedAt) + UnusedAnalysisRetention;
        }
        void ProtectAnalysis(FactoryAuditRecord record, Guid revision)
        {
            if (!revisions.TryGetValue(revision, out var retained)) throw new JsonException("Referenced analysis revision missing.");
            var claim = records.GetValueOrDefault((AuditStoreKind.Governance, FactoryAuditRetention.ClaimKey(revision)));
            if (claim is not null && Parse<AnalysisRetentionClaim>(claim).Kind != AnalysisRetentionKind.Protected)
                throw new JsonException("Live reference conflicts with expiry disposition.");
            Link(record, retained.Record);
            var count = retained.Progress.AnalyzedChunks + retained.Progress.CheckedChunks;
            for (var i = 0; i < count; i++) Link(record, Require(AuditStoreKind.Analysis, StepKey(revision, i)));
        }
        void Payload(FactoryAuditRecord record, int count, string hash, Func<int, string> key, Guid? revision)
        {
            if (count is < 1 or > FactoryCharterLimits.MaximumPayloadRecords) throw new JsonException();
            var content = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var part = Require(AuditStoreKind.Governance, key(i));
                Link(record, part);
                content.Append(Parse<string>(part));
            }
            if (FactoryModelsValidation.Hash(content.ToString()) != hash) throw new JsonException("Incomplete retained charter payload.");
            var payload = JsonSerializer.Deserialize<CharterPublicationPayload>(content.ToString(), FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (payload.Review.FactoryId != factory || revision is not null && payload.Review.AnalysisRevision != revision)
                throw new JsonException();
            ProtectAnalysis(record, payload.Review.AnalysisRevision);
        }
        FactoryAuditRecord Require(AuditStoreKind store, string key) => records.GetValueOrDefault((store, key))
            ?? throw new JsonException("Required audit target missing.");
        void Link(FactoryAuditRecord from, FactoryAuditRecord to) => edges[from.Identity].Add(to.Identity);
    }

    public static string StepKey(Guid revision, int index) => $"intent/{revision:N}/steps/{index:D4}";
    public static string Id(AuditRecordIdentity identity) => FactoryModelsValidation.Hash(identity);
    public static string TextHash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static T Parse<T>(FactoryAuditRecord record) => JsonSerializer.Deserialize<T>(record.Content, FactorySourceValidation.RecordJson) ?? throw new JsonException();
}
