using System.Text.Json;

namespace Dsf.Core.Runtime;

/// <summary>
/// Reads and validates a <c>--signal</c> JSON file for the <c>run</c> verb,
/// mirroring the deterministic, I/O-free normalization the Python runtime's
/// <c>control.signal_to_run</c> performs once the file is loaded: a missing or
/// blank <c>product_hints</c>/<c>source_kinds</c> field yields an empty list, and
/// unknown source kinds are dropped rather than rejected. Every failure here is a
/// real, input-dependent condition (missing file, invalid JSON) rather than an
/// unconditional stub.
/// </summary>
public static class SignalReader
{
    public static Signal ReadFromFile(string path, bool dryRun)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"signal file not found: {path}", path);
        }

        using var document = Parse(path);
        var root = document.RootElement;
        var productHints = ReadStringList(root, "product_hints");
        var sourceKinds = ReadStringList(root, "source_kinds")
            .Select(kind => kind.Trim().ToLowerInvariant())
            .Where(SourceAgentKinds.IsKnown)
            .ToArray();
        return new Signal(path, productHints, sourceKinds, dryRun);
    }

    private static JsonDocument Parse(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new JsonException($"signal file '{path}' is not valid JSON: {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => [value.GetString()!.Trim()],
            JsonValueKind.Array => value.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString())
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray(),
            _ => [],
        };
    }
}
