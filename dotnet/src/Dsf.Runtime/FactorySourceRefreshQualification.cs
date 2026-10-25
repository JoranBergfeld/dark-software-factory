using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class FactorySourceRefreshWorkflow
{
    internal async Task<SourceRefreshQualification> QualifyAsync(SourceCharterContextResult context, CancellationToken token)
    {
        var observed = await InspectAsync(Guid.NewGuid(), token);
        if (observed.State != SourceRefreshState.Compatible || root is null)
            throw Denied(observed.State, "CompleteCurrentSourceCompatibilityRequired:" + observed.Reason);
        var originalRoot = root;
        var proofRequest = progress.LastRequest ?? throw new JsonException();
        var checkpoint = await RequiredAsync(FactorySourceRefresh.CheckpointKey(proofRequest));
        var receiptRecord = await RequiredAsync(FactorySourceRefresh.ReceiptKey(proofRequest));
        var receipt = SourceProgressStore.Parse<SourceRefreshReceipt>(receiptRecord);
        if (checkpoint.Value != root.Value || receipt.RequestId != proofRequest || receipt.State != SourceRefreshState.Compatible
            || receipt.Progress != Identity(checkpoint))
            throw Denied(SourceRefreshState.Unknown, "OriginalCompatibilityCloseoutUnconfirmed");
        var qualified = new SourceRefreshQualification(1, progress, Identity(checkpoint), Identity(receiptRecord));
        FactorySourceRefreshValidation.RequireCurrent(context with { Refresh = qualified }, source.Manifest.Identity.ProductKey);
        var analysis = context.Current!.Analysis;
        var targets = FactorySourceCompatibility.Targets(analysis, adoption.Original.Evidence.Analysis);
        await ReadCompatibilityChainAsync(targets, token);
        var originals = analysis.Chunks.Where(c => !c.Dispatch.Checking).OrderBy(c => c.Dispatch.Index).ToArray();
        for (var index = 0; index < progress.Chunks; index++)
        {
            var record = await RequiredAsync(FactorySourceRefresh.StepKey(progress.Id, index));
            var step = SourceProgressStore.Parse<SourceCompatibilityStep>(record);
            var capability = FactoryModelsValidation.ReadRetained(source.Manifest, source.Values, step.Dispatch.Capability, clock.GetUtcNow());
            if (step.Source != originals[index].Source || !step.Dependencies.SequenceEqual(originals[index].Dependencies)
                || step.Dispatch.Id == Guid.Empty || !FactorySourceValidation.Hash(step.Dispatch.InputSha256)
                || step.Dispatch.Capability.ConfigurationRevision != progress.Binding.ModelRevision
                || capability.ApprovalFingerprint != progress.Binding.ModelRevision
                || step.Model.ConfigurationRevision != progress.Binding.ModelRevision || step.Model.Route != "synthesis"
                || step.Model.State != ModelCapabilityState.Verified || step.Model.Reason != ModelCapabilityReason.None
                || !FactorySourceValidation.Hash(step.Model.OutputSha256) || step.Model.OutputCharacters <= 0)
                throw Denied(SourceRefreshState.Unknown, "OriginalCompatibilityModelOrDependenciesChanged");
        }
        var retention = SourceProgressStore.Parse<AnalysisEvidenceClaim>(
            await RequiredAsync(FactoryAuditRetention.EvidenceKey(progress.AnalysisRevision!.Value)));
        FactoryAuditRetention.RequireEvidence(retention, source.Manifest.Identity.FactoryId, progress.AnalysisRevision.Value,
            progress.AnalysisChain!, 2 * progress.Chunks);
        if (retention.Kind != AnalysisRetentionKind.Protected)
            throw Denied(SourceRefreshState.Unknown, "OriginalCompatibilityRetentionUnconfirmed");
        await ReadBasisAsync(token);
        await github.CheckSnapshotAsync(progress.Binding.Snapshot, token);
        if (!SameGovernance(progress.Binding, binding)
            || await store.ReadAsync(FactorySourceRefresh.CurrentKey, token) is not { } current
            || current.ETag != originalRoot.ETag || current.Value != originalRoot.Value)
            throw Denied(SourceRefreshState.Unknown, "SourceCompatibilityChangedDuringQualification");
        return qualified;

        async Task<ConfigurationSetting> RequiredAsync(string key) =>
            await store.ReadAsync(key, token) ?? throw Denied(SourceRefreshState.Unknown, "OriginalSourceCompatibilityRecordMissing");
    }
}
