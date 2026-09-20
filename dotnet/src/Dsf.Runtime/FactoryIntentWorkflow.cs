using System.Text;
using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class FactoryIntentWorkflow(FactorySourceAccess source, HttpClient http, TimeProvider clock, bool charterReview = false)
{
    private readonly SourceProgressStore store = new(source, http, "intent");
    private readonly FactorySourceInventory inventory = new(source, http, clock, charterReview);
    private readonly FactoryIntentAccess access = new(source, http, clock);
    private ConfigurationSetting? root;
    private IntentProgress progress = null!;
    private SourceInventoryResult acquisition = null!;
    private IReadOnlyDictionary<string, ConfigurationSetting> records = null!;
    private (SourcePathCoverage Path, SourceChunk Chunk)[] chunks = [];
    private readonly List<IntentChunkAnalysis> completed = [];
    private const string CurrentKey = "intent/current";
    private const string InitializedKey = "intent/initialized";
    private string StepKey(int index) => "intent/" + progress.Revision.ToString("N") + "/steps/" + index.ToString("D4");
    private int Completed => progress.AnalyzedChunks + progress.CheckedChunks;
    private static string Commitment(ConfigurationSetting record) => FactoryModelsValidation.Hash(new
        { record.Key, record.Value, ETag = record.ETag.ToString() });

    internal async Task<IntentResult> ReviewCharterAsync(CancellationToken token)
    {
        var result = await RunAsync(new(), true, token);
        if (result.State != IntentState.DraftReady) return result;
        return result with { Chunks = completed.ToArray(), NextPage = null,
            Limitations = result.Limitations.Concat(completed.SelectMany(c => c.Candidate?.Limitations ?? c.Verification?.Limitations ?? []))
                .Distinct().ToArray(), ExcludedPaths = acquisition.Paths.Where(p => p.Result?.State == SourcePathState.Excluded).ToArray(),
            NextExclusionPage = null };
    }

    internal async Task<IntentResult> RevalidateCharterReviewAsync(IntentResult review, CancellationToken token)
    {
        await access.CheckAsync(token);
        await FenceAsync(token);
        return review with { ModelAuthority = access.CurrentAuthority };
    }

    internal Task<CharterSource> ReadReviewedCharterAsync(CancellationToken token) =>
        inventory.ReadCharterAsync(new(progress.Snapshot, progress.SourceIntegritySha256), token);

    internal async Task<(SourceChunkReference Source, string Content, IReadOnlyList<SourceChunkReference> Dependencies)>
        ReadReviewedChunkAsync(int index, CancellationToken token)
    {
        if (progress.State != IntentState.DraftReady || index < 0 || index >= chunks.Length)
            throw new IntentException(IntentState.Blocked, "CompleteCheckedAnalysisRequiredForCompatibility");
        var (path, chunk) = chunks[index];
        using var materialized = await inventory.MaterializeAsync(Reference(chunk), token);
        var (content, dependencies) = await ReadContentAsync(path, chunk, materialized, token);
        await FenceAsync(token);
        return (Reference(chunk), content, dependencies);
    }

    internal async Task<IntentResult> RunAsync(IntentRequest request, bool status, CancellationToken token)
    {
        if (request.Page is < 0 or >= FactoryIntentLimits.MaximumPage
            || status && request.NewAnalysis) throw new IntentException(IntentState.Blocked, "InvalidIntentRequest");
        await access.CheckAsync(token);
        SourceInventoryReference? analyzed = null;
        if (charterReview)
        {
            records = await store.ReadNamespaceAsync(token);
            root = records.GetValueOrDefault(CurrentKey);
            if (root is not null)
            {
                progress = SourceProgressStore.Parse<IntentProgress>(root);
                ValidateProgress();
                analyzed = new(progress.Snapshot, progress.SourceIntegritySha256);
            }
        }
        acquisition = await inventory.ReadAnalysisInventoryAsync(token, analyzed);
        if (acquisition.State != SourceState.Acquired)
            return new(acquisition.State == SourceState.Partial ? IntentState.Blocked : Map(acquisition.State),
                acquisition.Detail, null, acquisition.Progress, null, [], [], [], null);
        chunks = acquisition.Paths.SelectMany(path => path.Chunks.Select(chunk => (path, chunk))).ToArray();
        if (chunks.Length is < 1 or > FactoryIntentLimits.MaximumChunks)
            return new(IntentState.Partial, "AnalysisChunkLimit; no source silently omitted.", null, acquisition.Progress, null, [], [], [], null);
        if (!charterReview)
        {
            records = await store.ReadNamespaceAsync(token);
            root = records.GetValueOrDefault(CurrentKey);
        }
        if (root is null)
        {
            if (records.Count != 0) throw new IntentException(IntentState.Unknown, "MissingAuthoritativeAnalysisCheckpoint");
            if (status) return new(IntentState.Partial, "AnalysisNotStarted", null, acquisition.Progress, null, [], [], [], null);
            progress = NewProgress(1);
            await access.CheckAsync(token);
            await store.WriteAsync(InitializedKey, progress, null, token);
            await access.CheckAsync(token);
            root = await store.WriteAsync(CurrentKey, progress, null, token);
            records = await store.ReadNamespaceAsync(token);
        }
        else progress = SourceProgressStore.Parse<IntentProgress>(root);
        ValidateProgress();
        if (request.NewAnalysis && progress.PendingSelection is null)
        {
            if (progress.Dispatch is not null) throw new IntentException(IntentState.Unknown, "UnresolvedModelDispatch; no replacement analysis.");
            if (progress.RevisionNumber >= FactoryIntentLimits.MaximumRevisions)
                throw new IntentException(IntentState.Partial, "AnalysisRevisionLimit");
            progress = progress with { PendingSelection = new(Guid.NewGuid(), acquisition.Progress!.Snapshot,
                acquisition.Progress.IntegritySha256!, access.Models.ApprovalFingerprint, chunks.Length) };
            await SaveAsync(token);
        }
        if (progress.PendingSelection is { } selection)
        {
            if (selection.Snapshot != acquisition.Progress!.Snapshot || selection.SourceIntegritySha256 != acquisition.Progress.IntegritySha256
                || selection.ModelConfigurationRevision != access.Models.ApprovalFingerprint || selection.AvailableChunks != chunks.Length)
                throw new IntentException(IntentState.Blocked, "PendingAnalysisSelectionChanged; no new selection or model dispatch.");
            if (status) return new(IntentState.Partial, "AnalysisSelectionPending; repeat infer to reconcile the recorded revision.",
                progress, acquisition.Progress, null, [], [], [], null);
            await WriteImmutableAsync("intent/history/" + progress.RevisionNumber.ToString("D2"), progress, token);
            progress = new(FactoryIntentLimits.Protocol, selection.Revision, progress.RevisionNumber + 1, selection.Snapshot,
                selection.SourceIntegritySha256, selection.ModelConfigurationRevision, selection.AvailableChunks);
            await SaveAsync(token);
            records = await store.ReadNamespaceAsync(token);
        }
        if (progress.Snapshot != acquisition.Progress!.Snapshot || progress.SourceIntegritySha256 != acquisition.Progress.IntegritySha256
            || progress.ModelConfigurationRevision != access.Models.ApprovalFingerprint || progress.AvailableChunks != chunks.Length)
            throw new IntentException(IntentState.Blocked, "IncompatiblePinnedAnalysis; review source/model changes and explicitly select new analysis.");
        ReadChain();
        if (progress.Dispatch is not null)
        {
            var receipt = records.GetValueOrDefault(StepKey(Completed));
            if (receipt is null) return await ReportAsync(request.Page, IntentState.Unknown, "ModelDispatchUnconfirmed; no automatic retry or lease takeover.", token);
            var recovered = SourceProgressStore.Parse<IntentChunkAnalysis>(receipt);
            ValidateReceipt(recovered, progress.Dispatch, progress.ChainSha256);
            if (status) return await ReportAsync(request.Page, IntentState.Partial, "ConfirmedReceiptPendingCheckpoint; repeat infer to reconcile without model replay.", token);
            await ConfirmAsync(receipt, recovered, token);
        }
        if (status || Completed == chunks.Length * 2) return await ReportAsync(request.Page, null, null, token);
        var modelResponseReceived = false;
        var validatingModelOutput = false;
        try
        {
            if (progress.ModelAttempts >= 2 * chunks.Length + FactoryIntentLimits.MaximumAdditionalAttempts)
                throw new IntentException(IntentState.Partial, "AnalysisAttemptLimit");
            var checking = progress.AnalyzedChunks == chunks.Length;
            var index = checking ? progress.CheckedChunks : progress.AnalyzedChunks;
            var (path, chunk) = chunks[index];
            var previous = completed.LastOrDefault(c => c.Candidate is not null)?.Candidate;
            using var materialized = await inventory.MaterializeAsync(Reference(chunk), token);
            var (content, dependencies) = await ReadContentAsync(path, chunk, materialized, token);
            var input = FactoryIntentPrompts.Build(path, chunk, content, previous, checking);
            if (input.Length > 32768) throw new IntentException(IntentState.Partial, "AnalysisPromptLimit");
            var dispatch = new IntentDispatch(Guid.NewGuid(), index, checking, FactoryModelsValidation.Hash(input), access.Capability);
            progress = progress with { Dispatch = dispatch, State = IntentState.Partial, Reason = "ModelDispatched",
                ModelAttempts = progress.ModelAttempts + 1, ModelOutcome = IntentModelOutcome.Dispatched };
            await SaveAsync(token);
            var response = await access.CompleteAsync(input, token);
            modelResponseReceived = true;
            await CheckSourceAsync(token);
            IntentCandidate? candidate = null;
            IntentVerification? verification = null;
            // Only acknowledged semantic-output errors may close a dispatched result without its receipt.
            validatingModelOutput = true;
            if (checking)
            {
                verification = FactoryIntentPrompts.Parse<IntentVerification>(response.Text);
                FactoryIntentPrompts.Validate(verification, previous!, content);
            }
            else
            {
                candidate = FactoryIntentPrompts.Parse<IntentCandidate>(response.Text);
                FactoryIntentPrompts.Validate(candidate, chunk, content, previous);
            }
            validatingModelOutput = false;
            var result = new IntentChunkAnalysis(progress.Revision, dispatch, Reference(chunk), progress.ChainSha256,
                response.Observation, candidate, verification, dependencies);
            var record = await WriteImmutableAsync(StepKey(Completed), result, token);
            await ConfirmAsync(record, result, token);
        }
        catch (IntentException e) when (modelResponseReceived && !validatingModelOutput)
        {
            throw new IntentException(IntentState.Unknown, "AcceptedModelResultCloseoutUnconfirmed; " + e.Message);
        }
        catch (IntentException e) when (e.State != IntentState.Unknown)
        {
            progress = progress with { State = e.State, Reason = e.Message, Dispatch = null,
                ModelOutcome = IntentModelOutcome.KnownFailure,
                LastFailure = progress.Dispatch is { } dispatched
                    ? new(dispatched, e.State, e.Message) { ModelRejection = e.ModelRejection, ModelRejectionProof = e.ModelRejectionProof } : progress.LastFailure };
            await SaveAsync(token);
        }
        catch (SourceAcquisitionException e) when (!modelResponseReceived && progress.Dispatch is null && e is not SourceProgressException)
        {
            progress = progress with { State = Map(e.State), Reason = e.Message };
            await SaveAsync(token);
        }
        return await ReportAsync(request.Page, null, null, token);
    }

    private IntentProgress NewProgress(int number) => new(FactoryIntentLimits.Protocol, Guid.NewGuid(), number,
        acquisition.Progress!.Snapshot, acquisition.Progress.IntegritySha256!, access.Models.ApprovalFingerprint, chunks.Length);

    private void ValidateProgress()
    {
        if (progress.Protocol != FactoryIntentLimits.Protocol || progress.Revision == Guid.Empty
            || progress.RevisionNumber is < 1 or > FactoryIntentLimits.MaximumRevisions || progress.Snapshot is null
            || progress.Snapshot.FactoryId != source.Manifest.Identity.FactoryId
            || !FactorySourceValidation.Hash(progress.SourceIntegritySha256) || !FactorySourceValidation.Hash(progress.ModelConfigurationRevision)
            || progress.AvailableChunks is < 1 or > FactoryIntentLimits.MaximumChunks
            || progress.AnalyzedChunks < 0 || progress.AnalyzedChunks > progress.AvailableChunks
            || progress.CheckedChunks < 0 || progress.CheckedChunks > progress.AvailableChunks
            || progress.CheckedChunks > 0 && progress.AnalyzedChunks != progress.AvailableChunks || !Enum.IsDefined(progress.State)
            || !Enum.IsDefined(progress.ModelOutcome) || progress.ModelAttempts < Completed
            || progress.ModelAttempts > 2 * progress.AvailableChunks + FactoryIntentLimits.MaximumAdditionalAttempts
            || (Completed == 0) != (progress.ChainSha256 is null)
            || progress.State == IntentState.DraftReady && progress.CheckedChunks != progress.AvailableChunks
            || progress.PendingSelection is { } selection && (selection.Revision == Guid.Empty || selection.Revision == progress.Revision
                || selection.Snapshot.FactoryId != source.Manifest.Identity.FactoryId || progress.Dispatch is not null
                || progress.RevisionNumber >= FactoryIntentLimits.MaximumRevisions)
            || !records.ContainsKey(InitializedKey))
            throw new IntentException(IntentState.Unknown, "InvalidAnalysisProgress");
        var initialized = SourceProgressStore.Parse<IntentProgress>(records[InitializedKey]);
        if (initialized.Protocol != FactoryIntentLimits.Protocol || initialized.RevisionNumber != 1
            || initialized.Snapshot.FactoryId != source.Manifest.Identity.FactoryId
            || progress.RevisionNumber == 1 && initialized.Revision != progress.Revision)
            throw new IntentException(IntentState.Unknown, "InvalidAnalysisInitialization");
        var next = progress;
        var revisions = new HashSet<Guid> { progress.Revision };
        for (var i = progress.RevisionNumber - 1; i >= 1; i--)
        {
            if (!records.TryGetValue("intent/history/" + i.ToString("D2"), out var historical))
                throw new IntentException(IntentState.Unknown, "MissingAnalysisHistory");
            var prior = SourceProgressStore.Parse<IntentProgress>(historical);
            if (prior.Protocol != FactoryIntentLimits.Protocol || prior.RevisionNumber != i || prior.Dispatch is not null
                || !revisions.Add(prior.Revision) || prior.Snapshot.FactoryId != source.Manifest.Identity.FactoryId
                || prior.PendingSelection is not { } selected || selected.Revision != next.Revision
                || selected.Snapshot != next.Snapshot || selected.SourceIntegritySha256 != next.SourceIntegritySha256
                || selected.ModelConfigurationRevision != next.ModelConfigurationRevision || selected.AvailableChunks != next.AvailableChunks
                || i == 1 && prior.Revision != initialized.Revision)
                throw new IntentException(IntentState.Unknown, "InconsistentAnalysisSelectionHistory");
            next = prior;
        }
        var pendingReceipt = progress.Dispatch is not null && records.ContainsKey(StepKey(Completed));
        if (records.Keys.Count(key => key.StartsWith("intent/" + progress.Revision.ToString("N") + "/steps/", StringComparison.Ordinal))
                != Completed + (pendingReceipt ? 1 : 0)
            || records.Keys.Count(key => key.StartsWith("intent/history/", StringComparison.Ordinal))
                != progress.RevisionNumber - 1 + (progress.PendingSelection is not null
                    && records.ContainsKey("intent/history/" + progress.RevisionNumber.ToString("D2")) ? 1 : 0))
            throw new IntentException(IntentState.Unknown, "AnalysisAccountingMismatch");
    }

    private void ReadChain()
    {
        string? chain = null;
        for (var i = 0; i < Completed; i++)
        {
            var record = records.GetValueOrDefault(StepKey(i)) ?? throw new IntentException(IntentState.Unknown, "MissingChunkAnalysis");
            var step = SourceProgressStore.Parse<IntentChunkAnalysis>(record);
            ValidateReceipt(step, step.Dispatch, chain);
            if (step.Dispatch.Index != i % chunks.Length || step.Dispatch.Checking != (i >= chunks.Length))
                throw new IntentException(IntentState.Unknown, "InconsistentAnalysisCoverage");
            chain = Commitment(record);
            completed.Add(step);
        }
        if (chain != progress.ChainSha256) throw new IntentException(IntentState.Unknown, "AnalysisIntegrityMismatch");
    }

    private void ValidateReceipt(IntentChunkAnalysis result, IntentDispatch dispatch, string? previous)
    {
        if (result.Revision != progress.Revision || result.Dispatch != dispatch || dispatch.Id == Guid.Empty
            || dispatch.Index < 0 || dispatch.Index >= chunks.Length
            || dispatch.Capability.ConfigurationRevision != progress.ModelConfigurationRevision
            || result.Source != Reference(chunks[dispatch.Index].Chunk) || result.PreviousSha256 != previous
            || result.Model.ConfigurationRevision != progress.ModelConfigurationRevision || result.Model.Route != "synthesis"
            || result.Model.State != ModelCapabilityState.Verified
            || result.Dependencies is null || result.Dependencies.Count is < 1 or > 2 || result.Dependencies[0] != result.Source
            || result.Dependencies.Any(d => !chunks.Any(c => Reference(c.Chunk) == d))
            || dispatch.Checking != (result.Verification is not null) || dispatch.Checking == (result.Candidate is not null))
            throw new IntentException(IntentState.Unknown, "InvalidChunkAnalysisReceipt");
    }

    private async Task ConfirmAsync(ConfigurationSetting record, IntentChunkAnalysis result, CancellationToken token)
    {
        ValidateReceipt(result, progress.Dispatch!, progress.ChainSha256);
        completed.Add(result);
        progress = progress with { AnalyzedChunks = progress.AnalyzedChunks + (result.Dispatch.Checking ? 0 : 1),
            CheckedChunks = progress.CheckedChunks + (result.Dispatch.Checking ? 1 : 0),
            ChainSha256 = Commitment(record), Dispatch = null, State = IntentState.Partial,
            ModelOutcome = IntentModelOutcome.Confirmed,
            Reason = result.Dispatch.Checking ? "CheckingOriginals" : "AnalyzingSource" };
        if (progress.CheckedChunks == chunks.Length)
        {
            var review = Review();
            progress = progress with { State = review.State, Reason = review.Reason };
        }
        await SaveAsync(token);
    }

    private (IntentState State, string Reason, GroundedCharterDraft? Draft, IReadOnlyList<IntentReviewClaim> Claims) Review()
    {
        var candidate = completed.LastOrDefault(c => c.Candidate is not null)?.Candidate;
        if (candidate is null) return (IntentState.Partial, "PendingAnalysis", null, []);
        var claims = candidate.Claims.Select((claim, index) =>
        {
            var originals = claim.Evidence.Select(e => chunks.SingleOrDefault(c => c.Chunk.PathIndex == e.PathIndex && c.Chunk.Index == e.ChunkIndex))
                .Where(c => c.Chunk is not null).Select(c => new IntentOriginalReference(Reference(c.Chunk), c.Path.Node.Path,
                    "https://github.com/" + progress.Snapshot.Repository + "/blob/" + progress.Snapshot.Commit + "/"
                    + string.Join("/", c.Path.Node.Path.Split('/').Select(Uri.EscapeDataString)), c.Chunk.ByteOffset, c.Chunk.ByteLength)).ToArray();
            var checks = completed.Where(c => c.Verification is not null).Select(c => (c.Source,
                Check: c.Verification!.Checks.Single(check => check.Claim == index))).ToArray();
            var conflict = checks.Any(c => c.Check.Verdict == IntentSupportVerdict.Contradicts);
            var supported = progress.CheckedChunks == chunks.Length && originals.Length == claim.Evidence.Count
                && originals.All(original => checks.Any(c => c.Source == original.Chunk && c.Check.Verdict == IntentSupportVerdict.Supports))
                && !checks.Any(c => c.Check.Verdict == IntentSupportVerdict.Unsupported) && !conflict;
            return new IntentReviewClaim(index, claim, supported, conflict, originals);
        }).ToArray();
        GroundedCharterDraft? draft = null;
        try
        {
            var purpose = candidate.Claims.Where(c => c.Field == IntentClaimField.Purpose).ToArray();
            var users = candidate.Claims.Where(c => c.Field == IntentClaimField.Users).ToArray();
            if (purpose.Length != 1 || users.Length != 1) throw new CharterParseException(["One substantive purpose and intended-user statement required."]);
            draft = new(source.Manifest.Identity.ProductKey, purpose[0].Text, users[0].Text,
                candidate.Claims.Where(c => c.Field == IntentClaimField.Goal).Select(c => c.Text).ToArray(),
                new(candidate.NonGoals, candidate.Claims.Where(c => c.Field == IntentClaimField.NonGoal).Select(c => c.Text).ToArray()),
                new(candidate.SuccessMeasures, candidate.Claims.Where(c => c.Field == IntentClaimField.SuccessMeasure).Select(c => c.Text).ToArray()));
            draft.Validate();
        }
        catch (CharterParseException) { return (IntentState.Blocked, "SubstantiveCoreIntentRequired", null, claims); }
        return progress.CheckedChunks != chunks.Length ? (IntentState.Partial, "OriginalSourceChecksPending", draft, claims)
            : claims.Any(c => c.Conflicted) ? (IntentState.Blocked, "ConflictingIntent", draft, claims)
            : claims.Any(c => !c.Grounded) ? (IntentState.Blocked, "UnsupportedMaterialClaim", draft, claims)
            : (IntentState.DraftReady, "GroundedDraftForOwnerReviewOnly", draft, claims);
    }

    private async Task<IntentResult> ReportAsync(int page, IntentState? state, string? reason, CancellationToken token)
    {
        var review = Review();
        await CheckSourceAsync(token);
        await access.CheckAsync(token);
        await FenceAsync(token);
        var limitations = new[] { "Source analysis is not deployed behavior, adopted intent, council readiness or activation.",
            "Model semantic judgments are fallible; owner review remains required. No numerical accuracy is asserted." }
            .Concat(completed.LastOrDefault(c => c.Candidate is not null)?.Candidate?.Limitations ?? [])
            .Append("Excluded paths are not analyzed; all per-chunk findings, limitations and counterevidence remain paged, not silently discarded.")
            .Distinct().ToArray();
        var offset = page * FactoryIntentLimits.PageSize;
        var excluded = acquisition.Paths.Where(p => p.Result?.State == SourcePathState.Excluded).ToArray();
        return new(state ?? progress.State, reason ?? progress.Reason, progress, acquisition.Progress, review.Draft, review.Claims,
            completed.Skip(offset).Take(FactoryIntentLimits.PageSize).ToArray(), limitations, offset + FactoryIntentLimits.PageSize < completed.Count ? page + 1 : null)
        {
            ModelAuthority = access.CurrentAuthority,
            ExcludedPaths = excluded.Skip(offset).Take(FactoryIntentLimits.PageSize).ToArray(),
            NextExclusionPage = offset + FactoryIntentLimits.PageSize < excluded.Length ? page + 1 : null,
        };
    }

    private async Task<ConfigurationSetting> WriteImmutableAsync<T>(string key, T value, CancellationToken token)
    {
        records = await store.ReadNamespaceAsync(token);
        var serialized = JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
        var current = await store.ReadAsync(key, token);
        if (current is not null)
        {
            if (current.Value != serialized) throw new IntentException(IntentState.Unknown, "IncompatibleAnalysisRecord");
            return current;
        }
        var bytes = SourceProgressStore.RecordSize(key, serialized);
        var rootReserve = FactorySourceValidation.RecordBytes - SourceProgressStore.RecordSize(CurrentKey, root!.Value);
        if (bytes > FactorySourceValidation.RecordBytes || records.Count >= FactorySourceValidation.MaximumRecords
            || records.Sum(p => (long)SourceProgressStore.RecordSize(p.Key, p.Value.Value)) + bytes + rootReserve > FactorySourceValidation.MaximumMetadataBytes)
            throw new IntentException(IntentState.Partial, "AnalysisMetadataLimit");
        await access.CheckAsync(token);
        await FenceAsync(token);
        return await store.WriteAsync(key, value, null, token);
    }
    private async Task SaveAsync(CancellationToken token)
    {
        await access.CheckAsync(token);
        await FenceAsync(token);
        root = await store.WriteAsync(CurrentKey, progress, root!.ETag, token);
    }
    private async Task CheckSourceAsync(CancellationToken token)
    {
        var current = await inventory.ReadAnalysisInventoryAsync(token,
            charterReview ? new(progress.Snapshot, progress.SourceIntegritySha256) : null);
        if (current.State != SourceState.Acquired)
            throw new SourceAcquisitionException(current.State, current.Detail);
        if (current.Progress?.Snapshot != progress.Snapshot
            || current.Progress.IntegritySha256 != progress.SourceIntegritySha256)
            throw new IntentException(IntentState.Blocked, "AnalysisSourceChanged");
    }
    private async Task<(string Content, IReadOnlyList<SourceChunkReference> Dependencies)> ReadContentAsync(
        SourcePathCoverage path, SourceChunk chunk, SourceMaterializedChunk materialized, CancellationToken token)
    {
        var bytes = materialized.Content;
        var start = 0;
        // A scalar starting in the previous range is processed there, with a dependency on this range.
        if (chunk.Index > 0)
            while (start < bytes.Length && (bytes.Span[start] & 0xc0) == 0x80) start++;
        if (start > 3) throw new IntentException(IntentState.Failed, "InvalidUtf8ChunkBoundary");
        var last = bytes.Length - 1;
        while (last > start && (bytes.Span[last] & 0xc0) == 0x80) last--;
        var needsNext = last >= start && Rune.DecodeFromUtf8(bytes.Span[last..], out _, out _) == System.Buffers.OperationStatus.NeedMoreData;
        var encoding = new UTF8Encoding(false, true);
        if (!needsNext) return (encoding.GetString(bytes.Span[start..]), [Reference(chunk)]);
        var next = path.Chunks.SingleOrDefault(c => c.Index == chunk.Index + 1)
            ?? throw new IntentException(IntentState.Failed, "MissingUtf8Continuation");
        using var neighbor = await inventory.MaterializeAsync(Reference(next), token);
        var scalarBytes = new byte[4];
        try
        {
            bytes.Span[last..].CopyTo(scalarBytes);
            neighbor.Content.Span[..Math.Min(neighbor.Content.Length, 4 - (bytes.Length - last))]
                .CopyTo(scalarBytes.AsSpan(bytes.Length - last));
            if (Rune.DecodeFromUtf8(scalarBytes, out var scalar, out _) != System.Buffers.OperationStatus.Done)
                throw new IntentException(IntentState.Failed, "InvalidUtf8Continuation");
            return (encoding.GetString(bytes.Span[start..last]) + scalar, [Reference(chunk), Reference(next)]);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(scalarBytes); }
    }
    private async Task FenceAsync(CancellationToken token)
    {
        var current = await store.ReadAsync(CurrentKey, token);
        if (current?.ETag != root!.ETag || current.Value != root.Value)
            throw new IntentException(IntentState.Unknown, "AnalysisCoordinationChanged");
    }
    private static SourceChunkReference Reference(SourceChunk chunk) => new(chunk.SnapshotId, chunk.PathIndex, chunk.Index, chunk.Sha256);
    internal static IntentState Map(SourceState state) => state switch
    {
        SourceState.Blocked => IntentState.Blocked, SourceState.Failed => IntentState.Failed,
        SourceState.Canceled => IntentState.Canceled, SourceState.Unknown => IntentState.Unknown, _ => IntentState.Partial,
    };
}
