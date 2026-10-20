using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

internal static class FactorySourceRefreshAudit
{
    internal static void Link(Guid factory, IReadOnlyList<FactoryAuditRecord> input,
        Func<AuditStoreKind, string, FactoryAuditRecord> require, Action<FactoryAuditRecord, FactoryAuditRecord> link,
        Action<FactoryAuditRecord, Guid> protectAnalysis)
    {
        var records = input.Where(r => r.Identity.Store == AuditStoreKind.Analysis
            && r.Identity.Key.StartsWith(FactorySourceRefresh.Prefix, StringComparison.Ordinal)).ToArray();
        var root = records.Length == 0 ? null : require(AuditStoreKind.Analysis, FactorySourceRefresh.CurrentKey);
        foreach (var record in records.Where(r => r == root || r.Identity.Key.EndsWith("/completed", StringComparison.Ordinal)
            || r.Identity.Key.StartsWith(FactorySourceRefresh.Prefix + "checkpoints/", StringComparison.Ordinal)))
        {
            var progress = Parse<SourceRefreshProgress>(record);
            if (progress.Protocol != 1 || progress.Id == Guid.Empty || progress.Binding.Snapshot.FactoryId != factory
                || progress.Recovery is not null && !FactorySourceRefreshValidation.HasConfirmedRecovery(progress)
                || !FactorySourceRefreshValidation.HasBoundedCompatibilityAttempts(progress)
                || !Enum.IsDefined(progress.State) || !Enum.IsDefined(progress.Phase)
                || progress.CheckedChunks < 0 || progress.Chunks > FactoryIntentLimits.MaximumChunks || progress.CheckedChunks > progress.Chunks)
                throw new JsonException("Invalid descriptive source refresh.");
            var initial = require(AuditStoreKind.Analysis, FactorySourceRefresh.InitialKey(progress.Id));
            var original = Parse<SourceRefreshProgress>(initial);
            if (original.Id != progress.Id || original.Binding != progress.Binding
                || original.PreviousRefresh != progress.PreviousRefresh) throw new JsonException("Original source refresh binding differs.");
            link(record, initial);
            if (progress.PreviousRefresh is { } previous)
                link(initial, require(AuditStoreKind.Analysis, FactorySourceRefresh.HistoryKey(previous)));
            var adopted = require(AuditStoreKind.Governance, ProductConfigurationKeys.CharterAdoptionReceipt(progress.Binding.AdoptionRevision, "receipt"));
            var adoption = Parse<CharterAdoption>(adopted);
            if (adoption.FactoryId != factory || adoption.CharterSha256 != progress.Binding.CharterSha256
                || adoption.AnalysisRevision != progress.Binding.OriginalAnalysis || adoption.ModelRevision != progress.Binding.ModelRevision)
                throw new JsonException("Original human charter basis differs.");
            link(record, adopted);
            protectAnalysis(record, progress.Binding.OriginalAnalysis);
            if (progress.LastFailure is { } failure)
            {
                var proof = require(AuditStoreKind.Governance, failure.Dispatch.Capability.ProofReference);
                if (failure.State is not (IntentState.Blocked or IntentState.Failed or IntentState.Partial)
                    || string.IsNullOrWhiteSpace(failure.Reason) || failure.Reason.Length > 256
                    || failure.Dispatch.Capability.ConfigurationRevision != progress.Binding.ModelRevision
                    || FactoryModelsValidation.Hash(Parse<ModelCapabilityProof>(proof)) != failure.Dispatch.Capability.ProofSha256)
                    throw new JsonException("Original known compatibility failure differs.");
                link(record, proof);
            }
            if (progress.AnalysisRevision is { } analysis)
            {
                var retention = require(AuditStoreKind.Analysis, FactoryAuditRetention.EvidenceKey(analysis));
                var claim = Parse<AnalysisEvidenceClaim>(retention);
                FactoryAuditRetention.RequireEvidence(claim, factory, analysis, progress.AnalysisChain!, 2 * progress.Chunks);
                if (claim.Kind != AnalysisRetentionKind.Protected) throw new JsonException("Referenced refreshed analysis was not retained.");
                link(record, retention);
                protectAnalysis(record, analysis);
            }
            string? chain = null;
            var addressed = new HashSet<string>();
            for (var index = 0; index < progress.CheckedChunks; index++)
            {
                var stepRecord = require(AuditStoreKind.Analysis, FactorySourceRefresh.StepKey(progress.Id, index));
                var step = Parse<SourceCompatibilityStep>(stepRecord);
                if (step.Refresh != progress.Id || step.Analysis != progress.AnalysisRevision || step.Dispatch.Index != index
                    || step.Source.SnapshotId != progress.Binding.Snapshot.Id || step.PreviousSha256 != chain
                    || step.Dispatch.Capability.ConfigurationRevision != progress.Binding.ModelRevision || !step.Dispatch.Checking
                    || step.Result.Judgments is null || step.Result.Judgments.Any(j => j is null || !Enum.IsDefined(j.Verdict)))
                    throw new JsonException("Incomplete original compatibility chain.");
                if (progress.State == SourceRefreshState.Compatible && (!step.Result.Complete
                    || step.Result.Judgments.Any(j => j.Verdict is SourceCompatibilityVerdict.Conflict or SourceCompatibilityVerdict.Unknown)))
                    throw new JsonException("Compatibility receipt contains unresolved governing impact.");
                var proof = require(AuditStoreKind.Governance, step.Dispatch.Capability.ProofReference);
                if (FactoryModelsValidation.Hash(Parse<ModelCapabilityProof>(proof)) != step.Dispatch.Capability.ProofSha256)
                    throw new JsonException("Original compatibility model proof differs.");
                link(stepRecord, proof);
                foreach (var dependency in step.Dependencies)
                {
                    var chunk = require(AuditStoreKind.Analysis, $"source/{dependency.SnapshotId}/chunks/{dependency.PathIndex:D8}/{dependency.ChunkIndex:D4}");
                    if (Parse<SourceChunk>(chunk).Sha256 != dependency.Sha256)
                        throw new JsonException("Original compatibility dependency differs.");
                    link(stepRecord, chunk);
                }
                chain = FactoryModelsValidation.Hash(new { Key = stepRecord.Identity.Key, Value = stepRecord.Content, ETag = stepRecord.Identity.NativeRevision });
                addressed.UnionWith(step.Result.Judgments.Where(j => j.Verdict == SourceCompatibilityVerdict.Compatible).Select(j => j.Target));
                link(record, stepRecord);
            }
            if (chain != progress.CompatibilityChain || !addressed.SetEquals(progress.AddressedTargets)
                || progress.State == SourceRefreshState.Compatible && (progress.Phase != SourceRefreshPhase.Complete
                    || progress.CheckedChunks != progress.Chunks || progress.Chunks == 0 || progress.Work is not null || progress.Dispatch is not null))
                throw new JsonException("Incomplete compatibility checkpoint.");
            if (progress.Work is not null || progress.Dispatch is not null || progress.State == SourceRefreshState.Unknown)
                throw new FactoryAuditException(FactoryAuditState.Partial, "Original admitted source refresh or model outcome remains unconfirmed; no completed closure.")
                    { Records = [record] };
        }
        foreach (var native in input.Where(r => r.Identity.Store == AuditStoreKind.Preview
            && r.Identity.Key.Contains("/docs/decision-", StringComparison.Ordinal)
            && !r.Identity.Key.Contains("/docs/decision-index-", StringComparison.Ordinal)))
        {
            using var body = JsonDocument.Parse(native.Content);
            var decision = PreviewEvaluationDocument.ReadNative(body.RootElement);
            foreach (var step in decision.SourceRefresh ?? [])
            {
                if (step.Request.Invocation != decision.DecisionScope?.InvocationId || step.Request.Admission != decision.Admission?.Id
                    || step.State != PreviewModelDispatchState.Confirmed || step.Receipt is not { } receipt
                    || receipt.RequestSha256 != FactoryModelsValidation.Hash(step.Request) || receipt.RequestId != step.Request.Preview.RequestId)
                    throw new FactoryAuditException(FactoryAuditState.Partial, "Original issued CODE advance remains unconfirmed; no completed stop or audit closure.");
                var retained = require(AuditStoreKind.Analysis, FactorySourceRefresh.ReceiptKey(receipt.RequestId));
                if (FactoryModelsValidation.Hash(Parse<SourceRefreshReceipt>(retained)) != FactoryModelsValidation.Hash(receipt))
                    throw new JsonException("Original CODE response differs.");
                var checkpoint = require(receipt.Progress.Store, receipt.Progress.Key);
                if (checkpoint.Identity != receipt.Progress || Parse<SourceRefreshProgress>(checkpoint).LastRequest != receipt.RequestId)
                    throw new JsonException("Original CODE checkpoint receipt differs.");
                link(native, retained);
                link(retained, checkpoint);
            }
        }
    }

    private static T Parse<T>(FactoryAuditRecord record) =>
        JsonSerializer.Deserialize<T>(record.Content, FactorySourceValidation.RecordJson) ?? throw new JsonException();
}
