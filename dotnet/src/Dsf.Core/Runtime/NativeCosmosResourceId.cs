using System.Text.Json;

namespace Dsf.Core.Runtime;

public static class NativeCosmosResourceId
{
    public static string RequireDocument(string? value)
    {
        if (value is null || value.Length != 24) throw new JsonException("Native Cosmos document RID required.");
        // Cosmos replaces '/' with '-', not the Base64URL replacement for '+'.
        var normalized = value.Replace('-', '/');
        Span<byte> bytes = stackalloc byte[16];
        if (!Convert.TryFromBase64String(normalized, bytes, out var length) || length != bytes.Length
            || Convert.ToBase64String(bytes) != normalized || (bytes[4] & 0x80) == 0 || (bytes[15] & 0xf0) != 0
            || bytes[..4].IndexOfAnyExcept((byte)0) < 0 || bytes[8..].IndexOfAnyExcept((byte)0) < 0)
            throw new JsonException("Native Cosmos document RID required.");
        return value;
    }
}
