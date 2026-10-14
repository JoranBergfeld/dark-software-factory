using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryPauseTests
{
    private const string Key = "dsf/factory/pause";

    [Fact]
    public async Task Explicit_operator_pause_is_durable_without_readiness_or_a_completed_stop_claim()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var machine = scenario.Machine;
        var before = scenario.Arm.Product.Export();
        var inspection = await machine.InvokeAsync("pause");
        Assert.True(inspection.Exit == 0, inspection.Terminal.Output + inspection.Terminal.Error);
        Assert.Equal("NotRequested", (string?)inspection.Result["state"]);
        Assert.False((bool)inspection.Result["admissionClosed"]!);
        Assert.False((bool)inspection.Result["stopCompleted"]!);
        Assert.Equal(before, scenario.Arm.Product.Export());

        var operation = Guid.NewGuid();
        var requested = await machine.InvokeAsync("pause", "--request", "--operation", operation.ToString());
        Assert.True(requested.Exit == 0, requested.Terminal.Output + requested.Terminal.Error);
        Assert.Equal("DrainingUnknown", (string?)requested.Result["state"]);
        Assert.True((bool)requested.Result["admissionClosed"]!);
        Assert.False((bool)requested.Result["stopCompleted"]!);
        Assert.False((bool)requested.Result["isActivated"]!);
        var pause = requested.Result["request"]!;
        var manifest = JsonNode.Parse(scenario.Source.Values()[ProductConfigurationKeys.FactoryManifest])!;
        Assert.Equal(operation, (Guid?)pause["operationId"]);
        Assert.Equal((Guid?)manifest["identity"]!["factoryId"], (Guid?)pause["factoryId"]);
        Assert.Equal((long?)manifest["identity"]!["repositoryId"], (long?)pause["repositoryId"]);
        Assert.Equal((string?)manifest["identity"]!["azureActorId"], (string?)pause["requestedBy"]);
        Assert.Equal((string?)manifest["profilePin"], (string?)pause["profilePin"]);
        Assert.NotEmpty((string)pause["manifestRevision"]!);
        var writes = scenario.Arm.Product.Writes.Where(w => w.Key == Key || w.Key.StartsWith("dsf/factory/audit/pause/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.All(writes, w => Assert.Equal("*", w.IfNoneMatch));
        Assert.All(writes, w => Assert.Null(w.IfMatch));
        Assert.True(writes[0].Key == Key, "The admission-closing root must precede the audit copy.");

        var saved = scenario.Arm.Product.Export();
        scenario.Reconstruct();
        var status = await machine.InvokeAsync("pause");
        Assert.Equal("DrainingUnknown", (string?)status.Result["state"]);
        Assert.True(JsonNode.DeepEquals(pause, status.Result["request"]));
        Assert.Equal(saved, scenario.Arm.Product.Export());
        var repeated = await machine.InvokeAsync("pause", "--request", "--operation", operation.ToString());
        Assert.Equal("DrainingUnknown", (string?)repeated.Result["state"]);
        Assert.Equal(saved, scenario.Arm.Product.Export());
        Assert.Empty(scenario.Arm.Product.Writes);
    }

    [Theory]
    [InlineData("missing-operation")]
    [InlineData("unexpected-operation")]
    [InlineData("empty-operation")]
    [InlineData("missing-locators")]
    public async Task Redirected_pause_requires_exact_explicit_inputs_before_provider_access(string invalid)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        string[] locators = invalid == "missing-locators" ? [] :
            ["--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
                "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription];
        string[] action = invalid switch
        {
            "unexpected-operation" => ["--operation", Guid.NewGuid().ToString()],
            "empty-operation" => ["--request", "--operation", Guid.Empty.ToString()],
            _ => ["--request"],
        };
        var exit = await CliApplication.InvokeAsync(["onboard", "decide", "pause", "--json", .. locators, .. action],
            CancellationToken.None, terminal);
        Assert.Equal(1, exit);
        Assert.Contains("\"state\": \"Blocked\"", terminal.Output);
        Assert.Empty(terminal.Prompts);
        Assert.Empty(terminal.Error);
    }

    [Theory]
    [InlineData("root-response")]
    [InlineData("audit-rejected")]
    [InlineData("root-missing")]
    public async Task Interrupted_pause_never_reopens_admission_or_recreates_missing_authority(string interruption)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var operation = Guid.NewGuid();
        var auditKey = $"dsf/factory/audit/pause/{operation:N}/request";
        if (interruption == "root-response") scenario.Arm.Product.LoseResponseForKey = Key;
        if (interruption == "audit-rejected") scenario.Arm.Product.RejectKey = auditKey;
        var requested = await scenario.Machine.InvokeAsync("pause", "--request", "--operation", operation.ToString());
        Assert.Equal(interruption == "root-missing" ? 0 : 1, requested.Exit);
        if (interruption == "root-response") Assert.Equal("Unknown", (string?)requested.Result["state"]);
        if (interruption == "audit-rejected") Assert.Equal("Partial", (string?)requested.Result["state"]);
        if (interruption == "root-missing") scenario.Arm.Product.Remove(Key);
        scenario.Reconstruct();
        var before = scenario.Arm.Product.Export();
        var repeated = await scenario.Machine.InvokeAsync("pause", "--request", "--operation", operation.ToString());
        Assert.Equal(1, repeated.Exit);
        Assert.Equal(interruption == "root-missing" ? "Unknown" : "Partial", (string?)repeated.Result["state"]);
        Assert.True((bool)repeated.Result["admissionClosed"]!);
        Assert.False((bool)repeated.Result["stopCompleted"]!);
        Assert.Equal(before, scenario.Arm.Product.Export());
        Assert.Empty(scenario.Arm.Product.Writes);
    }

    [Fact]
    public async Task Pause_competitors_cannot_replace_the_first_native_request()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var arrivals = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Arm.Product.BeforePut = async (key, create) =>
        {
            if (key != FactoryLifecycleCoordinator.Key || !create) return;
            if (Interlocked.Increment(ref arrivals) == 2) both.SetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(60));
        };
        var contenders = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString())));
        Assert.Single(contenders, r => r.Exit == 0 && (string?)r.Result["state"] == "DrainingUnknown");
        Assert.Single(contenders, r => r.Exit == 1);
        Assert.All(scenario.Arm.Product.Writes.Where(w => w.Key == Key), w => Assert.Equal("*", w.IfNoneMatch));
        Assert.Single(JsonNode.Parse(scenario.Arm.Product.ListJson())!.AsArray(), r => (string?)r!["key"] == Key);
        var current = await scenario.Machine.InvokeAsync("pause");
        Assert.Equal("DrainingUnknown", (string?)current.Result["state"]);
        Assert.True(JsonNode.DeepEquals(contenders.Single(r => r.Exit == 0).Result["request"], current.Result["request"]));
    }

    [Fact]
    public async Task Real_hosted_council_rejects_operator_pause_before_any_admission_or_model_dispatch()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var stopped = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.True(stopped.Exit == 0, stopped.Terminal.Output + stopped.Terminal.Error);
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var start = transport.Requests.Count;
        var evaluated = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.Equal(1, evaluated.Exit);
        Assert.Equal("Blocked", (string?)evaluated.Result["readiness"]);
        Assert.Contains("DeliberatePause", (string?)evaluated.Result["reason"]);
        Assert.Empty(transport.EvaluationInference);
        Assert.Equal(native, transport.SeedDocuments);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }

    [Fact]
    public async Task Pause_after_operator_checks_is_enforced_by_the_actual_HTTP_admission_host()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var requested = false;
        transport.BeforeIngressRequest = async uri =>
        {
            if (uri.AbsolutePath != "/evaluate" || requested) return;
            requested = true;
            var result = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.True(result.Exit == 0, result.Terminal.Output);
        };
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var evaluated = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True(requested);
        Assert.Equal("Blocked", (string?)evaluated.Result["readiness"]);
        Assert.Contains("DeliberatePause", (string?)evaluated.Result["reason"]);
        Assert.Empty(transport.EvaluationInference);
        Assert.Equal(native, transport.SeedDocuments);
    }

    [Fact]
    public async Task Pause_during_an_issued_model_call_retains_unknown_effect_and_never_dispatches_a_second_model()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeEvaluationResponse = async (_, token) =>
        {
            if (entered.TrySetResult()) await release.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
        };
        var pins = PreviewEvaluationTests.Pins(transport);
        var evaluation = transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(180));
            var stopped = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            Assert.Equal("DrainingUnknown", (string?)stopped.Result["state"]);
            Assert.False((bool)stopped.Result["stopCompleted"]!);
            release.TrySetResult();
            var result = await evaluation;
            Assert.False((bool)result.Result["currentReady"]!);
            transport.Restart();
            Assert.Single(transport.EvaluationInference);
            var preview = transport.SeedDocuments.Select(p => JsonNode.Parse(p.Value)!)
                .Single(r => (string?)r["kind"] == "council-preview-evaluation");
            Assert.Null(preview["proof"]);
            Assert.Contains(preview["dispatches"]!.AsArray(), d => (string?)d!["state"] is "Unknown" or "Dispatched");
            var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
            var resumed = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
            Assert.Equal("Blocked", (string?)resumed.Result["readiness"]);
            Assert.Contains("DeliberatePause", (string?)resumed.Result["reason"]);
            Assert.Equal(native, transport.SeedDocuments);
            Assert.Single(transport.EvaluationInference);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Stop_winning_the_native_admission_race_quarantines_the_contender_before_worker_dispatch()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var raced = false;
        var began = false;
        transport.BeforeEvaluationWrite = body =>
        {
            began |= (string?)body["admission"]?["state"] == "Running";
            if (raced || (string?)body["admission"]?["state"] != "Queued") return;
            raced = true;
            var stopped = transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString())
                .GetAwaiter().GetResult();
            Assert.Equal(0, stopped.Exit);
        };
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True(raced);
        Assert.False((bool)result.Result["currentReady"]!);
        transport.Restart();
        Assert.False(began);
        Assert.Empty(transport.EvaluationInference);
        var native = JsonNode.Parse(Assert.Single(transport.SeedDocuments).Value)!;
        Assert.Equal("Unknown", (string?)native["admission"]?["state"]);
        Assert.Null(native["inputs"]);
        Assert.Null(native["run"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_pause_reference_closure_never_certifies_a_missing_coordinator(bool removeRoot)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var operation = Guid.NewGuid();
        var requested = await scenario.Machine.InvokeAsync("pause", "--request", "--operation", operation.ToString());
        Assert.Equal(0, requested.Exit);
        if (removeRoot) scenario.Arm.Product.Remove(Key);
        using var transport = new FactoryAuditTests.AuditTransport(scenario);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await scenario.Machine.InvokeAsync("audit");
        if (removeRoot)
        {
            Assert.Equal(1, result.Exit);
            Assert.Equal("Unknown", (string?)result.Result["state"]);
            Assert.Null(result.Result["receipt"]);
            return;
        }
        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
        var records = result.Result["records"]!.AsArray();
        var root = records.Single(r => (string?)r!["identity"]!["key"] == Key)!;
        var references = root["references"]!.AsArray();
        Assert.Contains(references, r => (string?)r!["key"] == $"dsf/factory/audit/pause/{operation:N}/request");
        var history = records.Single(r => (string?)r!["identity"]!["key"] == $"dsf/factory/audit/pause/{operation:N}/request")!;
        Assert.Contains(history["references"]!.AsArray(), r => (string?)r!["key"] == ProductConfigurationKeys.FactoryProfile);
        Assert.Contains(history["references"]!.AsArray(), r => (string?)r!["key"] == ProductConfigurationKeys.FactoryPlanChunk(0));
        Assert.False((bool)result.Result["currentAuthorization"]!);
    }
}
