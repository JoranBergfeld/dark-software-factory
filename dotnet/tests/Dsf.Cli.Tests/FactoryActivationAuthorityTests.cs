using System.Text.Json.Nodes;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryActivationAuthorityTests(ActivationAuthorityFixture fixture) : IClassFixture<ActivationAuthorityFixture>
{
    [Fact]
    public async Task Genuine_serialized_operator_approval_cannot_enable_shared_runtime_work_or_a_live_boolean()
    {
        var transport = await fixture.ResetApprovedAsync();
        var before = transport.Scenario.Arm.Product.Export();
        using var http = new HttpClient(transport.Scenario.Arm.Product.Client());
        var dependencies = RuntimeDependencies.Production(new Dictionary<string, string?>()) with
        {
            ConfigurationSettingsGateway = new AzureConfigurationSettingsGateway(new ReadOnlyCredential(), http),
            ConveyorComposer = new ForbiddenComposer(),
            SweepLeaseFactory = _ => throw new InvalidOperationException("Forbidden sweep lease effect."),
        };
        var settings = new RuntimeSettings("widget", $"https://dsfo-{transport.Manifest.Identity.FactoryId:N}.azconfig.io", "", "",
            "https://cosmos.invalid", "https://model.invalid", "gpt", "embedding", "", "", "", "");
        var env = new Dictionary<string, string?>
        {
            [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true",
            [RuntimeIntegrationSettings.FactoryProfilePin] = transport.Manifest.ProfilePin,
        };
        var run = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(settings,
            SignalReader.ReadFromJson("""{"product_hints":"widget","source_kinds":["azuremonitor"]}""", "operator-approved-test", false),
            dependencies, CancellationToken.None, env));
        Assert.Contains("Verified product governance", run.Message);
        var sweep = await Assert.ThrowsAsync<RuntimeVerbException>(() =>
            RuntimeVerbs.SweepAsync(settings, false, dependencies, CancellationToken.None, env));
        Assert.Contains("Verified product governance", sweep.Message);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Empty(transport.Scenario.Arm.Product.Writes);
        Assert.Equal(fixture.ModelCalls, transport.EvaluationInference.Count);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("manifest-revision")]
    [InlineData("native-run-body")]
    public async Task Stale_or_unverifiable_review_cannot_write_approval(string change)
    {
        var transport = fixture.Reset();
        var operation = Guid.NewGuid().ToString();
        var review = await Review(transport, operation);
        var pin = (string)review["approvalFingerprint"]!;
        if (change == "fingerprint") pin = new string('a', 64);
        if (change == "manifest-revision")
            transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, value => value);
        if (change == "native-run-body")
        {
            var run = transport.SeedDocuments.Single(p => p.Key.Contains("/runs/", StringComparison.Ordinal));
            var body = JsonNode.Parse(run.Value)!;
            body["proposals"]![0]!["body"] = "Not the reviewed native content";
            transport.SeedDocuments[run.Key] = body.ToJsonString();
        }
        var before = transport.Scenario.Arm.Product.Export();
        var result = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation, "--approve", pin);
        Assert.Equal(1, result.Exit);
        Assert.False((bool)result.Result["approvalRetained"]!);
        Assert.False((bool)result.Result["approvalCurrent"]!);
        Assert.False((bool)result.Result["isActivated"]!);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("missing-review")]
    [InlineData("missing-receipt")]
    [InlineData("null-references")]
    [InlineData("wrong-analysis-chain")]
    public async Task Missing_or_corrupt_provenance_cannot_certify_approval_or_audit_closure(string damage)
    {
        var transport = await fixture.ResetApprovedAsync();
        var values = transport.Values();
        var root = JsonNode.Parse(values[FactoryActivationTests.Root])!;
        var operation = (Guid)root["operationId"]!;
        var reviewKey = FactoryActivationTests.Prefix + operation.ToString("N") + "/review";
        var approvalKey = FactoryActivationTests.Prefix + operation.ToString("N") + "/approval";
        var product = transport.Scenario.Arm.Product;
        if (damage == "missing-root") product.Remove(FactoryActivationTests.Root);
        if (damage == "missing-review") product.Remove(reviewKey);
        if (damage == "missing-receipt") product.Remove(approvalKey);
        if (damage is "null-references" or "wrong-analysis-chain")
        {
            var review = JsonNode.Parse(values[reviewKey])!;
            if (damage == "null-references") review["references"] = null;
            else review["analysisChain"] = new string('a', 64);
            product.Change(reviewKey, _ => review.ToJsonString());
            if (damage == "wrong-analysis-chain")
            {
                var changed = review.Deserialize<FactoryActivationReview>(FactorySourceValidation.RecordJson)!;
                root["reviewFingerprint"] = changed.Fingerprint;
                var receipt = JsonNode.Parse(values[approvalKey])!;
                receipt["reviewFingerprint"] = changed.Fingerprint;
                root["approvalSha256"] = FactoryModelsValidation.Hash(receipt.Deserialize<FactoryActivationApproval>(FactorySourceValidation.RecordJson)!);
                product.Change(approvalKey, _ => receipt.ToJsonString());
                product.Change(FactoryActivationTests.Root, _ => root.ToJsonString());
            }
        }
        var before = product.Export();
        var audit = await transport.Scenario.Machine.InvokeAsync("audit");
        Assert.Equal(1, audit.Exit);
        Assert.False((bool)audit.Result["currentAuthorization"]!);
        var status = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.Equal(1, status.Exit);
        Assert.False((bool)status.Result["approvalCurrent"]!);
        Assert.False((bool)status.Result["historicallyCompleted"]!);
        Assert.Equal(before, product.Export());
        Assert.Empty(product.Writes);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("coordinator")]
    [InlineData("review")]
    [InlineData("receipt")]
    [InlineData("confirmation")]
    public async Task Operator_pause_winning_an_approval_boundary_never_yields_current_authority(string at)
    {
        var transport = fixture.Reset();
        var operation = Guid.NewGuid();
        var review = await Review(transport, operation.ToString());
        var paused = false;
        async Task Pause()
        {
            paused = true;
            var result = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.Equal("DrainingUnknown", (string?)result.Result["state"]);
        }
        if (at == "before") await Pause();
        else transport.Scenario.Arm.Product.BeforePut = async (key, create) =>
        {
            if (paused) return;
            if (at == "coordinator" && key == FactoryActivationTests.Root && create
                || at == "review" && key.EndsWith("/review", StringComparison.Ordinal)
                || at == "receipt" && key.EndsWith("/approval", StringComparison.Ordinal)
                || at == "confirmation" && key == FactoryActivationTests.Root && !create) await Pause();
        };
        var result = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation.ToString(),
            "--approve", (string)review["approvalFingerprint"]!);
        Assert.True(paused);
        Assert.Equal(1, result.Exit);
        Assert.False((bool)result.Result["approvalCurrent"]!);
        Assert.False((bool)result.Result["isActivated"]!);
        Assert.False((bool)result.Result["historicallyCompleted"]!);
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var before = transport.Scenario.Arm.Product.Export();
        var retry = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation.ToString(),
            "--approve", (string)review["approvalFingerprint"]!);
        Assert.Equal(1, retry.Exit);
        Assert.False((bool)retry.Result["approvalCurrent"]!);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Equal(fixture.ModelCalls, transport.EvaluationInference.Count);
    }

    [Theory]
    [InlineData("manifest-before-review-write")]
    [InlineData("manifest-before-receipt")]
    [InlineData("coordinator-before-confirmation")]
    public async Task Changed_native_authority_or_coordinator_wins_without_stale_approval_promotion(string race)
    {
        var transport = fixture.Reset();
        var operation = Guid.NewGuid().ToString();
        var review = await Review(transport, operation);
        var product = transport.Scenario.Arm.Product;
        var changed = false;
        product.BeforePut = (key, create) =>
        {
            if (!changed && (race == "manifest-before-review-write" && key.EndsWith("/review", StringComparison.Ordinal)
                || race == "manifest-before-receipt" && key.EndsWith("/approval", StringComparison.Ordinal)
                || race == "coordinator-before-confirmation" && key == FactoryActivationTests.Root && !create))
            {
                changed = true;
                if (race.StartsWith("manifest", StringComparison.Ordinal))
                    product.Change(ProductConfigurationKeys.FactoryManifest, value => value);
                else product.Change(FactoryActivationTests.Root, value =>
                {
                    var root = JsonNode.Parse(value)!;
                    root["phase"] = "Rejected";
                    return root.ToJsonString();
                });
            }
            return Task.CompletedTask;
        };
        var result = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation,
            "--approve", (string)review["approvalFingerprint"]!);
        Assert.True(changed);
        Assert.Equal(1, result.Exit);
        Assert.Contains((string?)result.Result["state"], new[] { "Blocked", "Unknown" });
        Assert.False((bool)result.Result["approvalCurrent"]!);
        Assert.False((bool)result.Result["isActivated"]!);
        if (race == "coordinator-before-confirmation")
            Assert.Equal("Rejected", (string?)JsonNode.Parse(transport.Values()[FactoryActivationTests.Root])!["phase"]);
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var before = transport.Scenario.Arm.Product.Export();
        var retry = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation,
            "--approve", (string)review["approvalFingerprint"]!);
        Assert.Equal(1, retry.Exit);
        Assert.False((bool)retry.Result["approvalCurrent"]!);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData("root-lost")]
    [InlineData("review-lost")]
    [InlineData("receipt-lost")]
    [InlineData("receipt-rejected")]
    [InlineData("receipt-canceled")]
    [InlineData("final-lost")]
    public async Task Interrupted_native_writes_never_claim_unconfirmed_receipt_or_replay(string failure)
    {
        var transport = fixture.Reset();
        var operation = Guid.NewGuid();
        var review = await Review(transport, operation.ToString());
        var product = transport.Scenario.Arm.Product;
        var receiptKey = FactoryActivationTests.Prefix + operation.ToString("N") + "/approval";
        if (failure == "root-lost") product.LoseResponseForKey = FactoryActivationTests.Root;
        if (failure == "review-lost") product.LoseResponseForKey = FactoryActivationTests.Prefix + operation.ToString("N") + "/review";
        if (failure == "receipt-lost") product.LoseResponseForKey = receiptKey;
        if (failure == "receipt-rejected") product.RejectKey = receiptKey;
        if (failure == "receipt-canceled") product.BeforePut = (key, _) =>
            key == receiptKey ? throw new OperationCanceledException() : Task.CompletedTask;
        if (failure == "final-lost") product.BeforePut = (key, create) =>
        {
            if (key == FactoryActivationTests.Root && !create) product.LoseResponseForKey = key;
            return Task.CompletedTask;
        };
        var result = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation.ToString(),
            "--approve", (string)review["approvalFingerprint"]!);
        Assert.Equal(1, result.Exit);
        Assert.Equal(failure == "receipt-rejected" ? "Failed" : "Unknown", (string?)result.Result["state"]);
        Assert.Equal(failure == "final-lost", (bool)result.Result["approvalRetained"]!);
        Assert.False((bool)result.Result["approvalCurrent"]!);
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var before = transport.Scenario.Arm.Product.Export();
        var status = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.Equal(failure == "final-lost" ? "ApprovalOnly" : "Partial", (string?)status.Result["state"]);
        Assert.Equal(failure is "receipt-lost" or "final-lost", (bool)status.Result["approvalRetained"]!);
        Assert.Equal(failure == "final-lost", (bool)status.Result["approvalCurrent"]!);
        var retry = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation.ToString(),
            "--approve", (string)review["approvalFingerprint"]!);
        Assert.Equal(1, retry.Exit);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Empty(transport.Scenario.Arm.Product.Writes);
    }

    [Fact]
    public async Task Native_duplicate_contenders_have_one_immutable_operator_approval()
    {
        var transport = fixture.Reset();
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var reviews = new[] { await Review(transport, ids[0]), await Review(transport, ids[1]) };
        var arrivals = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Scenario.Arm.Product.BeforePut = async (key, create) =>
        {
            if (key != FactoryActivationTests.Root || !create) return;
            if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(120));
        };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => transport.Scenario.Machine.InvokeAsync("activate",
            "--operation", ids[i], "--approve", (string)reviews[i]["approvalFingerprint"]!)));
        Assert.Single(results, r => (string?)r.Result["state"] == "ApprovalOnly");
        Assert.Single(results, r => (string?)r.Result["state"] == "Blocked");
        Assert.Single(transport.Values(), p => p.Key.StartsWith(FactoryActivationTests.Prefix, StringComparison.Ordinal)
            && p.Key.EndsWith("/approval", StringComparison.Ordinal));
        var writes = transport.Scenario.Arm.Product.Writes.Where(w => w.Key == FactoryActivationTests.Root).ToArray();
        Assert.Equal(2, writes.Count(w => w.IfNoneMatch == "*"));
        Assert.Single(writes, w => !string.IsNullOrEmpty(w.IfMatch) && w.IfNoneMatch is null);
        Assert.Equal(fixture.ModelCalls, transport.EvaluationInference.Count);
    }

    [Fact]
    public async Task Transient_readiness_failure_retains_approval_but_stop_never_reopens()
    {
        var transport = fixture.Reset();
        var operation = Guid.NewGuid().ToString();
        var review = await Review(transport, operation);
        var approved = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation, "--approve", (string)review["approvalFingerprint"]!);
        Assert.Equal("ApprovalOnly", (string?)approved.Result["state"]);
        var before = transport.Scenario.Arm.Product.Export();
        transport.AlterApplication = body => { body["tables"]![0]!["rows"] = new JsonArray(); return body; };
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var unavailable = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.Equal(1, unavailable.Exit);
        Assert.True((bool)unavailable.Result["approvalRetained"]!);
        Assert.False((bool)unavailable.Result["currentReady"]!);
        Assert.False((bool)unavailable.Result["approvalCurrent"]!);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        transport.AlterApplication = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var recovered = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.Equal("ApprovalOnly", (string?)recovered.Result["state"]);
        Assert.True((bool)recovered.Result["approvalCurrent"]!);
        Assert.False((bool)recovered.Result["historicallyCompleted"]!);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        var paused = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.True((bool)paused.Result["approvalRetained"]!);
        Assert.False((bool)paused.Result["approvalCurrent"]!);
        Assert.False((bool)paused.Result["currentReady"]!);
    }

    private static async Task<JsonNode> Review(PreviewPreparationTransport transport, string operation)
    {
        var review = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation);
        Assert.True(review.Exit == 0, review.Terminal.Output + review.Terminal.Error);
        return review.Result;
    }

    internal sealed class ForbiddenComposer : IConveyorComposer
    {
        public ConveyorServices ComposeFor(RuntimeSettings settings) => throw new InvalidOperationException("Forbidden conveyor composition.");
    }

    internal sealed class ReadOnlyCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext context, CancellationToken token) =>
            new("offline-runtime-reader", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext context, CancellationToken token) =>
            ValueTask.FromResult(GetToken(context, token));
    }
}

