using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record FactoryActivityReference(string Id, string IntentSha256, DateTimeOffset IntendedAt)
{
    public string? NativeResourceId { get; init; }
    public FactoryPublicationIntent? Publication { get; init; }
    public FactoryPublicationDecisionScope? Decision { get; init; }
}

public sealed record FactoryActivityIndex(int Protocol, string Id, string Product, Guid FactoryId, Guid PromotionOperation,
    DateTimeOffset CreatedAt, IReadOnlyList<FactoryActivityReference> Entries)
{
    public const string KindName = "onboarding-native-reference-index-v1";
    public const int MaximumEntries = 256;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Page { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? NextPageNativeResourceId { get; init; }
    public string Kind { get; init; } = KindName;
    public string Checksum { get; init; } = "";
    public string CalculateChecksum() => FactoryModelsValidation.Hash(this with { Checksum = "" });
    public static string Name(Guid factory, bool publication, int page = 0) =>
        (publication ? "publication-index-" : "decision-index-") + factory.ToString("N")
        + (page == 0 ? "" : "-page-" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string Container(bool publication) => publication ? FactoryPublication.Container : FactoryPreviewValidation.PreviewContainer;
    public static string Key(string product, Guid factory, bool publication, int page = 0) =>
        $"dbs/{product}/colls/{Container(publication)}/docs/{Name(factory, publication, page)}";
    public static FactoryActivityIndex Read(string json, Guid factory, string product, Guid operation, bool publication,
        DateTimeOffset now, int page = 0)
    {
        var body = JsonNode.Parse(json, documentOptions: new() { AllowDuplicateProperties = false })!.AsObject();
        foreach (var key in new[] { "_rid", "_etag", "_self", "_attachments", "_ts" }) body.Remove(key);
        var index = body.Deserialize<FactoryActivityIndex>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (index.Protocol != 1 || index.Kind != KindName || index.FactoryId != factory || index.Product != product
            || index.Id != Name(factory, publication, page) || index.Page != page || page < 0
            || index.PromotionOperation != operation || operation == Guid.Empty
            || index.CreatedAt == default || index.CreatedAt > now || index.CreatedAt.Offset != TimeSpan.Zero
            || index.Entries.Any(e => e is null)
            || index.Checksum != index.CalculateChecksum() || index.Entries.Count > MaximumEntries
            || index.NextPageNativeResourceId is not null && index.Entries.Count != MaximumEntries
            || index.Entries.Select(e => e.Id).Distinct().Count() != index.Entries.Count
            || index.Entries.Any(e => e.IntendedAt < index.CreatedAt || e.IntendedAt > now || !FactorySourceValidation.Hash(e.IntentSha256)
                || !e.Id.StartsWith(publication ? "proposal-" : "decision-", StringComparison.Ordinal)
                || !FactorySourceValidation.Hash(e.Id[(publication ? "proposal-" : "decision-").Length..])))
            throw new JsonException("Complete original bounded native reference index required.");
        if (index.NextPageNativeResourceId is not null)
            _ = NativeCosmosResourceId.RequireDocument(index.NextPageNativeResourceId);
        foreach (var entry in index.Entries)
        {
            if (entry.NativeResourceId is not null) _ = NativeCosmosResourceId.RequireDocument(entry.NativeResourceId);
            if (publication)
            {
                if (entry.Publication is not { } intent || entry.Decision is not null || intent.FactoryId != factory || intent.Product != product
                    || FactoryPublication.Id(intent) != entry.Id || FactoryModelsValidation.Hash(intent) != entry.IntentSha256) throw new JsonException();
                FactoryPublication.Validate(intent);
            }
            else if (entry.Decision is not { } scope || entry.Publication is not null || scope.Promotion.FactoryId != factory
                || scope.Publisher.FactoryId != factory || FactoryPromotion.DecisionId(factory, scope.InvocationId) != entry.Id
                || FactoryModelsValidation.Hash(scope) != entry.IntentSha256) throw new JsonException();
        }
        return index;
    }
}
