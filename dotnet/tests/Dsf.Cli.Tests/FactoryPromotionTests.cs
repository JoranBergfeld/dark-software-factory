using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryPromotionTests(PromotableFactoryFixture fixture) : IClassFixture<PromotableFactoryFixture>
{
    [Fact]
    public async Task Aged_original_preview_cannot_authorize_first_final_promotion_despite_fresh_current_telemetry()
    {
        var transport = await RolloutAsync();
        var now = transport.Scenario.Source.Source!.Clock.Now;
        try
        {
            var original = transport.SeedDocuments.Single(p => p.Key.Contains("/council-preview/docs/preview-", StringComparison.Ordinal));
            var evaluation = JsonNode.Parse(original.Value)!;
            transport.Scenario.Source.Source.Clock.Now = ((DateTimeOffset)evaluation["inputs"]!["telemetry"]!["capture"]!["rows"]![0]!["reference"]!["recordTime"]!)
                .AddSeconds((int)evaluation["inputs"]!["telemetryConfiguration"]!["freshnessSeconds"]!).AddMinutes(2);
            Assert.True((DateTimeOffset)JsonNode.Parse(transport.Values()[Dsf.Core.Runtime.ProductConfigurationKeys.FactoryModels])!["verifiedUntil"]!
                > transport.Scenario.Source.Source.Clock.Now);
            var review = await transport.Scenario.Machine.InvokeAsync("activate", "--promote", "--operation", Guid.NewGuid().ToString());
            Assert.Equal(1, review.Exit);
            Assert.False((bool)review.Result["currentReady"]!);
            Assert.False((bool)review.Result["isActivated"]!);
            Assert.False((bool)review.Result["historicallyCompleted"]!);
            Assert.False(transport.Values().ContainsKey(FactoryPromotion.RootKey));
            Assert.Equal(original.Value, transport.SeedDocuments[original.Key]);
            Assert.Equal(0, transport.IssuePosts);
        }
        finally { transport.Restart(now); }
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("pending")]
    [InlineData("active")]
    [InlineData("completion")]
    public async Task Pause_before_each_conditional_promotion_boundary_never_opens_current_admission(string boundary)
    {
        var transport = await RolloutAsync();
        var machine = transport.Scenario.Machine;
        var operation = Guid.NewGuid();
        var review = await machine.InvokeAsync("activate", "--promote", "--operation", operation.ToString());
        Assert.True(review.Exit == 0, review.Terminal.Output);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Scenario.Arm.Product.BeforePut = async (key, create) =>
        {
            var target = boundary switch
            {
                "intent" => key == FactoryPromotion.IntentKey,
                "pending" => key == FactoryPromotion.RootKey && create,
                "active" => key == FactoryPromotion.RootKey && !create,
                _ => key == FactoryPromotion.CompletionKey(operation)
            };
            if (!target) return;
            transport.Scenario.Arm.Product.BeforePut = null;
            reached.TrySetResult();
            await release.Task;
        };
        var activating = machine.InvokeAsync("activate", "--promote", "--operation", operation.ToString(),
            "--approve", (string)review.Result["approvalFingerprint"]!);
        await reached.Task.WaitAsync(TimeSpan.FromMinutes(3));
        var pause = await machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
        release.TrySetResult();
        var raced = await activating;
        Assert.Equal(1, raced.Exit);
        Assert.False((bool)raced.Result["isActivated"]!);
        var status = await machine.InvokeAsync("activate", "--status");
        Assert.False((bool)status.Result["isActivated"]!);
        var creates = transport.SeedCreates;
        var retry = await machine.InvokeAsync("activate", "--promote", "--operation", operation.ToString(),
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal(1, retry.Exit);
        Assert.Equal(creates, transport.SeedCreates);
        Assert.Equal(0, transport.IssuePosts);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("canceled")]
    public async Task Unacknowledged_native_activation_seed_is_discovered_but_never_adopted_or_replayed(string failure)
    {
        var transport = await RolloutAsync();
        var operation = Guid.NewGuid().ToString();
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("activate", "--promote", "--operation", operation);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        transport.SeedFailure = failure;
        var result = await machine.InvokeAsync("activate", "--promote", "--operation", operation,
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["isActivated"]!);
        Assert.False((bool)result.Result["historicallyCompleted"]!);
        Assert.Contains(transport.SeedDocuments.Keys, k => k.Contains("/publication-index-", StringComparison.Ordinal));
        var native = transport.SeedDocuments.ToDictionary();
        transport.SeedFailure = null;
        var retried = await machine.InvokeAsync("activate", "--promote", "--operation", operation,
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal(1, retried.Exit);
        Assert.Equal(native, transport.SeedDocuments);
        var audit = await machine.InvokeAsync("audit");
        Assert.Equal("Unknown", (string?)audit.Result["state"]);
        Assert.Contains(audit.Result["records"]!.AsArray(), r => (string?)r!["identity"]!["store"] == "Publication"
            && ((string)r["identity"]!["key"]!).Contains("/publication-index-", StringComparison.Ordinal));
        Assert.Equal(0, transport.IssuePosts);
    }

    [Fact]
    public async Task Promotion_contenders_cannot_overwrite_original_review_and_stale_configuration_cannot_promote()
    {
        var transport = await RolloutAsync();
        var machine = transport.Scenario.Machine;
        var first = Guid.NewGuid().ToString(); var second = Guid.NewGuid().ToString();
        var a = await machine.InvokeAsync("activate", "--promote", "--operation", first);
        var b = await machine.InvokeAsync("activate", "--promote", "--operation", second);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Scenario.Arm.Product.BeforePut = async (key, _) =>
        {
            if (key != FactoryPromotion.IntentKey) return;
            transport.Scenario.Arm.Product.BeforePut = null;
            reached.TrySetResult();
            await release.Task;
        };
        var loser = machine.InvokeAsync("activate", "--promote", "--operation", first, "--approve", (string)a.Result["approvalFingerprint"]!);
        await reached.Task.WaitAsync(TimeSpan.FromMinutes(3));
        var winner = await machine.InvokeAsync("activate", "--promote", "--operation", second, "--approve", (string)b.Result["approvalFingerprint"]!);
        release.TrySetResult();
        Assert.True(winner.Exit == 0, winner.Terminal.Output);
        Assert.Equal(1, (await loser).Exit);
        Assert.Equal(Guid.Parse(second), (Guid)JsonNode.Parse(transport.Values()[FactoryPromotion.RootKey])!["operationId"]!);
        var root = transport.Values()[FactoryPromotion.RootKey];
        transport.PublisherPermissionDrift = true;
        var status = await machine.InvokeAsync("activate", "--status");
        Assert.Equal(1, status.Exit);
        Assert.False((bool)status.Result["isActivated"]!);
        Assert.True((bool)status.Result["historicallyCompleted"]!);
        Assert.Equal(root, transport.Values()[FactoryPromotion.RootKey]);
        Assert.Equal(0, transport.IssuePosts);
    }

    private Task<PreviewPreparationTransport> RolloutAsync() => Task.FromResult(fixture.Reset());

    [Fact]
    public async Task Audit_cannot_claim_complete_discovery_for_an_unconfirmed_pre_promotion_writer_protocol()
    {
        var transport = await RolloutAsync();
        transport.BeforeIngressRequest = uri =>
        {
            if (uri.AbsolutePath == "/activation-capability") throw new HttpRequestException("Original pinned writer protocol unavailable.");
            return Task.CompletedTask;
        };
        var audit = await transport.Scenario.Machine.InvokeAsync("audit");
        Assert.Equal(1, audit.Exit);
        Assert.Equal("Unknown", (string?)audit.Result["state"]);
        Assert.Null(audit.Result["receipt"]);
    }

    [Fact]
    public async Task Explicit_final_promotion_activates_only_current_native_ready_factory_without_rewriting_prior_receipts()
    {
        var transport = fixture.Reset();
        var machine = transport.Scenario.Machine;
        var before = transport.Values();
        var originalNative = transport.SeedDocuments.ToDictionary();
        var operation = Guid.NewGuid().ToString();
        var final = await machine.InvokeAsync("activate", "--promote", "--operation", operation);
        Assert.True(final.Exit == 0, final.Terminal.Output + final.Terminal.Error);
        Assert.Equal("Reviewed", (string?)final.Result["state"]);
        Assert.True((bool)final.Result["approvalRetained"]!);
        Assert.True((bool)final.Result["approvalCurrent"]!);
        Assert.False((bool)final.Result["isActivated"]!);
        Assert.False((bool)final.Result["historicallyCompleted"]!);
        Assert.Equal(before, transport.Values());
        Assert.Equal(originalNative, transport.SeedDocuments);
        var activated = await machine.InvokeAsync("activate", "--promote", "--operation", operation,
            "--approve", (string)final.Result["approvalFingerprint"]!);
        Assert.True(activated.Exit == 0, activated.Terminal.Output + activated.Terminal.Error
            + "\n" + string.Join("\n", transport.HostFailures.TakeLast(3)));
        Assert.Equal("Activated", (string?)activated.Result["state"]);
        Assert.True((bool)activated.Result["approvalRetained"]!);
        Assert.True((bool)activated.Result["isActivated"]!);
        Assert.True((bool)activated.Result["historicallyCompleted"]!);
        Assert.True((bool)activated.Result["currentReady"]!);
        Assert.False((bool)activated.Result["firstConfirmedFiling"]!);
        foreach (var pair in before) Assert.Equal(pair.Value, transport.Values()[pair.Key]);
        var status = await machine.InvokeAsync("activate", "--status");
        Assert.True(status.Exit == 0, status.Terminal.Output);
        Assert.True((bool)status.Result["isActivated"]!);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == "api.github.com"
            && r.Method == "POST" && r.Uri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal));
        var live = FactoryLive.ReadConfirmed(transport.Values().Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)).ToDictionary(),
            transport.Scenario.Source.Source!.Clock.Now);
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        var models = transport.EvaluationInference.Count;
        using var admitted = await http.PostAsJsonAsync(live.Endpoint + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync());
        JsonNode? decision = null;
        for (var i = 0; i < 1000; i++)
        {
            using var observed = await http.PostAsJsonAsync(live.Endpoint + "/decision", request, FactorySourceValidation.RecordJson);
            decision = JsonNode.Parse(await observed.Content.ReadAsStringAsync())!;
            if (observed.StatusCode == HttpStatusCode.OK) break;
            Assert.True(observed.StatusCode == HttpStatusCode.Accepted, decision.ToJsonString());
            await Task.Delay(100);
        }
        Assert.True((string?)decision?["readiness"] == "ReadyAwaitingActivation",
            decision?.ToJsonString() + "\n" + string.Join("\n", transport.HostFailures.TakeLast(4)));
        Assert.Equal(16, transport.EvaluationInference.Count - models);
        var publication = Assert.Single(decision!["evaluation"]!["publications"]!.AsArray())!;
        Assert.Equal("Confirmed", (string?)publication["state"]);
        Assert.Single(transport.PublishedIssues);
        Assert.Equal(1, transport.IssuePosts);
        Assert.DoesNotContain(request.InvocationId.ToString("N"), (string)publication["journal"]!["intent"]!["logicalIntent"]!, StringComparison.Ordinal);
        Assert.Contains(transport.Manifest.Identity.FactoryId.ToString("D"), (string)publication["journal"]!["intent"]!["logicalIntent"]!, StringComparison.Ordinal);
        transport.PublishedIssues[0]["title"] = "Human-owned title";
        transport.PublishedIssues[0]["body"] = "Human-owned body";
        using var duplicate = await http.PostAsJsonAsync(live.Endpoint + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.Contains(duplicate.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal("Human-owned title", (string?)transport.PublishedIssues[0]["title"]);
        status = await machine.InvokeAsync("activate", "--status");
        Assert.True(status.Exit == 0, status.Terminal.Output);
        Assert.True((bool)status.Result["firstConfirmedFiling"]!);
        var audited = new List<JsonNode?>();
        for (var page = 0; ; page++)
        {
            var audit = await machine.InvokeAsync("audit", "--page", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(audit.Exit == 0, audit.Terminal.Output + audit.Terminal.Error);
            audited.AddRange(audit.Result["records"]!.AsArray().Select(r => r!.DeepClone()));
            if (audit.Result["nextPage"] is null) break;
        }
        foreach (var marker in new[] { "decision-index-", "publication-index-", "problem-index-", "decision-", "proposal-" })
            Assert.Contains(audited, r => ((string)r!["identity"]!["key"]!).Contains("/docs/" + marker, StringComparison.Ordinal));
        Assert.Contains(audited, r => (string?)r!["identity"]!["store"] == "Run"
            && ((string)r["identity"]!["key"]!).Contains("decision-", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Requests, r => r.Method is "PATCH" or "DELETE"
            || r.Uri.Host == "api.github.com" && r.Method != "GET" && r.Method != "POST");
        await machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        status = await machine.InvokeAsync("activate", "--status");
        Assert.Equal(1, status.Exit);
        Assert.False((bool)status.Result["isActivated"]!);
        Assert.True((bool)status.Result["historicallyCompleted"]!);
        Assert.False((bool)status.Result["currentReady"]!);
        Assert.True((bool)status.Result["firstConfirmedFiling"]!);
    }
}

public sealed class PromotableFactoryFixture : IAsyncLifetime
{
    private readonly PublisherFixture publisher = new();
    public async Task InitializeAsync()
    {
        await publisher.InitializeAsync(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(5));
        var transport = publisher.Reset();
        var selection = FactoryPublisherTests.Selection().ToJsonString();
        var review = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--selection", selection);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        var rollout = await transport.Scenario.Machine.InvokeAsync("live", "--publication", "--selection", selection,
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(rollout.Exit == 0, rollout.Terminal.Output);
        publisher.CaptureCurrent();
    }
    internal PreviewPreparationTransport Reset() => publisher.Reset();
    public Task DisposeAsync() => publisher.DisposeAsync();
}
