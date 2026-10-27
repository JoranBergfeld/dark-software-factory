using System.Text.Json;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Core.Tests;

public sealed class FactoryActivityIndexTests
{
    [Fact]
    public void Original_v1_index_without_paging_fields_retains_its_checksum()
    {
        var now = DateTimeOffset.Parse("2026-09-15T23:00:00Z");
        var factory = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var original = new LegacyActivityIndex(1, FactoryActivityIndex.Name(factory, false),
            "widget", factory, operation, now, []);
        original = original with { Checksum = FactoryModelsValidation.Hash(original) };

        var restored = FactoryActivityIndex.Read(JsonSerializer.Serialize(original, FactorySourceValidation.RecordJson),
            factory, "widget", operation, false, now);

        Assert.Equal(original.Checksum, restored.CalculateChecksum());
        Assert.Equal(0, restored.Page);
        Assert.Null(restored.NextPageNativeResourceId);
    }

    private sealed record LegacyActivityIndex(int Protocol, string Id, string Product, Guid FactoryId, Guid PromotionOperation,
        DateTimeOffset CreatedAt, IReadOnlyList<FactoryActivityReference> Entries)
    {
        public string Kind { get; init; } = FactoryActivityIndex.KindName;
        public string Checksum { get; init; } = "";
    }
}
