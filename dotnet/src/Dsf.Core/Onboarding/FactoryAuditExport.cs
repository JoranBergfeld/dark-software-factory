using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public sealed record AuditExportChunk(string Name, int Bytes, string Sha256);
public sealed record AuditExportManifest(int Protocol, Guid FactoryId, string GraphRevision, string Policy,
    IReadOnlyList<AuditRecordIdentity> Roots, int Records, IReadOnlyList<AuditExportChunk> Chunks,
    IReadOnlyList<AuditExportChunk> Documents);
public sealed record AuditExportReceipt(int Protocol, Guid FactoryId, Guid Operation, string GraphRevision,
    string Policy, string Path, string ManifestSha256, int Records, string ApprovedBy, DateTimeOffset VerifiedAt);
public sealed record AuditExportIntent(int Protocol, Guid FactoryId, Guid Operation, string GraphRevision,
    string Policy, string Path, string ApprovedBy, DateTimeOffset ApprovedAt);

/// <summary>Private local Linux artifact with create-only writes, native owner/mode checks and full readback.</summary>
public static class FactoryAuditExport
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int ChunkBytes = 256 * 1024;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(FactorySourceValidation.RecordJson) { WriteIndented = true };

    public static string ReviewPath(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new FactoryAuditException(FactoryAuditState.Blocked, "Private audit export requires a local Linux filesystem with native Unix ownership and permissions.");
        if (!Path.IsPathFullyQualified(path) || path.Contains("://", StringComparison.Ordinal) || path.Any(char.IsControl)
            || path.Length > 1024 || Path.GetFullPath(path) != path || Path.GetFileName(path).Length == 0)
            throw new FactoryAuditException(FactoryAuditState.Blocked, "An absolute normalized local export directory is required; public/provider URLs are not exports.");
        var parent = Path.GetDirectoryName(path) ?? throw new IOException();
        RequirePrivate(parent, true);
        if (Path.Exists(path)) throw new FactoryAuditException(FactoryAuditState.Blocked, "Export target already exists; no overwrite or artifact adoption.");
        return path;
    }

    public static string Write(AuditGraph graph, string path, CancellationToken token)
    {
        _ = ReviewPath(path);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(path, DirectoryMode);
        RequirePrivate(path, true);
        var chunks = new List<AuditExportChunk>();
        var records = new List<FactoryAuditRecord>();
        var bytes = 0;
        foreach (var record in graph.Records)
        {
            token.ThrowIfCancellationRequested();
            var length = JsonSerializer.SerializeToUtf8Bytes(record, Json).Length;
            if (length > MaximumFileBytes - 1024) throw new FactoryAuditException(FactoryAuditState.Partial, "Audit record exceeds bounded export chunk capacity.");
            if (bytes + length > ChunkBytes && records.Count != 0) Flush();
            records.Add(record);
            bytes += length;
        }
        if (records.Count != 0) Flush();
        var documents = new List<AuditExportChunk>();
        foreach (var document in FactoryAuditDocuments.Read(graph))
        {
            token.ThrowIfCancellationRequested();
            var content = JsonSerializer.SerializeToUtf8Bytes(document, Json);
            if (content.Length > MaximumFileBytes) throw new FactoryAuditException(FactoryAuditState.Partial, "Decoded audit document exceeds bounded export capacity.");
            var name = $"document-{documents.Count:D4}.json";
            WritePrivate(Path.Combine(path, name), content);
            documents.Add(new(name, content.Length, Hash(content)));
        }
        var manifest = new AuditExportManifest(1, graph.FactoryId, graph.Revision, graph.Policy, graph.Roots, graph.Records.Count, chunks, documents);
        WritePrivate(Path.Combine(path, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        return Verify(graph, path, token);

        void Flush()
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(records, Json);
            var name = $"records-{chunks.Count:D4}.json";
            WritePrivate(Path.Combine(path, name), content);
            chunks.Add(new(name, content.Length, Hash(content)));
            records.Clear();
            bytes = 0;
        }
    }

    public static string Verify(AuditGraph current, string path, CancellationToken token)
    {
        RequirePrivate(path, true);
        var manifestBytes = ReadPrivate(Path.Combine(path, "manifest.json"), FactoryAudit.MaximumBytes);
        var manifest = JsonSerializer.Deserialize<AuditExportManifest>(manifestBytes, Json) ?? throw new JsonException();
        if (manifest.Protocol != 1 || manifest.FactoryId != current.FactoryId || manifest.GraphRevision != current.Revision || manifest.Policy != current.Policy)
            throw new FactoryAuditException(FactoryAuditState.Blocked, "Export roots/revisions/retention context are obsolete; no disposition authority.");
        if (manifest.Records != current.Records.Count || manifest.Chunks.Count is < 1 or > 4096
            || !manifest.Roots.SequenceEqual(current.Roots)) throw new JsonException("Incomplete export manifest.");
        var records = new List<FactoryAuditRecord>();
        long total = manifestBytes.Length;
        for (var i = 0; i < manifest.Chunks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var chunk = manifest.Chunks[i];
            if (chunk.Name != $"records-{i:D4}.json" || chunk.Bytes is < 1 or > MaximumFileBytes) throw new JsonException();
            var bytes = ReadPrivate(Path.Combine(path, chunk.Name), MaximumFileBytes);
            if (bytes.Length != chunk.Bytes || Hash(bytes) != chunk.Sha256
                || (total += bytes.Length) > 4L * FactoryAudit.MaximumBytes) throw new JsonException("Corrupt or oversized export.");
            records.AddRange(JsonSerializer.Deserialize<FactoryAuditRecord[]>(bytes, Json) ?? throw new JsonException());
            if (records.Count > manifest.Records) throw new JsonException();
        }
        var documents = FactoryAuditDocuments.Read(current);
        if (manifest.Documents.Count != documents.Count) throw new JsonException("Missing readable logical documents.");
        for (var i = 0; i < documents.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var expected = JsonSerializer.SerializeToUtf8Bytes(documents[i], Json);
            var proof = manifest.Documents[i];
            if (proof.Name != $"document-{i:D4}.json" || proof.Bytes != expected.Length || proof.Sha256 != Hash(expected)
                || !ReadPrivate(Path.Combine(path, proof.Name), MaximumFileBytes).SequenceEqual(expected))
                throw new JsonException("Readable decoded document differs from its pinned source chunks.");
        }
        if (Directory.EnumerateFileSystemEntries(path).Count() != manifest.Chunks.Count + documents.Count + 1 || records.Count != manifest.Records)
            throw new JsonException("Unexpected or missing export entries.");
        var identities = records.Select(r => r.Identity).ToHashSet();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var expected = current.Records[i];
            using var readable = JsonDocument.Parse(record.Content, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (record.Identity != expected.Identity || record.Content != expected.Content
                || record.Identity.Sha256 != FactoryAudit.TextHash(record.Content)
                || !record.References.SequenceEqual(expected.References) || record.References.Any(r => !identities.Contains(r)))
                throw new JsonException("Export content, revisions or reference closure differ.");
        }
        if (identities.Count != manifest.Records || manifest.Roots.Any(r => !identities.Contains(r))) throw new JsonException();
        RequirePrivate(path, true);
        return Hash(manifestBytes);
    }

    private static byte[] ReadPrivate(string path, int maximum)
    {
        RequirePrivate(path, false);
        using var stream = new FileStream(path, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 || stream.Length > maximum) throw new IOException("Invalid private artifact size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Private artifact changed.");
        RequirePrivate(path, false);
        return bytes;
    }

    private static void WritePrivate(string path, byte[] bytes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = FileMode,
        }))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        RequirePrivate(path, false);
    }

    private static void RequirePrivate(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux()) throw new FactoryAuditException(FactoryAuditState.Blocked, "Private local Linux artifact required.");
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw new IOException("Normalized artifact path required.");
        var ancestors = directory ? path : Path.GetDirectoryName(path);
        while (ancestors is not null)
        {
            if ((File.GetAttributes(ancestors) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symlink export paths are not supported.");
            ancestors = Path.GetDirectoryName(ancestors);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || File.GetAttributes(path).HasFlag(FileAttributes.Directory) != directory
            || File.GetUnixFileMode(path) != (directory ? DirectoryMode : FileMode))
            throw new FactoryAuditException(FactoryAuditState.Blocked, "Artifact must remain owner-only: directory 0700, regular files 0600; no symlinks.");
        var native = Native("/usr/bin/stat", "--format=%u:%h:%F", "--", path).Split(':');
        var owner = Native("/usr/bin/id", "-u");
        if (native.Length != 3 || native[0] != owner || !directory && (native[1] != "1" || native[2] != "regular file"))
            throw new FactoryAuditException(FactoryAuditState.Blocked, "Native artifact ownership/type/link count is not private.");
    }

    private static string Native(string command, params string[] arguments)
    {
        using var process = new Process { StartInfo = new(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        process.StartInfo.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Native filesystem proof unavailable.");
        if (!process.WaitForExit(5000)) { process.Kill(); throw new IOException("Native filesystem proof timed out."); }
        if (process.ExitCode != 0) throw new IOException("Native filesystem proof unavailable.");
        return process.StandardOutput.ReadToEnd().Trim();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
