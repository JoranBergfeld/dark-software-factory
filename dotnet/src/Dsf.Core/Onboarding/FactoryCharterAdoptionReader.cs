using System.Text;
using System.Text.Json;
using Dsf.Core.Charters;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record CharterEvidenceRecord(string Key, string? Label, string Value, string ETag, string? ContentType);

public interface IFactoryCharterEvidenceReader
{
    Task<CharterEvidenceRecord?> ReadAsync(string key, CancellationToken cancellationToken);
    Task RequirePristineAsync(string prefix, CancellationToken cancellationToken);
}

/// <summary>Verified retained adoption, not proof of current repository, source or model authority.</summary>
public sealed record RetainedCharterAdoption(
    CharterAdoption Adoption, CharterPublicationPayload Original, string Revision, string OperationRevision);

public sealed record SourceCharterContextResult(
    FactoryCharterState State, string Detail, RetainedCharterAdoption? Adoption, CharterEvidence? Current);

public sealed class CharterAdoptionEvidenceException(string detail, CharterAdoption? historical = null) : Exception(detail)
{
    public CharterAdoption? Historical { get; } = historical;
}

/// <summary>Reads the bounded immutable adoption chain and fences its current protected pointers.</summary>
public static class FactoryCharterAdoptionReader
{
    public const int PayloadChunkCharacters = 3000;
    private static readonly JsonSerializerOptions Json = new(FactorySourceValidation.RecordJson)
    { IgnoreReadOnlyProperties = true };

    public static string Fingerprint(CharterReview review, CharterMergeReceipt? merge, Guid? operation) =>
        FactoryModelsValidation.Hash(new { Review = review.Fingerprint, operation, merge?.PullRequestId, merge?.PullRequestNodeId,
            merge?.Number, merge?.Commit, merge?.HumanId, merge?.MergedAt });

    public static CharterAdoption ReadRecord(CharterEvidenceRecord record, Guid factoryId)
    {
        CheckRecord(record, ProductConfigurationKeys.CharterAdopted);
        var adoption = Parse<CharterAdoption>(record.Value);
        if (factoryId == Guid.Empty || adoption.Protocol != 1 || adoption.FactoryId != factoryId || adoption.Revision == Guid.Empty
            || adoption.Sequence is < 1 or > FactoryCharterLimits.MaximumRevisions || !FactorySourceValidation.Hash(adoption.ReviewFingerprint)
            || !FactorySourceValidation.GitId(adoption.CharterBlob) || !FactorySourceValidation.GitId(adoption.SourceCommit)
            || !FactorySourceValidation.GitId(adoption.SourceTree) || !FactorySourceValidation.Hash(adoption.PayloadSha256)
            || adoption.Route == "HumanMerged" && (adoption.PublicationOperation is null || adoption.Merge is null)
            || adoption.Route is not ("UnchangedExisting" or "HumanMerged")
            || adoption.PayloadRecords is < 1 or > FactoryCharterLimits.MaximumPayloadRecords)
            throw new JsonException("Invalid retained adoption.");
        return adoption;
    }

    /// <summary>Checks reasoning-data consistency after the source workflow's current native/inventory checks.</summary>
    public static void RequireCurrent(CharterAdoption adoption, CharterEvidence evidence, string product)
    {
        var analysis = evidence.Analysis;
        var source = evidence.Source;
        if (analysis.State != IntentState.DraftReady || analysis.Governs || analysis.Draft is null
            || analysis.Progress is not { Dispatch: null, PendingSelection: null, State: IntentState.DraftReady,
                ModelOutcome: IntentModelOutcome.Confirmed } progress
            || progress.Revision == Guid.Empty || progress.Snapshot.FactoryId != adoption.FactoryId
            || progress.Snapshot.RepositoryId != source.Snapshot.RepositoryId
            || progress.AvailableChunks is < 1 or > FactoryIntentLimits.MaximumChunks || progress.AnalyzedChunks != progress.AvailableChunks
            || progress.CheckedChunks != progress.AvailableChunks || analysis.ModelAuthority is null
            || analysis.Acquisition is not { State: SourceState.Acquired, CompleteRootTree: true, NecessaryGaps: 0 } acquisition
            || acquisition.Snapshot != progress.Snapshot || acquisition.IntegritySha256 != progress.SourceIntegritySha256
            || analysis.Claims.Count == 0 || analysis.Claims.Any(c => c is null || !c.Grounded || c.Conflicted)
            || analysis.Chunks.Count != 2 * progress.AvailableChunks
            || analysis.Chunks.Any(c => c is null || c.Revision != progress.Revision || c.Source.SnapshotId != progress.Snapshot.Id
                || (c.Dispatch.Checking ? c.Verification?.Complete != true : c.Candidate?.Complete != true))
            || analysis.Chunks.Count(c => c.Dispatch.Checking) != progress.AvailableChunks
            || analysis.Chunks.Select(c => (c.Dispatch.Checking, c.Dispatch.Index)).Distinct().Count() != analysis.Chunks.Count
            || analysis.Limitations.Any(string.IsNullOrWhiteSpace) || analysis.NextPage is not null
            || analysis.NextExclusionPage is not null || source.Snapshot.FactoryId != adoption.FactoryId
            || source.Content is null || source.Blob != adoption.CharterBlob
            || CharterMarkdown.GitBlobSha(source.Content) != adoption.CharterBlob
            || FactoryModelsValidation.Hash(source.Content) != adoption.CharterSha256
            || progress.ModelConfigurationRevision != adoption.ModelRevision)
            throw new JsonException("Current complete analysis and exact adopted charter/model required.");
        analysis.Draft.Validate();
        _ = OnboardingCharterMarkdown.Parse(source.Content, product);
        if (source.Snapshot.Id != progress.Snapshot.Id && source.Content != OnboardingCharterMarkdown.Render(analysis.Draft))
            throw new JsonException("Current charter differs from the reconciled analyzed intent.");
    }

