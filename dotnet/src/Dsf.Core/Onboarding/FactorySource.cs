namespace Dsf.Core.Onboarding;

public enum SourceState { Acquired, Partial, Blocked, Failed, Canceled, Unknown }
public enum SourcePathState { Pending, Acquired, Excluded, Failed }
public enum SourceExclusion { None, Generated, Vendor, Binary, Sensitive, Unavailable, External }

public sealed record SourceSelection(string RuntimeImage, string AudienceClientId);
public sealed record SourceInfrastructureBinding(FoundationOperation Receipt, string Incarnation);
public sealed record SourceConfiguration(
    int SchemaVersion, Guid FactoryId, string PlanFingerprint, string ProfilePin,
    Guid GitHubOperationId, string GitHubApprovalFingerprint, SourceSelection Selection, string TemplateSha256,
    IReadOnlyList<SourceInfrastructureBinding> Infrastructure);
public sealed record FactorySource(
    Guid OperationId, string ApprovalFingerprint, SourceConfiguration Configuration,
    SourceState State, string Stage, int OperationCount, IReadOnlyList<string> EffectHashes,
    string? HostEndpoint, string Detail);

public sealed record SourceSnapshot(
    int SchemaVersion, Guid FactoryId, Guid OperationId, string ConfigurationRevision,
    long RepositoryId, long AccountId, string Repository, string DefaultBranch, string Commit,
    string RootTree, DateTimeOffset ObservedAt, string Id);

public sealed record SourceProgress(SourceSnapshot Snapshot)
{
    public SourceState State { get; init; } = SourceState.Partial;
    public string Reason { get; init; } = "Pending";
    public int Nodes { get; init; } = 1;
    public int TreeCursor { get; init; }
    public int ChildCursor { get; init; }
    public int ContentCursor { get; init; } = 1;
    public int ChunkCursor { get; init; }
    public bool CompleteRootTree { get; init; }
    public int Acquired { get; init; }
    public int Excluded { get; init; }
    public int Failed { get; init; }
    public int NecessaryGaps { get; init; }
    public int Files { get; init; }
    public int Records { get; init; } = 1;
    public int Snapshots { get; init; } = 1;
    public long MetadataBytes { get; init; } = FactorySourceValidation.RecordBytes;
    public Guid? Worker { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
}

public sealed record SourceNode(string SnapshotId, int Index, string Path, string Mode, string ObjectId, long? Size);
public sealed record SourceChunk(string SnapshotId, int PathIndex, string BlobId, int Index, int ByteOffset,
    int ByteLength, string Sha256, string Encoding);
public sealed record SourcePathResult(string SnapshotId, int PathIndex, string BlobId,
    SourcePathState State, SourceExclusion Exclusion, string Reason, int Chunks, long? Bytes);
public sealed record SourcePathCoverage(SourceNode Node, SourcePathResult? Result, IReadOnlyList<SourceChunk> Chunks)
{
    public SourcePathState State { get; init; } = SourcePathState.Pending;
    public int? ExpectedChunks { get; init; }
}
public sealed record SourceInventoryResult(SourceState State, string Detail, SourceProgress? Progress,
    IReadOnlyList<SourcePathCoverage> Paths, int? NextPage)
{
    public string Analysis { get; init; } = "NotYetAnalyzed";
    public string DeployedSourceCorrelation { get; init; } = "Unknown";
    public bool RawSourceRetained { get; init; }
}

public sealed record SourceAcquireRequest(bool StartNewSnapshot = false, int Page = 0);
public sealed record SourceChunkReference(string SnapshotId, int PathIndex, int ChunkIndex, string Sha256);

public interface IFactorySourceMaterializer
{
    Task<SourceMaterializedChunk> MaterializeAsync(SourceChunkReference reference, CancellationToken cancellationToken);
}

/// <summary>Temporary reacquired bytes; dispose after consumption. Acquisition metadata is not a source cache.</summary>
public sealed class SourceMaterializedChunk(SourceChunk provenance, byte[] bytes) : IDisposable
{
    public SourceChunk Provenance { get; } = provenance;
    public ReadOnlyMemory<byte> Content => bytes;
    public void Dispose() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
}
