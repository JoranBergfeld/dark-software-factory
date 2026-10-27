using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dsf.Cli;

internal static class BoundaryPlanFingerprint
{
    public static string Hash(object value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(JsonSerializer.SerializeToElement(value)))));

    public static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "]",
        _ => value.GetRawText(),
    };
}
