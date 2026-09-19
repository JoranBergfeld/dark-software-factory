using System.Text.Json.Nodes;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryCharterReconciliationTests
{
    private const string Root = "dsf/factory/charter/publication";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Original_response_witness_reconciles_after_reconstruction_without_another_GitHub_write(int effect)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.LoseResponseWhen = (key, value) => key == Root
            && JsonNode.Parse(value)!["effects"] is JsonArray effects && effects.Count == effect
            && (string?)effects[^1]!["state"] == "Dispatched" && effects[^1]!["responseRecord"] is not null;
        var interrupted = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Unknown", (string?)interrupted.Result["state"]);
        scenario.Reconstruct();
        var inspection = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        Assert.Equal(effect, scenario.GitHubWrites.Count);
        Assert.NotNull(inspection.Result["publication"]!["effects"]![effect - 1]!["receipt"]);
        Assert.NotNull(inspection.Result["publication"]!["effects"]![effect - 1]!["objectId"]);
        var result = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)inspection.Result["publication"]!["operation"]!, "--revision", (string)inspection.Result["revision"]!);
        Assert.Equal(effect == 3 ? "Published" : "Partial", (string?)result.Result["state"]);
        Assert.Equal("Confirmed", (string?)result.Result["publication"]!["effects"]![effect - 1]!["state"]);
        Assert.NotNull(result.Result["publication"]!["effects"]![effect - 1]!["reconciliation"]);
        Assert.Equal(effect, scenario.GitHubWrites.Count);
        Assert.Null(result.Result["evidence"]);
        Assert.DoesNotContain("responseJson", result.Terminal.Output);
        scenario.Reconstruct();
        var retained = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        Assert.Equal(result.Result["publication"]!.ToJsonString(), retained.Result["publication"]!.ToJsonString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Lost_original_response_is_not_recovered_from_matching_remote_objects(int effect)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.LoseEffect = effect;
        Assert.Equal("Unknown", (string?)(await scenario.Machine.InvokeAsync("charter", "--publish",
            "--approve", (string)review.Result["revision"]!)).Result["state"]);
        scenario.Reconstruct();
        var inspection = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var original = scenario.Arm.Product.Export();
        var result = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)inspection.Result["publication"]!["operation"]!, "--revision", (string)inspection.Result["revision"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal(effect, scenario.GitHubWrites.Count);
        Assert.Equal(original, scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Explicit_rejected_retry_preserves_original_identity_and_history_across_clients()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.RejectEffect = 2;
        var rejected = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Failed", (string?)rejected.Result["state"]);
        scenario.Reconstruct();
        var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var result = await scenario.Machine.InvokeAsync("charter", "--retry",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!,
            "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Published", (string?)result.Result["state"]);
        Assert.Equal(inspected.Result["publication"]!["operation"]!.ToJsonString(), result.Result["publication"]!["operation"]!.ToJsonString());
        Assert.Single(result.Result["publication"]!["effects"]![1]!["previousAttempts"]!.AsArray());
        Assert.Equal(4, scenario.GitHubWrites.Count);
        Assert.All(scenario.GitHubWrites, args => Assert.Equal("graphql", args[^1]));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("factoryId")]
    [InlineData("repositoryId")]
    [InlineData("attempt")]
    [InlineData("responseHash")]
    [InlineData("revision")]
    [InlineData("missing")]
    public async Task Copied_or_conflicting_original_response_binding_is_never_adopted(string damage)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.LoseResponseWhen = (key, value) => key == Root
            && JsonNode.Parse(value)!["effects"] is JsonArray effects && effects.Count == 1 && effects[0]!["responseRecord"] is not null;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        scenario.Reconstruct();
        var original = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var publication = original.Result["publication"]!;
        var responseKey = Dsf.Core.Onboarding.FactoryCharterResponses.Key((Guid)publication["operation"]!, (Guid)publication["effects"]![0]!["attempt"]!);
        if (damage == "missing") scenario.Arm.Product.Remove(responseKey);
        else scenario.Arm.Product.Change(responseKey, text =>
        {
            var witness = JsonNode.Parse(text)!;
            if (damage == "revision") return text;
            if (damage == "repositoryId") witness[damage] = 42;
            else if (damage == "responseHash") witness["native"]!["responseSha256"] = new string('a', 64);
            else witness[damage] = Guid.NewGuid();
            return witness.ToJsonString();
        });
        if (damage is not ("revision" or "missing"))
        {
            var native = JsonNode.Parse(scenario.Arm.Product.ListJson())!.AsArray().Single(r => (string?)r!["key"] == responseKey)!;
            scenario.Arm.Product.Change(Root, text =>
            {
                var root = JsonNode.Parse(text)!;
                root["effects"]![0]!["responseRecord"]!["revisionSha256"] = Dsf.Core.Onboarding.FactoryAudit.TextHash((string)native["etag"]!);
                root["effects"]![0]!["responseRecord"]!["sha256"] = Dsf.Core.Onboarding.FactoryAudit.TextHash((string)native["value"]!);
                return root.ToJsonString();
            });
        }
        var damaged = scenario.Arm.Product.Export();
        var result = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)original.Result["publication"]!["operation"]!, "--revision", (string)original.Result["revision"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Single(scenario.GitHubWrites);
        Assert.Equal(damaged, scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Historical_positive_reconciliation_ignores_current_readiness_but_does_not_overwrite_human_branch()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.LoseResponseWhen = (key, value) => key == Root
            && JsonNode.Parse(value)!["effects"] is JsonArray effects && effects.Count == 2
            && (string?)effects[^1]!["state"] == "Dispatched" && effects[^1]!["responseRecord"] is not null;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        scenario.Reconstruct();
        scenario.HumanBranchEdit = true;
        scenario.CanPush = false;
        var inspection = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var result = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)inspection.Result["publication"]!["operation"]!, "--revision", (string)inspection.Result["revision"]!);
        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.Equal("Confirmed", (string?)result.Result["publication"]!["effects"]![1]!["state"]);
        Assert.Equal(2, scenario.GitHubWrites.Count);
        var stale = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)inspection.Result["publication"]!["operation"]!, "--revision", (string)inspection.Result["revision"]!);
        Assert.Equal("Blocked", (string?)stale.Result["state"]);
        var continuation = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Blocked", (string?)continuation.Result["state"]);
        Assert.Equal(2, scenario.GitHubWrites.Count);
    }

    [Fact]
    public async Task A_rejected_retry_never_overwrites_a_human_edited_branch()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.RejectEffect = 2;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        scenario.Reconstruct();
        scenario.HumanBranchEdit = true;
        var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var result = await scenario.Machine.InvokeAsync("charter", "--retry",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!,
            "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Equal(2, scenario.GitHubWrites.Count);
    }

    [Fact]
    public async Task Matching_head_on_a_recreated_human_ref_is_not_the_original_branch()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.RejectEffect = 3;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        scenario.Reconstruct();
        var native = scenario.Machine.Boundary.Repository.NativeCharter!;
        scenario.Machine.Boundary.Repository.NativeCharter = (args, input) =>
        {
            var response = native(args, input);
            if (response is null || !args[^1].EndsWith(Uri.EscapeDataString(scenario.Branch!), StringComparison.Ordinal)) return response;
            return response with { StandardOutput = response.StandardOutput.Replace("\"node_id\":\"BRANCH\"", "\"node_id\":\"HUMAN_REF\"", StringComparison.Ordinal) };
        };
        var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var result = await scenario.Machine.InvokeAsync("charter", "--retry",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!,
            "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }

    [Fact]
    public async Task Duplicate_operators_have_one_conditional_reconciliation_winner()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.LoseResponseWhen = (key, value) => key == Root
            && JsonNode.Parse(value)!["effects"] is JsonArray effects && effects.Count == 1 && effects[0]!["responseRecord"] is not null;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        scenario.Reconstruct();
        var inspection = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = 0;
        scenario.Arm.Product.BeforePut = async (key, _) =>
        {
            if (key != Root || Interlocked.Increment(ref writers) != 1) return;
            entered.TrySetResult();
            await release.Task;
        };
        var args = new[] { "--reconcile", "--operation", (string)inspection.Result["publication"]!["operation"]!,
            "--revision", (string)inspection.Result["revision"]! };
        var first = scenario.Machine.InvokeAsync("charter", args);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            var second = await scenario.Machine.InvokeAsync("charter", args);
            Assert.Equal("Partial", (string?)second.Result["state"]);
        }
        finally { release.TrySetResult(); }
        var stale = await first;
        Assert.Contains((string?)stale.Result["state"], new[] { "Blocked", "Unknown" });
        Assert.Single(scenario.GitHubWrites);
        Assert.Equal(2, writers);
        scenario.Reconstruct();
        Assert.Equal("Confirmed", (string?)(await scenario.Machine.InvokeAsync("charter", "--inspect-publication"))
            .Result["publication"]!["effects"]![0]!["state"]);
    }

    [Fact]
    public async Task Original_pre_request_failure_can_retry_under_the_same_operation()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.AfterPut = (key, value) =>
        {
            if (key == Root && JsonNode.Parse(value)!["effects"] is JsonArray effects
                && effects.Count == 1 && (string?)effects[0]!["state"] == "Dispatched")
                scenario.CanPush = false;
        };
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Empty(scenario.GitHubWrites);
        scenario.Reconstruct();
        var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        Assert.Equal("NotDispatched", (string?)inspected.Result["publication"]!["effects"]![0]!["state"]);
        scenario.CanPush = true;
        var result = await scenario.Machine.InvokeAsync("charter", "--retry",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!,
            "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Published", (string?)result.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
        Assert.Equal(inspected.Result["publication"]!["branch"]!.ToJsonString(), result.Result["publication"]!["branch"]!.ToJsonString());
    }

    [Fact]
    public async Task GraphQL_partial_data_and_errors_are_not_success_proof()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        var native = scenario.Machine.Boundary.Repository.NativeCharter!;
        scenario.Machine.Boundary.Repository.NativeCharter = (args, input) =>
        {
            var response = native(args, input);
            if (input is null || args[^1] != "graphql" || response is null) return response;
            var text = response.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
            var body = JsonNode.Parse(text[(text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..])!;
            body["errors"] = new JsonArray(new JsonObject { ["message"] = "test-only-private-provider-error" });
            return new GhInvocationResult(0, "HTTP/2 200\nX-GitHub-Request-ID: PARTIAL\n\n" + body.ToJsonString(), "");
        };
        var failed = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Unknown", (string?)failed.Result["state"]);
        scenario.Reconstruct();
        var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        var result = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        var pause = await scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.Contains(pause.Result["issuedPublications"]!.AsArray(), p =>
            (string?)p!["kind"] == "Charter" && (string?)p["state"] == "Unknown");
        Assert.False((bool)pause.Result["stopCompleted"]!);
        var retry = await scenario.Machine.InvokeAsync("charter", "--retry",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!,
            "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Blocked", (string?)retry.Result["state"]);
        Assert.Single(scenario.GitHubWrites);
        Assert.DoesNotContain("test-only-private-provider-error", result.Terminal.Output + failed.Terminal.Output);
    }

    [Fact]
    public async Task Bounded_attempt_history_blocks_before_a_ninth_native_mutation()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.RejectEffect = 1;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        for (var attempt = 2; attempt <= 8; attempt++)
        {
            scenario.Reconstruct();
            scenario.RejectEffect = attempt == 8 ? null : attempt;
            var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
            var result = await scenario.Machine.InvokeAsync("charter", "--retry",
                "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!,
                "--approve", (string)review.Result["revision"]!);
            Assert.Equal(attempt == 8 ? "Partial" : "Failed", (string?)result.Result["state"]);
        }
        Assert.Equal(8, scenario.GitHubWrites.Count);
        scenario.Reconstruct();
        var resumed = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Partial", (string?)resumed.Result["state"]);
        Assert.Equal(8, scenario.GitHubWrites.Count);
    }

    [Theory]
    [InlineData("--reconcile")]
    [InlineData("--retry")]
    [InlineData("--inspect-publication")]
    public async Task Redirected_recovery_requires_complete_explicit_input_without_provider_access(string action)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        Assert.Equal(1, await CliApplication.InvokeAsync(["onboard", "decide", "charter", action, "--json"], CancellationToken.None, terminal));
        Assert.Contains("Blocked", terminal.Output);
        Assert.Empty(terminal.Prompts);
    }
}