public sealed class ActivationAuthorityFixture : IAsyncLifetime
{
    private PreviewPreparationTransport transport = null!;
    private string product = "";
    private Dictionary<string, string> native = [];
    private string? approvedProduct;
    public int ModelCalls { get; private set; }

    public async Task InitializeAsync()
    {
        transport = await FactoryActivationTests.ReadyAsync();
        product = transport.Scenario.Arm.Product.Export();
        native = transport.SeedDocuments.ToDictionary();
        ModelCalls = transport.EvaluationInference.Count;
    }

    internal PreviewPreparationTransport Reset()
    {
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(product);
        transport.SeedDocuments.Clear();
        foreach (var pair in native) transport.SeedDocuments.Add(pair.Key, pair.Value);
        transport.AlterApplication = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        return transport;
    }

    internal async Task<PreviewPreparationTransport> ResetApprovedAsync()
    {
        Reset();
        if (approvedProduct is null)
        {
            var operation = Guid.NewGuid().ToString();
            var review = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation);
            Assert.True(review.Exit == 0, review.Terminal.Output);
            var approval = await transport.Scenario.Machine.InvokeAsync("activate", "--operation", operation,
                "--approve", (string)review.Result["approvalFingerprint"]!);
            Assert.Equal("ApprovalOnly", (string?)approval.Result["state"]);
            approvedProduct = transport.Scenario.Arm.Product.Export();
        }
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(approvedProduct);
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        return transport;
    }

    public Task DisposeAsync() { transport?.Dispose(); return Task.CompletedTask; }
}
