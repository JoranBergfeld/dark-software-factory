using System.Net;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class FactorySourceProgressProtocolTests
{
    [Theory]
    [InlineData("pages")]
    [InlineData("bytes")]
    public async Task Native_listing_limits_stop_before_initialization(string limit)
    {
        using var transport = new SourceInventoryTransport();
        if (limit == "pages") transport.EmptyProgressPages = 1024;
        else transport.ChangeProgressPage = (_, response) =>
            response.Content = new StringContent(new JsonObject { ["items"] = new JsonArray(), ["padding"] = new string('x', 4 * 1024 * 1024) }.ToJsonString());
        var result = await FactorySourceInventoryTests.RunAsync(transport);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(result.Body)!["state"]);
        Assert.Contains(limit == "pages" ? "ProgressListingLimit" : "ProgressListingPageLimit", result.Body);
        Assert.Empty(transport.Writes);
        if (limit == "pages")
            Assert.Equal(1024, transport.Requests.Count(r => r.Contains(new Uri(transport.ProgressEndpoint).Host + "/kv?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Lost_integrity_proof_response_resumes_verified_remote_records_without_double_charging()
    {
        using var first = new SourceInventoryTransport();
        first.AfterWrite = (key, _) =>
        {
            if (key.Contains("/integrity/", StringComparison.Ordinal)) throw new HttpRequestException("Lost integrity proof response");
        };
        var failed = await FactorySourceInventoryTests.RunAsync(first);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(failed.Body)!["state"]);
        using var second = new SourceInventoryTransport();
        second.ImportProgress(first);
        second.Clock.Now = first.Clock.Now.AddMinutes(2);
        var resumed = await AcquireAsync(second);
        Assert.Equal("Acquired", (string?)resumed["state"]);
        Assert.Equal(second.Progress.Count, (int)resumed["progress"]!["records"]!);
        Assert.DoesNotContain(second.Writes, k => k.Contains("/integrity/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("branch")]
    [InlineData("access")]
    [InlineData("authority")]
    [InlineData("deadline")]
    public async Task Inflight_materialization_changes_never_release_stale_bytes(string change)
    {
        using var first = new SourceInventoryTransport();
        var acquired = await AcquireAsync(first);
        var reference = new SourceChunkReference((string)acquired["progress"]!["snapshot"]!["id"]!, 1, 0,
            (string)acquired["paths"]![0]!["chunks"]![0]!["sha256"]!);
        using var consumer = new SourceInventoryTransport();
        consumer.ImportProgress(first);
        var delivered = false;
        consumer.AfterBlobResponse = () =>
        {
            delivered = true;
            if (change == "head") consumer.Head = new string('b', 40);
            if (change == "branch") consumer.Branch = "new-default";
            if (change == "access") consumer.GitHubStatus = 403;
            if (change == "authority") consumer.Values.Remove(Dsf.Core.Runtime.ProductConfigurationKeys.FactoryProfile);
        };
        consumer.OnRequest = request =>
        {
            if (delivered && change == "deadline" && request.RequestUri!.Host == "management.azure.com")
                consumer.Clock.Now += TimeSpan.FromSeconds(46);
        };
        var result = await FactorySourceInventoryTests.RunAsync(consumer, "/materialize", reference, codeCaller: true);
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Equal(change == "deadline" ? "Partial" : "Blocked", (string?)JsonNode.Parse(result.Body)!["state"]);
        Assert.DoesNotContain("First party application documentation", result.Body);
        Assert.Empty(consumer.Writes);
        if (change == "head")
            Assert.Contains("SourceIdentityChanged", (await FactorySourceInventoryTests.RunAsync(consumer, "/status")).Body);
    }

    [Fact]
    public async Task Revisited_A_B_A_C_selections_keep_distinct_immutable_occurrences_and_the_eight_selection_limit()
    {
        var workers = new List<SourceInventoryTransport>();
        long bytes = 0;
        try
        {
            foreach (var revision in "abacdef2")
            {
                var worker = new SourceInventoryTransport();
                if (workers.LastOrDefault() is { } previous)
                {
                    worker.ImportProgress(previous);
                    worker.Clock.Now = previous.Clock.Now.AddSeconds(2);
                }
                workers.Add(worker);
                Select(worker, revision);
                var result = await AcquireAsync(worker, new SourceAcquireRequest(StartNewSnapshot: true));
                Assert.True((string?)result["state"] == "Acquired", result.ToJsonString());
                Assert.Equal(workers.Count, (int)result["progress"]!["snapshots"]!);
                Assert.True((long)result["progress"]!["metadataBytes"]! >= bytes);
                bytes = (long)result["progress"]!["metadataBytes"]!;
                Assert.Equal(worker.Progress.Count, (int)result["progress"]!["records"]!);
            }
            var last = workers[^1];
            var history = last.Progress.Where(p => p.Key.Contains("/history/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(7, history.Length);
            Assert.Equal(7, history.Select(p => p.Key.Split('/').Last()).Distinct().Count());
            var a = history.Select(p => JsonNode.Parse(p.Value.Value)!).Where(h => (string?)h["snapshot"]!["commit"] == new string('a', 40)).ToArray();
            Assert.Equal(2, a.Length);
            Assert.NotEqual((string?)a[0]["snapshot"]!["observedAt"], (string?)a[1]["snapshot"]!["observedAt"]);
            var saved = last.Progress.ToDictionary(p => p.Key, p => p.Value.Value);
            Select(last, '3');
            var limited = await FactorySourceInventoryTests.RunAsync(last, request: new SourceAcquireRequest(StartNewSnapshot: true));
            Assert.Equal("Partial", (string?)JsonNode.Parse(limited.Body)!["state"]);
            Assert.Contains("SnapshotHistoryLimit", limited.Body);
            Assert.Equal(saved, last.Progress.ToDictionary(p => p.Key, p => p.Value.Value));
        }
        finally { foreach (var worker in workers) worker.Dispose(); }
    }

    private static void Select(SourceInventoryTransport transport, char revision)
    {
        transport.Head = new string(revision, 40);
        transport.RootTree = transport.Tree(("app.py", "100644", transport.Blob($"print('{revision}')")));
    }

    [Fact]
    public async Task Interrupted_full_listing_is_not_cached_success_and_a_new_reader_rechecks_every_page_without_writes()
    {
        using var first = new SourceInventoryTransport();
        Populate(first);
        Assert.Equal("Acquired", (string?)(await AcquireAsync(first))["state"]);
        using var interrupted = new SourceInventoryTransport { ProgressPageSize = 10 };
        Populate(interrupted);
        interrupted.ImportProgress(first);
        interrupted.ChangeProgressPage = (page, _) =>
        {
            if (page == 1) throw new HttpRequestException("Lost native listing page");
        };
        Assert.Equal("Unknown", (string?)JsonNode.Parse((await FactorySourceInventoryTests.RunAsync(interrupted, "/status")).Body)!["state"]);
        Assert.Empty(interrupted.Writes);
        using var resumed = new SourceInventoryTransport { ProgressPageSize = 10 };
        Populate(resumed);
        resumed.ImportProgress(interrupted);
        var result = await FactorySourceInventoryTests.RunAsync(resumed, "/status");
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(resumed.Requests.Count(r => r.Contains(new Uri(resumed.ProgressEndpoint).Host + "/kv?", StringComparison.Ordinal)) > 10);
        Assert.Empty(resumed.Writes);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("proof")]
    [InlineData("protocol")]
    public async Task An_unfinished_or_unproven_integrity_read_never_reports_global_completion(string damage)
    {
        using var first = new SourceInventoryTransport();
        Populate(first);
        await AcquireAsync(first);
        using var reader = new SourceInventoryTransport { ProgressPageSize = 10 };
        Populate(reader);
        reader.ImportProgress(first);
        if (damage == "deadline")
            reader.ChangeProgressPage = (page, _) => { if (page == 1) reader.Clock.Now += TimeSpan.FromSeconds(46); };
        else if (damage == "proof")
            reader.Progress.Remove(reader.Progress.Keys.Single(k => k.Contains("/integrity/", StringComparison.Ordinal)));
        else
        {
            var key = reader.Progress.Keys.Single(k => k.EndsWith("/current", StringComparison.Ordinal));
            var stored = reader.Progress[key];
            var json = JsonNode.Parse(stored.Value)!;
            json.AsObject().Remove("integrityProtocol");
            reader.Progress[key] = (json.ToJsonString(), stored.Revision + 1);
        }
        var result = await FactorySourceInventoryTests.RunAsync(reader, "/status");
        Assert.Equal(damage == "deadline" ? "Partial" : "Unknown", (string?)JsonNode.Parse(result.Body)!["state"]);
        Assert.Empty(reader.Writes);
        Assert.DoesNotContain("\"state\":\"Acquired\"", result.Body);
    }

    [Theory]
    [InlineData("/status", "missing-chunk")]
    [InlineData("/acquire", "missing-chunk")]
    [InlineData("/status", "corrupt-chunk")]
    [InlineData("/acquire", "corrupt-chunk")]
    [InlineData("/status", "missing-result")]
    [InlineData("/acquire", "missing-result")]
    [InlineData("/status", "corrupt-node")]
    [InlineData("/acquire", "corrupt-node")]
    public async Task Nonselected_page_damage_invalidates_global_completion_across_serialized_workers(string route, string damage)
    {
        using var first = new SourceInventoryTransport();
        Populate(first);
        var original = await AcquireAsync(first);
        Assert.Equal("Acquired", (string?)original["state"]);
        var kind = damage.EndsWith("chunk", StringComparison.Ordinal) ? "chunks" : damage.EndsWith("result", StringComparison.Ordinal) ? "results" : "nodes";
        var key = first.Progress.Keys.Single(k => k.Contains("/" + kind + "/00000033", StringComparison.Ordinal));
        if (damage.StartsWith("missing", StringComparison.Ordinal)) first.Progress.Remove(key);
        else
        {
            var stored = first.Progress[key];
            var changed = JsonNode.Parse(stored.Value)!;
            if (kind == "chunks") changed["sha256"] = new string('0', 64);
            else changed["path"] = "a-valid-but-wrong-path.py";
            first.Progress[key] = (changed.ToJsonString(), stored.Revision + 1);
        }
        using var second = new SourceInventoryTransport();
        Populate(second);
        second.ImportProgress(first);
        var result = await FactorySourceInventoryTests.RunAsync(second, route);
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        var coverage = JsonNode.Parse(result.Body)!;
        Assert.Equal("Unknown", (string?)coverage["state"]);
        Assert.NotEqual("Acquired", (string?)coverage["progress"]?["state"]);
        if (route == "/status") Assert.Empty(second.Writes);
        else Assert.NotEqual("Acquired", (string?)JsonNode.Parse(second.Progress.Single(p => p.Key.EndsWith("/current", StringComparison.Ordinal)).Value.Value)!["state"]);
    }

    [Fact]
    public async Task Native_empty_page_continuation_does_not_prove_a_pristine_namespace()
    {
        using var transport = new SourceInventoryTransport { EmptyProgressPages = 1 };
        transport.Progress["source/orphan"] = ("{}", 1);
        var result = await FactorySourceInventoryTests.RunAsync(transport, "/status");
        Assert.Equal("Unknown", (string?)JsonNode.Parse(result.Body)!["state"]);
        Assert.Equal(2, transport.Requests.Count(r => r.Contains(new Uri(transport.ProgressEndpoint).Host + "/kv?", StringComparison.Ordinal)));
        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("malformed")]
    [InlineData("missing-items")]
    public async Task Native_incomplete_or_untrusted_listing_never_initializes_progress(string damage)
    {
        using var transport = new SourceInventoryTransport();
        transport.ChangeProgressPage = (_, response) =>
        {
            if (damage == "missing-items") response.Content = new StringContent("{}");
            else response.Headers.TryAddWithoutValidation("Link", damage == "foreign"
                ? "<https://outside.invalid/kv?key=source%2F*&label=*&$select=key>; rel=\"next\"" : "malformed");
        };
        var result = await FactorySourceInventoryTests.RunAsync(transport);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(result.Body)!["state"]);
        Assert.Empty(transport.Writes);
        Assert.DoesNotContain(transport.Requests, r => r.Contains("outside.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lost_initialization_response_is_held_without_resetting_its_reserved_lifecycle()
    {
        using var first = new SourceInventoryTransport();
        first.AfterWrite = (key, _) =>
        {
            if (key.EndsWith("/initialized", StringComparison.Ordinal)) throw new HttpRequestException("Lost initialization response");
        };
        Assert.Equal("Unknown", (string?)JsonNode.Parse((await FactorySourceInventoryTests.RunAsync(first)).Body)!["state"]);
        using var second = new SourceInventoryTransport();
        second.ImportProgress(first);
        Assert.Equal("Unknown", (string?)JsonNode.Parse((await FactorySourceInventoryTests.RunAsync(second)).Body)!["state"]);
        Assert.Empty(second.Writes);
        Assert.Single(second.Progress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_initializer_cannot_replace_a_lifecycle_after_the_pristine_observation(bool loseCurrent)
    {
        using var winner = new SourceInventoryTransport();
        Assert.Equal("Acquired", (string?)(await AcquireAsync(winner))["state"]);
        if (loseCurrent) winner.Progress.Remove(winner.Progress.Keys.Single(k => k.EndsWith("/current", StringComparison.Ordinal)));
        using var contender = new SourceInventoryTransport();
        contender.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Put && Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).EndsWith("/initialized", StringComparison.Ordinal))
                contender.ImportProgress(winner);
        };
        var result = await FactorySourceInventoryTests.RunAsync(contender);
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Empty(contender.Writes);
        Assert.Equal(winner.Progress.Count, contender.Progress.Count);
    }

    [Theory]
    [InlineData("/acquire")]
    [InlineData("/status")]
    public async Task Missing_current_with_existing_remote_inventory_never_resets_lifecycle_accounting(string route)
    {
        using var first = new SourceInventoryTransport();
        Populate(first);
        var acquired = await AcquireAsync(first);
        Assert.Equal("Acquired", (string?)acquired["state"]);
        Assert.Equal(first.Progress.Count, (int)acquired["progress"]!["records"]!);
        var key = first.Progress.Keys.Single(k => k.EndsWith("/current", StringComparison.Ordinal));
        first.Progress.Remove(key);
        using var second = new SourceInventoryTransport();
        Populate(second);
        second.ImportProgress(first);
        var result = route == "/acquire" ? await AcquireAsync(second)
            : JsonNode.Parse((await FactorySourceInventoryTests.RunAsync(second, route)).Body)!;
        Assert.Equal("Unknown", (string?)result["state"]);
        Assert.Contains("MissingAuthoritativeCheckpoint", (string?)result["detail"]);
        Assert.Empty(second.Writes);
        Assert.Equal(first.Progress.Count, second.Progress.Count);
        Assert.False(second.Progress.ContainsKey(key));
    }

    private static void Populate(SourceInventoryTransport transport) =>
        transport.RootTree = transport.Tree(Enumerable.Range(0, 40)
            .Select(i => ($"file-{i:D2}.py", "100644", transport.Blob("print('first party')"))).ToArray());

    private static async Task<JsonNode> AcquireAsync(SourceInventoryTransport transport, SourceAcquireRequest? request = null)
    {
        JsonNode? result = null;
        for (var i = 0; i < 24; i++)
        {
            var response = await FactorySourceInventoryTests.RunAsync(transport, request: request);
            result = JsonNode.Parse(response.Body)!;
            if ((string?)result["state"] != "Partial") return result;
        }
        Assert.Fail("Acquisition did not reach a bounded terminal observation: " + result);
        return result!;
    }
}
