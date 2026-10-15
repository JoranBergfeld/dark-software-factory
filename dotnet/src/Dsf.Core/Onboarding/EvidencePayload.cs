using System.Text.Json;

namespace Dsf.Core.Onboarding;

public static class EvidencePayload
{
    public const int MaximumBytes = 256 * 1024;
    public static void RequireComplete(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
            if (property.Name is "nextLink" or "@odata.nextLink" or "continuationToken" or "nextPageToken"
                    && property.Value.ValueKind != JsonValueKind.Null && (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != "")
                || property.Name is "partial" or "truncated" && property.Value.ValueKind != JsonValueKind.False && property.Value.ValueKind != JsonValueKind.Null)
                throw new SupplementalSourceException(SupplementalReason.Incomplete);
    }
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken token, int limit = MaximumBytes)
    {
        if (response.Content.Headers.ContentLength > limit) throw new SupplementalSourceException(SupplementalReason.CapacityExceeded);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, token);
            if (count == 0) break;
            if (buffer.Length + count > limit) throw new SupplementalSourceException(SupplementalReason.CapacityExceeded);
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 32 });
    }
}
