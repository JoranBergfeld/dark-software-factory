using System.Security.Cryptography;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class FactorySourceInventory(FactorySourceAccess access, HttpClient http, TimeProvider clock) : IFactorySourceMaterializer
{
    private readonly SourceProgressStore store = new(access, http);
    private readonly GitHubSourceSnapshot github = new(access, clock);
    private ConfigurationSetting? root;
    private SourceProgress? progress;
    private readonly Guid worker = Guid.NewGuid();
    private string RootKey => "source/" + access.Source.OperationId.ToString("N") + "/current";
    private string Key(string kind, int index) => "source/" + progress!.Snapshot.Id + "/" + kind + "/" + index.ToString("D8");
    private string ChunkKey(int path, int chunk) => Key("chunks", path) + "/" + chunk.ToString("D4");

    internal async Task<SourceInventoryResult> AdvanceAsync(SourceAcquireRequest request, CancellationToken token)
    {
        root = await store.ReadAsync(RootKey, token);
        if (root is null)
        {
            var snapshot = await github.PinAsync(token);
            progress = new(snapshot);
            await access.CheckAsync(token);
            root = await store.WriteAsync(RootKey, progress, null, token);
        }
        else progress = SourceProgressStore.Parse<SourceProgress>(root);
        ValidateProgress();
        if (progress.Worker is not null && progress.LeaseUntil > clock.GetUtcNow())
            return await ReportAsync(request.Page, "Another bounded worker holds acquisition.", token);
        SourceSnapshot? replacement = null;
        if (request.StartNewSnapshot)
        {
            var observed = await github.PinAsync(token);
            if (observed.Id != progress.Snapshot.Id)
            {
                if (progress.Snapshots >= 8) throw new SourceAcquisitionException(SourceState.Partial, "SnapshotHistoryLimit");
                replacement = observed;
            }
        }
        else await github.CheckSnapshotAsync(progress.Snapshot, token);
        progress = progress with { Worker = worker, LeaseUntil = clock.GetUtcNow() + FactorySourceValidation.LeaseBudget,
            State = SourceState.Partial, Reason = "Pending" };
        await SaveAsync(token);
        if (replacement is not null)
        {
            await WriteImmutableAsync(Key("history", 0), progress.Snapshot, token);
            progress = new(replacement) { Worker = worker, LeaseUntil = progress.LeaseUntil, Records = progress.Records,
                MetadataBytes = progress.MetadataBytes, Snapshots = progress.Snapshots + 1 };
            await SaveAsync(token);
        }
        await WriteImmutableAsync(Key("nodes", 0), new SourceNode(progress.Snapshot.Id, 0, "", "040000", progress.Snapshot.RootTree, null), token);
        var nodes = 0;
        var chunks = 0;
        var files = 0;
        try
        {
            while (progress.TreeCursor < progress.Nodes && nodes < FactorySourceValidation.MaximumNodesPerAdvance)
            {
                var parent = await ReadNodeAsync(progress.TreeCursor, token);
                if (parent.Mode == "040000")
                {
                    var children = await github.TreeAsync(progress.Snapshot, parent, token);
                    while (progress.ChildCursor < children.Count && nodes < FactorySourceValidation.MaximumNodesPerAdvance)
                    {
                        if (progress.Nodes >= FactorySourceValidation.MaximumNodes)
                            throw new SourceAcquisitionException(SourceState.Partial, "InventoryLimit");
                        var child = children[progress.ChildCursor] with { Index = progress.Nodes };
                        await WriteImmutableAsync(Key("nodes", child.Index), child, token);
                        progress = progress with { Nodes = progress.Nodes + 1, ChildCursor = progress.ChildCursor + 1,
                            Files = progress.Files + (child.Mode == "040000" ? 0 : 1) };
                        await SaveAsync(token);
                        nodes++;
                    }
                    if (progress.ChildCursor < children.Count) break;
                }
                progress = progress with { TreeCursor = progress.TreeCursor + 1, ChildCursor = 0 };
                await SaveAsync(token);
            }
            if (progress.TreeCursor == progress.Nodes)
            {
                progress = progress with { CompleteRootTree = true };
                await SaveAsync(token);
                while (progress.ContentCursor < progress.Nodes && chunks < FactorySourceValidation.MaximumChunksPerAdvance
                    && files < FactorySourceValidation.MaximumNodesPerAdvance)
                {
                    files++;
                    var node = await ReadNodeAsync(progress.ContentCursor, token);
                    if (node.Mode != "040000")
                    {
                        var completed = await store.ReadAsync(Key("results", node.Index), token);
                        SourcePathResult result;
                        if (completed is not null)
                        {
                            result = SourceProgressStore.Parse<SourcePathResult>(completed);
                            await ReadChunksAsync(node, result, token);
                        }
                        else if (SourceContentPolicy.PathExclusion(node) is { } exclusion && exclusion != SourceExclusion.None)
                        {
                            result = new(progress.Snapshot.Id, node.Index, node.ObjectId, SourcePathState.Excluded, exclusion,
                                "ExcludedBeforeRead", 0, node.Size);
                            await WriteImmutableAsync(Key("results", node.Index), result, token);
                        }
                        else
                        {
                            var acquired = await AcquirePathAsync(node, FactorySourceValidation.MaximumChunksPerAdvance - chunks, token);
                            chunks += acquired.Chunks;
                            if (acquired.Result is null) break;
                            result = acquired.Result;
                            await WriteImmutableAsync(Key("results", node.Index), result, token);
                        }
                        progress = progress with
                        {
                            Acquired = progress.Acquired + (result.State == SourcePathState.Acquired ? 1 : 0),
                            Excluded = progress.Excluded + (result.State == SourcePathState.Excluded ? 1 : 0),
                            Failed = progress.Failed + (result.State == SourcePathState.Failed ? 1 : 0),
                            NecessaryGaps = progress.NecessaryGaps + (result.Exclusion is SourceExclusion.Sensitive or SourceExclusion.Unavailable or SourceExclusion.External ? 1 : 0),
                        };
                    }

                    progress = progress with { ContentCursor = progress.ContentCursor + 1, ChunkCursor = 0 };
                    await SaveAsync(token);
                }
            }
            await github.CheckSnapshotAsync(progress.Snapshot, token);
            var complete = progress.CompleteRootTree && progress.ContentCursor == progress.Nodes && progress.NecessaryGaps == 0
                && progress.Failed == 0 && progress.Acquired + progress.Excluded == progress.Files;
            progress = progress with { State = complete ? SourceState.Acquired : SourceState.Partial,
                Reason = complete ? "AcquiredNotAnalyzed" : progress.NecessaryGaps > 0 ? "NecessarySourceGaps" : "Pending",
                Worker = null, LeaseUntil = null };
            await SaveAsync(token);
            return await ReportAsync(request.Page, "Pinned source acquisition only; raw bytes discarded, not semantic analysis.", token);
        }
        catch (SourceAcquisitionException e) when (e is not SourceProgressException)
        {
            progress = progress with { State = e.State, Reason = e.Message, Worker = null, LeaseUntil = null };
            await SaveAsync(token);
            return await ReportAsync(request.Page, e.Message, token);
        }
    }

    private async Task<(SourcePathResult? Result, int Chunks)> AcquirePathAsync(SourceNode node, int budget, CancellationToken token)
    {
        byte[] bytes;
        try { bytes = await github.BlobAsync(progress!.Snapshot, node, token); }
        catch (SourceAcquisitionException e) when (e.State == SourceState.Partial && e.Message is "Unavailable" or "BlobLimit" or "ResponseLimit")
        { return (new(progress!.Snapshot.Id, node.Index, node.ObjectId, SourcePathState.Failed, SourceExclusion.Unavailable, e.Message, 0, node.Size), 0); }
        try
        {
            var exclusion = SourceContentPolicy.ContentExclusion(bytes);
            if (exclusion != SourceExclusion.None)
                return (new(progress!.Snapshot.Id, node.Index, node.ObjectId, SourcePathState.Excluded, exclusion,
                    "SuppressedContent", 0, bytes.Length), 0);
            var count = Math.Max(1, (bytes.Length + FactorySourceValidation.ChunkBytes - 1) / FactorySourceValidation.ChunkBytes);
            var chunks = 0;
            while (progress!.ChunkCursor < count && chunks < budget)
            {
                var offset = progress.ChunkCursor * FactorySourceValidation.ChunkBytes;
                var length = Math.Min(FactorySourceValidation.ChunkBytes, bytes.Length - offset);
                var chunk = new SourceChunk(progress.Snapshot.Id, node.Index, node.ObjectId, progress.ChunkCursor,
                    offset, length, Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(offset, length))), "utf-8");
                await WriteImmutableAsync(ChunkKey(node.Index, chunk.Index), chunk, token);
                progress = progress with { ChunkCursor = progress.ChunkCursor + 1 };
                await SaveAsync(token);
                chunks++;
            }
            return (progress.ChunkCursor < count ? null : new(progress.Snapshot.Id, node.Index, node.ObjectId,
                SourcePathState.Acquired, SourceExclusion.None, "AcquiredNotAnalyzed", count, bytes.Length), chunks);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task<SourceInventoryResult> StatusAsync(int page, CancellationToken token)
    {
        root = await store.ReadAsync(RootKey, token);
        if (root is null) return new(SourceState.Partial, "Acquisition pending; no snapshot pinned.", null, [], null);
        progress = SourceProgressStore.Parse<SourceProgress>(root);
        ValidateProgress();
        await github.CheckSnapshotAsync(progress.Snapshot, token);
        return await ReportAsync(page, "Historical acquisition coverage; no retained source or analysis.", token);
    }

    public async Task<SourceMaterializedChunk> MaterializeAsync(SourceChunkReference reference, CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        root = await store.ReadAsync(RootKey, token) ?? throw new SourceAcquisitionException(SourceState.Blocked, "MissingSnapshot");
        progress = SourceProgressStore.Parse<SourceProgress>(root);
        ValidateProgress();
        if (progress.Snapshot.Id != reference.SnapshotId || !progress.CompleteRootTree || progress.Worker is not null
            || reference.PathIndex < 1 || reference.PathIndex >= progress.Nodes || reference.ChunkIndex < 0
            || reference.ChunkIndex >= FactorySourceValidation.MaximumBlobBytes / FactorySourceValidation.ChunkBytes
            || !FactorySourceValidation.Hash(reference.Sha256)) throw new SourceAcquisitionException(SourceState.Blocked, "IncompatibleSourceReference");
        var node = await ReadNodeAsync(reference.PathIndex, token);
        var result = SourceProgressStore.Parse<SourcePathResult>(await store.ReadAsync(Key("results", node.Index), token)
            ?? throw new SourceAcquisitionException(SourceState.Blocked, "UnreadSource"));
        var chunk = SourceProgressStore.Parse<SourceChunk>(await store.ReadAsync(ChunkKey(node.Index, reference.ChunkIndex), token)
            ?? throw new SourceAcquisitionException(SourceState.Blocked, "MissingChunk"));
        if (result.State != SourcePathState.Acquired || result.Exclusion != SourceExclusion.None
            || result.SnapshotId != progress.Snapshot.Id || result.BlobId != node.ObjectId || result.PathIndex != node.Index
            || result.Chunks <= reference.ChunkIndex || chunk.Sha256 != reference.Sha256
            || chunk.SnapshotId != progress.Snapshot.Id || chunk.PathIndex != node.Index || chunk.BlobId != node.ObjectId
            || chunk.Index != reference.ChunkIndex || chunk.ByteOffset != chunk.Index * FactorySourceValidation.ChunkBytes
            || chunk.ByteLength is < 0 or > FactorySourceValidation.ChunkBytes || SourceContentPolicy.PathExclusion(node) != SourceExclusion.None)
            throw new SourceAcquisitionException(SourceState.Blocked, "IncompatibleSourceReference");
        await github.CheckSnapshotAsync(progress.Snapshot, token);
        var bytes = await github.BlobAsync(progress.Snapshot, node, token);
        try
        {
            if (SourceContentPolicy.ContentExclusion(bytes) != SourceExclusion.None || result.Bytes != bytes.Length
                || chunk.ByteOffset + chunk.ByteLength > bytes.Length
                || Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(chunk.ByteOffset, chunk.ByteLength))) != reference.Sha256)
                throw new SourceAcquisitionException(SourceState.Blocked, "SourceMaterializationGap");
            await access.CheckAsync(token);
            await FenceAsync(token);
            return new(chunk, bytes.AsSpan(chunk.ByteOffset, chunk.ByteLength).ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<SourceInventoryResult> ReportAsync(int page, string detail, CancellationToken token)
    {
        if (page < 0 || page > FactorySourceValidation.MaximumNodes / FactorySourceValidation.PageSize) throw new ArgumentOutOfRangeException(nameof(page));
        var first = page * FactorySourceValidation.PageSize + 1;
        var paths = new List<SourcePathCoverage>();
        for (var i = first; i < Math.Min(first + FactorySourceValidation.PageSize, progress!.Nodes); i++)
        {
            var node = await ReadNodeAsync(i, token);
            var record = await store.ReadAsync(Key("results", i), token);
            var result = record is null ? null : SourceProgressStore.Parse<SourcePathResult>(record);
            if (node.Mode != "040000" && node.Index < progress.ContentCursor && result is null)
                throw new SourceAcquisitionException(SourceState.Unknown, "MissingPathProgress");
            var chunks = await ReadChunksAsync(node, result, token);
            paths.Add(new(node, result, chunks) { State = result?.State
                    ?? (node.Mode == "040000" && node.Index < progress.TreeCursor ? SourcePathState.Acquired : SourcePathState.Pending),
                ExpectedChunks = node.Mode is "100644" or "100755" && node.Size is >= 0 and <= FactorySourceValidation.MaximumBlobBytes
                    ? Math.Max(1, (int)((node.Size.Value + FactorySourceValidation.ChunkBytes - 1) / FactorySourceValidation.ChunkBytes)) : null });
        }
        await access.CheckAsync(token);
        await FenceAsync(token);
        return new(progress.State, detail, progress, paths, first + paths.Count < progress.Nodes ? page + 1 : null);
    }

    private async Task<SourceNode> ReadNodeAsync(int index, CancellationToken token)
    {
        var node = SourceProgressStore.Parse<SourceNode>(await store.ReadAsync(Key("nodes", index), token) ?? throw new SourceAcquisitionException(SourceState.Unknown, "MissingInventoryPage"));
        if (node.SnapshotId != progress!.Snapshot.Id || node.Index != index || !FactorySourceValidation.GitId(node.ObjectId))
            throw new SourceAcquisitionException(SourceState.Unknown, "InvalidInventoryPage");
        return node;
    }

    private async Task<IReadOnlyList<SourceChunk>> ReadChunksAsync(SourceNode node, SourcePathResult? result, CancellationToken token)
    {
        if (result is not null && (result.SnapshotId != progress!.Snapshot.Id || result.PathIndex != node.Index || result.BlobId != node.ObjectId
            || !Enum.IsDefined(result.State) || !Enum.IsDefined(result.Exclusion) || result.Chunks is < 0 or > 16
            || result.State == SourcePathState.Acquired && (result.Exclusion != SourceExclusion.None || result.Bytes is < 0 or > FactorySourceValidation.MaximumBlobBytes
                || result.Bytes is null || result.Chunks != Math.Max(1, (result.Bytes.Value + FactorySourceValidation.ChunkBytes - 1) / FactorySourceValidation.ChunkBytes))))
            throw new SourceAcquisitionException(SourceState.Unknown, "InvalidPathProgress");
        var chunks = new List<SourceChunk>();
        for (var i = 0; i < FactorySourceValidation.MaximumBlobBytes / FactorySourceValidation.ChunkBytes; i++)
        {
            var record = await store.ReadAsync(ChunkKey(node.Index, i), token);
            if (record is null) break;
            var chunk = SourceProgressStore.Parse<SourceChunk>(record);
            if (chunk.SnapshotId != progress!.Snapshot.Id || chunk.PathIndex != node.Index || chunk.BlobId != node.ObjectId || chunk.Index != i
                || chunk.ByteOffset != i * FactorySourceValidation.ChunkBytes || chunk.ByteLength is < 0 or > FactorySourceValidation.ChunkBytes
                || !FactorySourceValidation.Hash(chunk.Sha256) || chunk.Encoding != "utf-8")
                throw new SourceAcquisitionException(SourceState.Unknown, "InvalidChunkProgress");
            chunks.Add(chunk);
        }
        if (result?.State == SourcePathState.Acquired && (chunks.Count != result.Chunks || chunks.Sum(c => c.ByteLength) != result.Bytes)
            || node.Index == progress!.ContentCursor && chunks.Count < progress.ChunkCursor)
            throw new SourceAcquisitionException(SourceState.Unknown, "MissingChunk");
        return chunks;
    }

    private async Task WriteImmutableAsync<T>(string key, T value, CancellationToken token)
    {
        var existing = await store.ReadAsync(key, token);
        if (existing is not null)
        {
            if (existing.Value != System.Text.Json.JsonSerializer.Serialize(value, FactoryGitHubJson.Options))
                throw new SourceAcquisitionException(SourceState.Unknown, "IncompatibleProgressRecord");
            return;
        }
        var size = SourceProgressStore.RecordSize(key, System.Text.Json.JsonSerializer.Serialize(value, FactoryGitHubJson.Options));
        if (size > FactorySourceValidation.RecordBytes || progress!.Records >= FactorySourceValidation.MaximumRecords
            || progress.MetadataBytes + size > FactorySourceValidation.MaximumMetadataBytes)
            throw new SourceAcquisitionException(SourceState.Partial, "MetadataCapacity");
        progress = progress with { Records = progress.Records + 1, MetadataBytes = progress.MetadataBytes + size };
        await SaveAsync(token);
        await access.CheckAsync(token);
        await FenceAsync(token);
        await store.WriteAsync(key, value, null, token);
    }
    private async Task SaveAsync(CancellationToken token)
    {
        if (progress!.Worker == worker && progress.LeaseUntil <= clock.GetUtcNow()) throw new SourceAcquisitionException(SourceState.Partial, "TimeBudget");
        await access.CheckAsync(token);
        await FenceAsync(token);
        root = await store.WriteAsync(RootKey, progress, root!.ETag, token);
    }
    private async Task FenceAsync(CancellationToken token)
    {
        var current = await store.ReadAsync(RootKey, token);
        if (current?.ETag != root!.ETag || current.Value != root.Value)
            throw new SourceAcquisitionException(SourceState.Unknown, "ProgressOwnershipChanged");
        if (progress!.Worker == worker && progress.LeaseUntil <= clock.GetUtcNow())
            throw new SourceAcquisitionException(SourceState.Partial, "TimeBudget");
    }
    private void ValidateProgress()
    {
        if (progress!.Snapshot.SchemaVersion != 1 || !FactorySourceValidation.Hash(progress.Snapshot.Id)
            || !FactorySourceValidation.GitId(progress.Snapshot.Commit) || !FactorySourceValidation.GitId(progress.Snapshot.RootTree)
            || progress.Snapshot.FactoryId != access.Manifest.Identity.FactoryId || progress.Snapshot.OperationId != access.Source.OperationId
            || progress.Snapshot.ConfigurationRevision != access.Source.ApprovalFingerprint || progress.Snapshot.RepositoryId != access.GitHub.RepositoryId
            || progress.Snapshot.AccountId != access.GitHub.AccountId || !Enum.IsDefined(progress.State)
            || progress.Nodes is < 1 or > FactorySourceValidation.MaximumNodes || progress.TreeCursor < 0 || progress.TreeCursor > progress.Nodes
            || progress.ContentCursor is < 1 || progress.ContentCursor > progress.Nodes || progress.ChildCursor < 0 || progress.ChunkCursor < 0
            || progress.ChunkCursor > 16 || progress.Snapshots is < 1 or > 8 || progress.Files < 0 || progress.Files >= progress.Nodes
            || progress.Acquired < 0 || progress.Excluded < 0 || progress.Failed < 0 || progress.NecessaryGaps < 0
            || progress.Acquired + progress.Excluded + progress.Failed > progress.Files
            || progress.Records is < 1 or > FactorySourceValidation.MaximumRecords || progress.MetadataBytes is < 0 or > FactorySourceValidation.MaximumMetadataBytes
            || progress.CompleteRootTree && progress.TreeCursor != progress.Nodes
            || (progress.Worker is null) != (progress.LeaseUntil is null)
            || progress.State == SourceState.Acquired && (!progress.CompleteRootTree || progress.ContentCursor != progress.Nodes
                || progress.Worker is not null || progress.NecessaryGaps != 0 || progress.Failed != 0 || progress.Acquired + progress.Excluded != progress.Files))
            throw new SourceAcquisitionException(SourceState.Unknown, "UntrustedProgress");
    }
}
