using System.Text;
using System.Text.Json;

namespace Dsf.Core.Onboarding;

public static class FactoryCharterResponses
{
    public const int MaximumBytes = 2048;
    public const int MaximumEncodedCharacters = (MaximumBytes * 4 + 2) / 3;
    public static string Key(Guid operation, Guid attempt) =>
        $"dsf/factory/charter/publications/{operation:N}/responses/{attempt:N}";

    public static CharterResponseRecord Encode(CharterResponseWitness witness)
    {
        var bytes = Encoding.UTF8.GetBytes(witness.ResponseJson);
        if (bytes.Length is < 1 or > MaximumBytes) throw new JsonException("Original response exceeds bounded evidence record.");
        return new(1, witness.FactoryId, witness.RepositoryId, witness.Operation, witness.Attempt,
            witness.ReviewFingerprint, witness.RequestSha256, witness.Native, Encode(bytes));
    }

    public static CharterResponseWitness Read(CharterPublication publication, CharterEffect effect, string value, string revision)
    {
        var reference = effect.ResponseRecord ?? throw new JsonException();
        if (effect.Response is not null || string.IsNullOrEmpty(revision)
            || FactoryAudit.TextHash(revision) != reference.RevisionSha256 || FactoryAudit.TextHash(value) != reference.Sha256)
            throw new JsonException("Original native response record changed.");
        var record = JsonSerializer.Deserialize<CharterResponseRecord>(value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (record.Protocol != 1 || record.FactoryId != publication.FactoryId || record.RepositoryId <= 0
            || record.Operation != publication.Operation || record.Attempt != effect.Attempt || effect.Attempt == Guid.Empty
            || record.ReviewFingerprint != publication.ReviewFingerprint || record.RequestSha256 != effect.RequestSha256
            || effect.State is not (CharterEffectState.Dispatched or CharterEffectState.Unknown or CharterEffectState.Confirmed)
            || record.Native is not { Method: "POST", Route: "graphql", Status: 200, RequestId.Length: > 0 and <= 128 }
            || record.Native.RequestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != ':')
            || record.ResponseUtf8 is not { Length: > 0 and <= MaximumEncodedCharacters })
            throw new JsonException("Original response scope differs.");
        var encoded = record.ResponseUtf8.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
        if (bytes.Length is < 1 or > MaximumBytes || Encode(bytes) != record.ResponseUtf8) throw new JsonException();
        string json;
        try { json = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new JsonException("Original response UTF-8 encoding differs."); }
        if (FactoryModelsValidation.Hash(json) != record.Native.ResponseSha256) throw new JsonException("Original response bytes differ.");
        return new(record.FactoryId, record.RepositoryId, record.Operation, record.Attempt,
            record.ReviewFingerprint, record.RequestSha256, record.Native, json);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