    public static async Task<RetainedCharterAdoption?> ReadAsync(
        Guid factoryId, IFactoryCharterEvidenceReader reader, CancellationToken token)
    {
        CharterAdoption? adoption = null;
        try
        {
            var root = await reader.ReadAsync(ProductConfigurationKeys.CharterAdopted, token);
            var coordinator = await reader.ReadAsync(ProductConfigurationKeys.CharterAdoptionOperation, token);
            if (root is null && coordinator is null)
            {
                await reader.RequirePristineAsync(ProductConfigurationKeys.CharterAdoptionsPrefix, token);
                return null;
            }
            if (root is null) throw new JsonException();
            adoption = ReadRecord(root, factoryId);
            if (coordinator is null) throw new JsonException();
            CheckRecord(coordinator, ProductConfigurationKeys.CharterAdoptionOperation);
            var operation = Parse<CharterAdoptionOperation>(coordinator.Value);
            if (operation.State != CharterEffectState.Confirmed || !Same(operation.Adoption, adoption))
                throw new JsonException();
            var payload = new StringBuilder();
            for (var index = 0; index < adoption.PayloadRecords; index++)
            {
                var part = await RequiredAsync(ProductConfigurationKeys.CharterAdoptionPayload(adoption.Revision, index));
                var text = Parse<string>(part.Value);
                if (text.Length is < 1 or > PayloadChunkCharacters) throw new JsonException();
                payload.Append(text);
            }
            var receipt = await RequiredAsync(ProductConfigurationKeys.CharterAdoptionReceipt(adoption.Revision, "receipt"));
            var intended = await RequiredAsync(ProductConfigurationKeys.CharterAdoptionReceipt(adoption.Revision, "intent"));
            var original = Parse<CharterAdoptionOperation>(intended.Value);
            if (receipt.Value != JsonSerializer.Serialize(adoption, Json)
                || original.State != CharterEffectState.Intended || !Same(original.Adoption, adoption)
                || original.PreviousETag != operation.PreviousETag
                || FactoryModelsValidation.Hash(payload.ToString()) != adoption.PayloadSha256)
                throw new JsonException();
            var evidence = Parse<CharterPublicationPayload>(payload.ToString());
            var review = evidence.Review;
            if (review.FactoryId != adoption.FactoryId || review.AnalysisRevision != adoption.AnalysisRevision
                || review.AnalysisChain != adoption.AnalysisChain || review.AnalysisSource != adoption.AnalysisSource
                || review.SourceIntegrity != adoption.SourceIntegrity || review.ModelRevision != adoption.ModelRevision
                || review.Commit != adoption.SourceCommit || review.RootTree != adoption.SourceTree
                || review.AfterBlob != adoption.CharterBlob || review.OperatorId != adoption.OperatorId
                || FactoryModelsValidation.Hash(review.After) != adoption.CharterSha256
                || (adoption.Route == "UnchangedExisting" ? review.Fingerprint
                    : Fingerprint(review, adoption.Merge, adoption.PublicationOperation)) != adoption.ReviewFingerprint)
                throw new JsonException();
            await FenceAsync(root);
            await FenceAsync(coordinator);
            return new(adoption, evidence, root.ETag, coordinator.ETag);

            async Task<CharterEvidenceRecord> RequiredAsync(string key)
            {
                var record = await reader.ReadAsync(key, token) ?? throw new JsonException();
                CheckRecord(record, key);
                return record;
            }

            async Task FenceAsync(CharterEvidenceRecord expected)
            {
                if (await reader.ReadAsync(expected.Key, token) != expected) throw new JsonException();
            }
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw new CharterAdoptionEvidenceException(
                "Adoption evidence, operator, coordinator or current record revision unconfirmed; retained intent is history.", adoption);
        }
    }

    private static void CheckRecord(CharterEvidenceRecord record, string key)
    {
        if (record.Key != key || record.Label is not null || string.IsNullOrEmpty(record.ETag)
            || record.ContentType != FactoryCharterLimits.RecordContentType
            || Encoding.UTF8.GetByteCount(key + ProductConfigurationKeys.NoLabel + record.Value + record.ContentType) + 1024 > FactoryCharterLimits.RecordBytes)
            throw new JsonException("Invalid adoption record metadata.");
    }

    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new JsonException();
    private static bool Same(CharterAdoption first, CharterAdoption second) =>
        FactoryModelsValidation.Hash(first) == FactoryModelsValidation.Hash(second);
}
