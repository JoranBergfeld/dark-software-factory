using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Dsf.Cli.Tests;

public sealed class SourceWorkflowTransportTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("permissions")]
    [InlineData("roleassignments")]
    [InlineData("denyassignments")]
    public async Task Access_listing_does_not_construct_unrelated_source_inventory(string resource)
    {
        using var foundation = new FactoryFoundationTests.FoundationTransport(new());
        using var transport = new SourceWorkflowTransport(foundation, foundation);
        using var client = new HttpClient(transport);

        using var response = await client.GetAsync(
            "https://management.azure.com/subscriptions/test/providers/microsoft.authorization/" + resource);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var values = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["value"]!.AsArray();
        if (resource == "permissions") Assert.Equal("*", (string)Assert.Single(values)!["actions"]![0]!);
        else Assert.Empty(values);
    }

    [Fact]
    public void Repeated_value_reads_do_not_copy_unchanged_payloads()
    {
        using var foundation = new FactoryFoundationTests.FoundationTransport(new());
        using var transport = new SourceWorkflowTransport(foundation, foundation);
        for (var index = 0; index < 32; index++)
            foundation.Product.Seed($"fixture/{index}", new string('x', 8 * 1024));
        _ = transport.Values();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10; index++) _ = transport.Values();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Repeated value reads allocated {allocated:N0} bytes.");

        // Dictionary copies are expected; repeatedly copying 256 KiB of immutable values is not.
        Assert.True(allocated < 256 * 1024, $"Repeated value reads allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Value_snapshots_are_independent_and_observe_mutations_and_store_replacement()
    {
        using var foundation = new FactoryFoundationTests.FoundationTransport(new());
        using var transport = new SourceWorkflowTransport(foundation, foundation);
        foundation.Product.Seed("first", "original");
        var original = transport.Values();
        original["first"] = "caller edit";
        original["caller-only"] = "not persisted";
        Assert.Equal("original", transport.Values()["first"]);
        Assert.False(transport.Values().ContainsKey("caller-only"));

        foundation.Product.Change("first", value => value + " changed");
        foundation.Product.Seed("second", "new");
        Assert.Equal("original changed", transport.Values()["first"]);
        Assert.Equal("new", transport.Values()["second"]);
        foundation.Product.Remove("first");
        Assert.False(transport.Values().ContainsKey("first"));
        Assert.Equal("caller edit", original["first"]);

        var replacement = new ReservationTestStore();
        replacement.Seed("replacement", "fresh");
        foundation.Product = ReservationTestStore.Restore(replacement.Export());
        Assert.Equal(new Dictionary<string, string> { ["replacement"] = "fresh" }, transport.Values());
    }

    [Fact]
    public async Task Value_snapshots_observe_conditional_writes_and_deletes_without_accepting_stale_writes()
    {
        using var foundation = new FactoryFoundationTests.FoundationTransport(new());
        using var transport = new SourceWorkflowTransport(foundation, foundation);
        using var client = new HttpClient(foundation.Product.Client()) { BaseAddress = new("https://owner.azconfig.io") };
        foundation.Product.Seed("entry", "original");
        var original = transport.Values();
        using var read = await client.GetAsync("/kv/entry");
        var etag = read.Headers.ETag!.Tag;
        using var write = new HttpRequestMessage(HttpMethod.Put, "/kv/entry") { Content = JsonContent.Create(new { value = "changed" }) };
        write.Headers.TryAddWithoutValidation("If-Match", etag);
        using var written = await client.SendAsync(write);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        Assert.Equal("changed", transport.Values()["entry"]);
        Assert.Equal("original", original["entry"]);

        using var stale = new HttpRequestMessage(HttpMethod.Put, "/kv/entry") { Content = JsonContent.Create(new { value = "stale" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        using var rejected = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        Assert.Equal("changed", transport.Values()["entry"]);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/kv/entry");
        delete.Headers.TryAddWithoutValidation("If-Match", written.Headers.ETag!.Tag);
        using var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(transport.Values());
    }
}
