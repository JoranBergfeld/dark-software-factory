using System.Text;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public enum FactoryReconfigurationPhase { Quiescing }

public sealed record FactoryReconfigurationOperation(
    int Protocol, Guid OperationId, FactoryReservationIdentity Identity, string ApprovalFingerprint,
    string ReviewedRevision, string ManifestRevision, FactoryManifest ReviewedManifest,
    string InputSha256, int InputBytes, int InputChunks, string ApprovedBy, DateTimeOffset ApprovedAt,
    FactoryReconfigurationPhase Phase, Guid CoordinatorId, int ConfirmedChunks);

public sealed record FactoryReconfigurationInputChunk(int Protocol, Guid OperationId, int Index, string Data);

public static class FactoryReconfigurationOperationRecords
{
    public const string Prefix = "dsf/factory/reconfiguration/";
    public const string RootKey = Prefix + "current";
    public const int ChunkBytes = 4000;
    public const int MaximumInputBytes = 1024 * 1024;
    public const int MaximumRecords = 272;
    public static string RequestKey(Guid operation) => Prefix + operation.ToString("N") + "/request";
    public static string InputKey(Guid operation, int index) => Prefix + operation.ToString("N") + "/inputs/" + index.ToString("D4");
    public static bool IsRecord(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    public static IReadOnlyList<string> ValidateArchive(FactoryReconfigurationOperation operation,
        IReadOnlyDictionary<string, string> records)
    {
        var requestKey = RequestKey(operation.OperationId);
        if (!records.TryGetValue(requestKey, out var requestValue)) throw new JsonException();
        var request = Read(requestValue, operation.Identity, operation.ApprovedAt);
        if (request.ConfirmedChunks != 0 || request.CoordinatorId != operation.OperationId
            || FactoryModelsValidation.Hash(operation with
            { CoordinatorId = request.CoordinatorId, ConfirmedChunks = 0, Phase = request.Phase }) != FactoryModelsValidation.Hash(request))
            throw new JsonException();
        using var content = new MemoryStream();
        var keys = new List<string> { requestKey };
        for (var index = 0; index < operation.InputChunks; index++)
        {
            var key = InputKey(operation.OperationId, index);
            if (!records.TryGetValue(key, out var value)) throw new JsonException();
            var chunk = JsonSerializer.Deserialize<FactoryReconfigurationInputChunk>(value, FactorySourceValidation.RecordJson)
                ?? throw new JsonException();
            var bytes = Convert.FromBase64String(chunk.Data);
            if (chunk.Protocol != 1 || chunk.OperationId != operation.OperationId || chunk.Index != index
                || bytes.Length != Math.Min(ChunkBytes, operation.InputBytes - index * ChunkBytes))
                throw new JsonException();
            content.Write(bytes);
            keys.Add(key);
        }
        if (content.Length != operation.InputBytes
            || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.ToArray())) != operation.InputSha256)
            throw new JsonException();
        _ = JsonSerializer.Deserialize<FactoryReconfigurationInputs>(Encoding.UTF8.GetString(content.ToArray()),
            FactorySourceValidation.RecordJson) ?? throw new JsonException();
        return keys;
    }

    public static FactoryReconfigurationOperation Read(string value, FactoryReservationIdentity identity, DateTimeOffset now)
    {
        var operation = JsonSerializer.Deserialize<FactoryReconfigurationOperation>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (operation.Protocol != 1 || operation.OperationId == Guid.Empty || operation.CoordinatorId == Guid.Empty
            || operation.Identity != identity || operation.ReviewedManifest.Identity != identity
            || !FactorySourceValidation.Hash(operation.ApprovalFingerprint)
            || !FactorySourceValidation.Hash(operation.ReviewedRevision) || !FactorySourceValidation.Hash(operation.InputSha256)
            || operation.ManifestRevision.Length is < 1 or > 256 || operation.InputBytes is <= 0 or > MaximumInputBytes
            || operation.InputChunks != (operation.InputBytes + ChunkBytes - 1) / ChunkBytes
            || operation.ConfirmedChunks < 0 || operation.ConfirmedChunks > operation.InputChunks
            || operation.ApprovedBy != identity.AzureActorId || operation.ApprovedAt < identity.CreatedAt
            || operation.ApprovedAt > now || operation.ApprovedAt.Offset != TimeSpan.Zero || !Enum.IsDefined(operation.Phase))
            throw new JsonException("Invalid retained reconfiguration operation.");
        return operation;
    }
}
