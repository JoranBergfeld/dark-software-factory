using System.Text.Json;

namespace Dsf.Core.Onboarding;

/// <summary>Checks the complete native CODE qualification envelope, never grants governing authority.</summary>
public static class FactorySourceRefreshValidation
{
    public static void RequireCurrent(SourceCharterContextResult context, string product)
    {
        var retained = context.Adoption ?? throw new JsonException("Original human adoption required.");
        var evidence = context.Current ?? throw new JsonException("Complete current evidence required.");
        FactoryCharterAdoptionReader.RequireCurrent(retained.Adoption, evidence, product);
        var analysis = evidence.Analysis.Progress!;
        if (analysis.Revision == retained.Adoption.AnalysisRevision)
        {
            if (context.Refresh is not null) throw new JsonException("Original analysis has unexpected refresh qualification.");
            return;
        }
        var proof = context.Refresh ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked,
            "CompleteCurrentSourceCompatibilityRequired");
        var progress = proof.Progress;
        var binding = progress.Binding;
        if (proof.Protocol != 1 || progress.Protocol != 1 || progress.Id == Guid.Empty
            || progress.State != SourceRefreshState.Compatible || progress.Phase != SourceRefreshPhase.Complete
            || progress.Work is not null || progress.Dispatch is not null || progress.LastRequest is not { } request
            || progress.AnalysisRevision != analysis.Revision || progress.AnalysisChain != analysis.ChainSha256
            || progress.SourceIntegrity != analysis.SourceIntegritySha256 || progress.Chunks != analysis.AvailableChunks
            || progress.CheckedChunks != progress.Chunks || !FactorySourceValidation.Hash(progress.CompatibilityChain)
            || binding.Snapshot with { ObservedAt = analysis.Snapshot.ObservedAt } != analysis.Snapshot
            || binding.AdoptionRevision != retained.Adoption.Revision
            || binding.AdoptionETag != retained.Revision || binding.AdoptionOperationETag != retained.OperationRevision
            || binding.OriginalAnalysis != retained.Adoption.AnalysisRevision || binding.CharterSha256 != retained.Adoption.CharterSha256
            || binding.ModelRevision != retained.Adoption.ModelRevision || !FactorySourceValidation.Hash(binding.GovernanceRevision)
            || !progress.AddressedTargets.Order().SequenceEqual(Targets(evidence.Analysis, retained.Original.Evidence.Analysis).Order())
            || !Reference(proof.Checkpoint, FactorySourceRefresh.CheckpointKey(request))
            || !Reference(proof.Receipt, FactorySourceRefresh.ReceiptKey(request)))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "CompleteNativeSourceCompatibilityUnconfirmed");

        bool Reference(AuditRecordIdentity identity, string key) => identity.FactoryId == retained.Adoption.FactoryId
            && identity.Store == AuditStoreKind.Analysis && identity.Key == key && !string.IsNullOrEmpty(identity.NativeRevision)
            && FactorySourceValidation.Hash(identity.Sha256);
    }

    public static bool IsCompatibleSuccessor(SourceCharterContextResult original, SourceCharterContextResult current)
    {
        if (current.Refresh is null || original.Adoption is null || current.Adoption is null
            || original.Current?.Analysis.Progress?.Revision != original.Adoption.Adoption.AnalysisRevision
            || FactoryModelsValidation.Hash(original.Adoption) != FactoryModelsValidation.Hash(current.Adoption))
            return false;
        RequireCurrent(current, current.Current!.Analysis.Draft!.Product);
        return true;
    }

    public static string[] Targets(IntentResult current, IntentResult original) =>
        new[] { "Purpose", "Users", "Goals", "NonGoals", "SuccessMeasures" }
            .Concat(current.Claims.Select(c => "current:" + c.Index))
            .Concat(original.Claims.Select(c => "approved:" + c.Index)).ToArray();
}
