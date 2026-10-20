using System.Text.Json.Nodes;
using Dsf.Cli;
using Xunit;
using static Dsf.Cli.Tests.FactoryFoundationTests;

namespace Dsf.Cli.Tests;

public sealed class FactoryFoundationRecoveryTests
{
    [Fact]
    public async Task Admitted_retry_cannot_masquerade_as_the_previous_no_effect_attempt()
    {
        var owner = new ReservationTestStore();
        var prime = new ReservationMachine(owner);
        var reviewed = await prime.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await prime.ReserveAsync(fingerprint);
        prime.Boundary.Repository.ReservationHttp = new HttpClient(new FoundationTransport(owner) { RejectPut = true });
        Assert.Equal(1, (await prime.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var first = new ReservationMachine(owner);
        var provider = new FoundationTransport(owner) { CompleteCreates = true, CompleteDeployment = true };
        var barrier = new DispatchBarrier(provider, "after-retry-admission");
        first.Boundary.Repository.ReservationHttp = new HttpClient(barrier);
        var pending = first.InvokeAsync("resume");
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = owner.Export();
        var second = new ReservationMachine(owner);
        second.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        try
        {
            var observed = await second.InvokeAsync("resume");
            Assert.Equal(1, observed.Exit);
            Assert.Equal("Unknown", (string?)observed.Result["foundation"]?["state"]);
            Assert.NotNull(observed.Result["foundation"]?["retryAdmission"]?["clientRequestId"]);
            Assert.Equal(before, owner.Export());
            Assert.Empty(provider.Writes);
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(0, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Exit);
        Assert.Equal(3, provider.Writes.Count);
    }

    [Theory]
    [InlineData("root-ack", "Unknown")]
    [InlineData("receipt-ack", "Unknown")]
    [InlineData("root-conflict", "Blocked")]
    public async Task Recovery_lost_acknowledgments_and_conditional_conflicts_keep_authoritative_progress(string failure, string state)
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await first.ReserveAsync(fingerprint);
        first.Store.LoseResponseWhen = (key, value) => key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
            && (int?)JsonNode.Parse(value)?["index"] == 2 && (string?)JsonNode.Parse(value)?["state"] == "Completed";
        var provider = new FoundationTransport(first.Store) { CompleteCreates = true };
        first.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        Assert.Equal(1, (await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        var restored = new FoundationTransport(second.Store) { Product = ReservationTestStore.Restore(provider.Product.Export()) };
        restored.RestoreNative(provider.ExportNative());
        second.Boundary.Repository.ReservationHttp = new HttpClient(restored);
        second.Store.LoseResponseWhen = (key, value) => failure == "root-ack" && key == "dsf/onboarding/products/widget"
            || failure == "receipt-ack" && key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
                && (int?)JsonNode.Parse(value)?["index"] == 3 && (string?)JsonNode.Parse(value)?["state"] == "Completed";
        second.Store.BeforePut = (key, _) =>
        {
            if (failure == "root-conflict" && key == "dsf/onboarding/products/widget")
            {
                second.Store.BeforePut = null;
                second.Store.Change(key, value =>
                {
                    var root = JsonNode.Parse(value)!;
                    root["foundation"]!["recoveryDetail"] = "newer remote coordinator";
                    return root.ToJsonString();
                });
            }
            return Task.CompletedTask;
        };

        var interrupted = await second.InvokeAsync("resume");

        Assert.Equal(1, interrupted.Exit);
        Assert.Equal(state, (string?)interrupted.Result["foundation"]?["state"]);
        Assert.Empty(restored.Writes);
        if (failure != "receipt-ack") Assert.Empty(restored.Product.Writes);
        if (failure == "root-conflict") Assert.Contains("newer remote coordinator", second.Store.Export());
        var third = new ReservationMachine(ReservationTestStore.Restore(second.Store.Export()));
        var final = new FoundationTransport(third.Store) { Product = ReservationTestStore.Restore(restored.Product.Export()) };
        final.RestoreNative(restored.ExportNative());
        third.Boundary.Repository.ReservationHttp = new HttpClient(final);
        var result = await third.InvokeAsync("resume");
        Assert.Equal("Blocked", (string?)result.Result["foundation"]?["state"]);
        Assert.Contains("dsf/factory/manifest", final.Product.Values().Keys);
        Assert.Empty(final.Writes);
        Assert.DoesNotContain(final.Product.Writes, write => write.Key == "dsf/factory/profile");
        if (failure == "receipt-ack")
            Assert.DoesNotContain(final.Product.Writes, write => write.Key == "dsf/factory/reviewed-plan/0000");
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("surviving-history")]
    [InlineData("changed-assets")]
    [InlineData("missing-foundation-pointer")]
    public async Task Recovery_never_treats_missing_or_changed_authority_as_pristine(string damage)
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await first.ReserveAsync(fingerprint);
        var provider = new FoundationTransport(first.Store);
        first.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        const string rootKey = "dsf/onboarding/products/widget";
        if (damage == "missing-root") second.Store.Remove(rootKey);
        else
        {
            if (damage == "changed-assets")
                foreach (var key in second.Store.Values().Keys.Where(key => key.StartsWith("dsf/foundation/", StringComparison.Ordinal)))
                    second.Store.Remove(key);
            second.Store.Change(rootKey, value =>
            {
                var root = JsonNode.Parse(value)!;
                if (damage == "missing-foundation-pointer") root.AsObject().Remove("foundation");
                else
                {
                    root["foundation"]!["operationCount"] = 0;
                    if (damage == "changed-assets") root["foundation"]!["requestSha256"] = new string('0', 64);
                }
                return root.ToJsonString();
            });
        }
        var remote = new FoundationTransport(second.Store) { CompleteCreates = true, CompleteDeployment = true };
        second.Boundary.Repository.ReservationHttp = new HttpClient(remote);
        var before = second.Store.Export();

        var result = await second.InvokeAsync("resume");

        Assert.Equal(1, result.Exit);
        Assert.Equal(damage == "changed-assets" ? "Blocked" : "Unknown", (string?)result.Result["state"]);
        Assert.Equal(before, second.Store.Export());
        Assert.Empty(remote.Writes);
        Assert.Empty(remote.Product.Writes);
        Assert.Equal(1, (await second.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        Assert.Equal(before, second.Store.Export());
        Assert.Empty(remote.Writes);
    }

    [Theory]
    [InlineData("recreated-cosmos", "Blocked")]
    [InlineData("recreated-identity", "Blocked")]
    [InlineData("lost-native-operations", "Unknown")]
    [InlineData("lost-owner-receipt", "Unknown")]
    [InlineData("newer-publication", "Blocked")]
    [InlineData("request-revision", "Blocked")]
    [InlineData("correlation", "Unknown")]
    [InlineData("current-provisioning-state", "Blocked")]
    [InlineData("terminal-state-change", "Blocked")]
    public async Task Reconstructed_deployment_recovery_requires_original_and_current_identity_evidence(string damage, string state)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new ReservationMachine(new ReservationTestStore()) { Token = cancellation.Token };
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await first.ReserveAsync(fingerprint);
        first.Store.AfterPut = (key, value) =>
        {
            if (key.StartsWith("dsf/foundation/", StringComparison.Ordinal) && (string?)JsonNode.Parse(value)?["state"] == "InFlight")
                cancellation.Cancel();
        };
        var provider = new FoundationTransport(first.Store)
        { CompleteCreates = true, CompleteDeployment = true, AsyncDeployment = true, IncludeCreationResponses = true, CosmosNativeReceipts = true };
        first.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        Assert.Equal(130, (await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        var product = ReservationTestStore.Restore(provider.Product.Export());
        var native = JsonNode.Parse(provider.ExportNative())!.AsObject();
        if (damage == "recreated-cosmos")
            native.Single(p => p.Key.EndsWith("/containers/runs", StringComparison.Ordinal)).Value!["properties"]!["resource"]!["_rid"] = "AAAAAAABAAA=";
        if (damage == "recreated-identity")
            native.Single(p => p.Key.Contains("/userassignedidentities/", StringComparison.Ordinal) && p.Key.EndsWith("-council", StringComparison.Ordinal))
                .Value!["properties"]!["principalId"] = Guid.NewGuid().ToString();
        if (damage == "lost-native-operations") native.Remove(native.Single(p => p.Key.EndsWith("/operations", StringComparison.Ordinal)).Key);
        if (damage == "correlation")
            native.Single(p => p.Key.Contains("/deployments/", StringComparison.Ordinal) && !p.Key.EndsWith("/operations", StringComparison.Ordinal))
                .Value!["properties"]!["correlationId"] = Guid.NewGuid().ToString();
        if (damage == "newer-publication") product.Change("dsf/factory/manifest", value => value);
        if (damage == "current-provisioning-state")
            native.Single(p => p.Key.Contains("/microsoft.network/virtualnetworks/", StringComparison.Ordinal))
                .Value!["properties"]!["provisioningState"] = "Updating";
        var steps = second.Store.Values().Where(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)).ToArray();
        if (damage == "lost-owner-receipt") second.Store.Remove(steps.First(p => (string?)JsonNode.Parse(p.Value)?["deploymentTarget"] is not null).Key);
        if (damage == "request-revision")
            second.Store.Change(steps.Single(p => ((string?)JsonNode.Parse(p.Value)?["target"])?.Contains("/deployments/", StringComparison.Ordinal) == true).Key,
                value => { var step = JsonNode.Parse(value)!; step["requestSha256"] = new string('a', 64); return step.ToJsonString(); });
        if (damage == "terminal-state-change")
            second.Store.Change(steps.Single(p => ((string?)JsonNode.Parse(p.Value)?["target"])?.Contains("/deployments/", StringComparison.Ordinal) == true).Key,
                value => { var step = JsonNode.Parse(value)!; step["state"] = "Canceled"; step["disposition"] = "Unproven"; return step.ToJsonString(); });
        var restored = new FoundationTransport(second.Store) { Product = product };
        restored.RestoreNative(native.ToJsonString());
        second.Boundary.Repository.ReservationHttp = new HttpClient(restored);
        var before = second.Store.Export();

        var result = await second.InvokeAsync("resume");

        Assert.Equal(1, result.Exit);
        Assert.Equal(state, (string?)result.Result["foundation"]?["state"]);
        Assert.Equal(before, second.Store.Export());
        Assert.Empty(restored.Writes);
        Assert.Empty(product.Writes);
        Assert.Empty(result.Terminal.Error);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("status")]
    public async Task Interactive_recovery_prompts_only_for_nonsecret_remote_locator_and_prints_explicit_command(string action)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        var terminal = new ScriptedTerminal(new(true, false, false),
            ["https://owner.azconfig.io", "widget", BoundaryPlanFixture.Tenant, BoundaryPlanFixture.Subscription]);

        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal, ["onboard", "decide", action]);

        Assert.Equal(0, exit);
        Assert.Equal(4, terminal.Prompts.Count);
        Assert.Contains("dsf onboard decide " + action + " --owner-appconfig-endpoint https://owner.azconfig.io --product widget", terminal.Output);
        Assert.Contains("--tenant " + BoundaryPlanFixture.Tenant + " --subscription " + BoundaryPlanFixture.Subscription, terminal.Output);
        Assert.Empty(terminal.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_redirected_or_json_recovery_never_prompts_or_authenticates(bool json)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var terminal = new ScriptedTerminal(new(json, false, false), []);
        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal,
            json ? ["onboard", "decide", "resume", "--json"] : ["onboard", "decide", "resume"]);
        Assert.Equal(1, exit);
        Assert.Empty(terminal.Prompts);
        Assert.Empty(machine.Boundary.Calls);
        Assert.Empty(machine.Store.Writes);
    }

    [Fact]
    public async Task Known_rejection_with_no_effect_can_continue_same_step_retaining_original_attempt()
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        var (_, reserved, _) = await first.ReserveAsync(fingerprint);
        var denied = new FoundationTransport(first.Store) { RejectPut = true };
        first.Boundary.Repository.ReservationHttp = new HttpClient(denied);
        var original = await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);
        Assert.Equal("NoEffect", (string?)original.Result["foundation"]!["operations"]![0]!["disposition"]);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        var provider = new FoundationTransport(second.Store)
        { CompleteCreates = true, CompleteDeployment = true, EnforceCorrelation = true };
        second.Boundary.Repository.ReservationHttp = new HttpClient(provider);

        var (exit, result, _) = await second.InvokeAsync("resume");

        Assert.True(exit == 0, (string?)result["detail"]);
        Assert.True(JsonNode.DeepEquals(reserved["identity"], result["identity"]));
        Assert.Equal(3, provider.Writes.Count);
        var step = result["foundation"]!["operations"]![0]!;
        Assert.Equal("Completed", (string?)step["state"]);
        var prior = Assert.Single(step["previousAttempts"]!.AsArray())!;
        Assert.Equal("NoEffect", (string?)prior["disposition"]);
        Assert.Equal(403, (int?)prior["httpStatus"]);
        Assert.Equal((string?)original.Result["foundation"]!["operations"]![0]!["clientRequestId"], (string?)prior["clientRequestId"]);
        Assert.NotEqual((string?)prior["clientRequestId"], (string?)step["clientRequestId"]);
    }

    [Theory]
    [InlineData("request-revision")]
    [InlineData("configuration-recreated")]
    [InlineData("configuration-policy")]
    [InlineData("profile-recreated")]
    [InlineData("profile-newer")]
    public async Task Changed_input_or_current_incarnation_never_authorizes_partial_publication_reuse(string damage)
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await first.ReserveAsync(fingerprint);
        first.Store.LoseResponseWhen = (key, value) => key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
            && (int?)JsonNode.Parse(value)?["index"] == 2 && (string?)JsonNode.Parse(value)?["state"] == "Completed";
        var provider = new FoundationTransport(first.Store) { CompleteCreates = true };
        first.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        Assert.Equal(1, (await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        var product = ReservationTestStore.Restore(provider.Product.Export());
        var native = JsonNode.Parse(provider.ExportNative())!.AsObject();
        var config = native.Single(p => p.Key.Contains("/configurationstores/", StringComparison.Ordinal)).Value!;
        if (damage == "configuration-recreated") config["properties"]!["creationDate"] = DateTimeOffset.UtcNow.AddSeconds(1);
        if (damage == "configuration-policy") config["properties"]!["disableLocalAuth"] = false;
        if (damage is "profile-recreated" or "profile-newer")
            product.Change("dsf/factory/profile", value => damage == "profile-recreated" ? value : value.Replace("inactive-v1", "changed-v1", StringComparison.Ordinal));
        if (damage == "request-revision")
        {
            var key = second.Store.Values().Single(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
                && (int?)JsonNode.Parse(p.Value)?["index"] == 1).Key;
            second.Store.Change(key, value =>
            {
                var step = JsonNode.Parse(value)!;
                step["requestSha256"] = new string('a', 64);
                return step.ToJsonString();
            });
        }
        var restored = new FoundationTransport(second.Store) { Product = product };
        restored.RestoreNative(native.ToJsonString());
        second.Boundary.Repository.ReservationHttp = new HttpClient(restored);
        var before = second.Store.Export();
        var beforeProduct = product.Export();

        var (exit, result, _) = await second.InvokeAsync("resume");

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)result["foundation"]?["state"]);
        Assert.Equal(before, second.Store.Export());
        Assert.Equal(beforeProduct, product.Export());
        Assert.Empty(restored.Writes);
        Assert.Empty(product.Writes);
    }

    [Theory]
    [InlineData("before-admission", "Inactive", 0, true)]
    [InlineData("after-admission", "Unknown", 1, false)]
    [InlineData("after-intent", "Unknown", 1, false)]
    [InlineData("after-provider", "Unknown", 1, false)]
    [InlineData("after-response", "Unknown", 1, false)]
    [InlineData("after-group", "Blocked", 1, true)]
    [InlineData("after-configuration", "Blocked", 1, true)]
    [InlineData("before-profile-admission", "Blocked", 1, true)]
    [InlineData("after-profile-admission", "Unknown", 1, false)]
    [InlineData("after-profile-intent", "Unknown", 1, false)]
    [InlineData("after-profile-provider", "Unknown", 1, false)]
    [InlineData("after-profile", "Blocked", 1, true)]
    [InlineData("after-target-intent", "Unknown", 1, false)]
    [InlineData("before-deployment", "Unknown", 1, false)]
    [InlineData("after-deployment-provider", "Unknown", 1, false)]
    [InlineData("after-deployment", "Blocked", 1, true)]
    public async Task Delayed_original_actor_cannot_dispatch_conflicting_work_across_recovery_boundaries(
        string boundary, string state, int exit, bool takeover)
    {
        var owner = new ReservationTestStore();
        var first = new ReservationMachine(owner);
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await first.ReserveAsync(fingerprint);
        var provider = new FoundationTransport(owner) { CompleteCreates = true, CompleteDeployment = true, AsyncDeployment = true, IncludeCreationResponses = true };
        var barrier = new DispatchBarrier(provider, boundary);
        first.Boundary.Repository.ReservationHttp = new HttpClient(barrier);
        var pending = first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = new ReservationMachine(owner);
        second.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        var beforeNative = provider.Writes.Count;
        var beforeJournal = owner.Export();
        var previousCoordinator = (string?)JsonNode.Parse(owner.Values()["dsf/onboarding/products/widget"])!["foundation"]!["coordinatorId"];
        var beforeProduct = provider.Product.Export();
        (int Exit, JsonNode Result, ScriptedTerminal Terminal) resumed;
        try
        {
            resumed = await second.InvokeAsync("resume");
            Assert.Equal(exit, resumed.Exit);
            Assert.Equal(state, (string?)resumed.Result["foundation"]?["state"]);
            if (takeover)
            {
                var nextCoordinator = (string?)JsonNode.Parse(owner.Values()["dsf/onboarding/products/widget"])!["foundation"]!["coordinatorId"];
                Assert.False(string.IsNullOrEmpty(nextCoordinator));
                Assert.NotEqual(previousCoordinator, nextCoordinator);
                Assert.Equal(nextCoordinator, (string?)resumed.Result["foundation"]?["coordinatorId"]);
            }
            if (!takeover)
            {
                Assert.Equal(beforeNative, provider.Writes.Count);
                Assert.Equal(beforeJournal, owner.Export());
                Assert.Equal(beforeProduct, provider.Product.Export());
            }
        }
        finally { barrier.Release.TrySetResult(); }
        var previous = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(boundary == "after-group" ? 1 : boundary is "after-configuration" or "after-profile" or "before-profile-admission" ? 2 : 3, provider.Writes.Count);
        if (takeover) Assert.NotEqual(0, previous.Exit);
        Assert.Empty(previous.Terminal.Error);
        Assert.Empty(resumed.Terminal.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_partial_publication_continues_create_once_handoff_but_does_not_guess_group_ownership(bool rejected)
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await first.ReserveAsync(fingerprint);
        first.Store.LoseResponseWhen = (key, value) => !rejected && key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
            && ((string?)JsonNode.Parse(value)?["target"])?.EndsWith("/kv/dsf%2Ffactory%2Fprofile", StringComparison.Ordinal) == true
            && (string?)JsonNode.Parse(value)?["state"] == "Completed";
        var provider = new FoundationTransport(first.Store)
        { CompleteCreates = true, Product = new ReservationTestStore { RejectKey = rejected ? "dsf/factory/profile" : null } };
        first.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        Assert.Equal(1, (await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        Assert.Equal(rejected ? 0 : 1, provider.Product.Values().Count);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        var restored = new FoundationTransport(second.Store)
        { Product = ReservationTestStore.Restore(provider.Product.Export()), EnforceCorrelation = true };
        restored.RestoreNative(provider.ExportNative());
        second.Boundary.Repository.ReservationHttp = new HttpClient(restored);

        var (exit, result, terminal) = await second.InvokeAsync("resume");

        Assert.Equal(1, exit);
        Assert.Empty(terminal.Error);
        Assert.Contains("dsf/factory/manifest", restored.Product.Values().Keys);
        Assert.Equal("Blocked", (string?)result["foundation"]?["state"]);
        Assert.Equal("product-authoritative", (string?)result["foundation"]?["stage"]);
        Assert.Contains("resource-group incarnation", (string?)result["detail"]);
        if (!rejected) Assert.DoesNotContain(restored.Product.Writes, write => write.Key == "dsf/factory/profile");
        else Assert.Single(result["foundation"]!["operations"]![2]!["previousAttempts"]!.AsArray());
        Assert.All(restored.Product.Writes, write => Assert.Equal("*", write.IfNoneMatch));
        Assert.Empty(restored.Writes);
        var durable = second.Store.Export();
        var productDurable = restored.Product.Export();
        await second.InvokeAsync("resume");
        Assert.Equal(durable, second.Store.Export());
        Assert.Equal(productDurable, restored.Product.Export());
    }

    [Theory]
    [InlineData(false, false, true, "Blocked", 1)]
    [InlineData(true, false, true, "Blocked", 1)]
    [InlineData(false, true, true, "Blocked", 1)]
    [InlineData(false, false, false, "Unknown", 1)]
    public async Task Provider_completion_after_local_cancellation_is_reconciled_without_redeploying(
        bool failed, bool canceled, bool originalResponses, string state, int expectedExit)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new ReservationMachine(new ReservationTestStore()) { Token = cancellation.Token };
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        var (_, reserved, _) = await first.ReserveAsync(fingerprint);
        first.Store.AfterPut = (key, value) =>
        {
            if (key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
                && (string?)JsonNode.Parse(value)?["state"] == "InFlight"
                && ((string?)JsonNode.Parse(value)?["target"])?.Contains("/deployments/", StringComparison.Ordinal) == true)
                cancellation.Cancel();
        };
        var provider = new FoundationTransport(first.Store)
        { CompleteCreates = true, CompleteDeployment = true, AsyncDeployment = true, CosmosNativeReceipts = true,
            DeploymentFails = failed, DeploymentCanceled = canceled, IncludeCreationResponses = originalResponses };
        first.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        Assert.Equal(130, (await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        Assert.Equal(3, provider.Writes.Count);
        var pendingRuns = first.Store.Values().Where(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal))
            .Select(p => JsonNode.Parse(p.Value)!).Single(o => ((string?)o["target"])?.EndsWith("/containers/runs", StringComparison.Ordinal) == true);
        Assert.Equal("Intent", (string?)pendingRuns["state"]);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        var restored = new FoundationTransport(second.Store)
        { Product = ReservationTestStore.Restore(provider.Product.Export()), DeploymentFails = failed, DeploymentCanceled = canceled };
        restored.RestoreNative(provider.ExportNative());
        second.Boundary.Repository.ReservationHttp = new HttpClient(restored);

        var (exit, result, terminal) = await second.InvokeAsync("resume");

        Assert.Equal(expectedExit, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal(state, (string?)result["foundation"]?["state"]);
        Assert.True(JsonNode.DeepEquals(reserved["identity"], result["identity"]));
        var steps = result["foundation"]!["operations"]!.AsArray();
        if (state == "Blocked")
        {
            if (failed || canceled)
                Assert.Contains(steps, step => (string?)step!["state"] == (failed ? "Failed" : "Canceled")
                    && (string?)step["disposition"] == "Unproven");
            Assert.Contains(steps, step => (string?)step!["state"] == "Completed" && (string?)step["disposition"] == "Created");
            var runs = steps.Single(step => (string?)step!["target"] == (string?)pendingRuns["target"])!;
            Assert.Equal("Completed", (string?)runs["state"]);
            Assert.Equal("Created", (string?)runs["disposition"]);
            Assert.StartsWith("cosmos-rid-sha256:", (string?)runs["incarnation"]);
            Assert.NotNull(runs["providerOperationId"]);
            Assert.Equal((string?)pendingRuns["clientRequestId"], (string?)runs["clientRequestId"]);
            var group = Assert.Single(steps, step => ((string?)step!["target"])?.EndsWith("-hosting-infra", StringComparison.Ordinal) == true)!;
            Assert.Equal("Unknown", (string?)group["state"]);
            Assert.Equal("CreateRequested", (string?)group["disposition"]);
            Assert.Null(group["incarnation"]);
            Assert.Single(result["foundation"]!["recoveryBlockers"]!.AsArray());
            var third = new ReservationMachine(ReservationTestStore.Restore(second.Store.Export()));
            var reloaded = new FoundationTransport(third.Store) { Product = ReservationTestStore.Restore(restored.Product.Export()) };
            reloaded.RestoreNative(restored.ExportNative());
            third.Boundary.Repository.ReservationHttp = new HttpClient(reloaded);
            var inspected = await third.InvokeAsync("status");
            Assert.Equal("Blocked", (string?)inspected.Result["foundation"]!["state"]);
            Assert.True(JsonNode.DeepEquals(steps, inspected.Result["foundation"]!["operations"]));
            Assert.True(JsonNode.DeepEquals(result["foundation"]!["recoveryBlockers"], inspected.Result["foundation"]!["recoveryBlockers"]));
            Assert.Empty(reloaded.Writes);
            Assert.Empty(reloaded.Product.Writes);
        }
        Assert.Empty(restored.Writes);
        Assert.Empty(restored.Product.Writes);
        if (state == "Unknown") Assert.Empty(second.Store.Writes);
        else Assert.NotEmpty(second.Store.Writes);
        Assert.DoesNotContain("not-for-receipts", second.Store.Export() + terminal.Output);
    }

    [Fact]
    public async Task Completed_group_receipt_without_a_supported_current_incarnation_blocks_new_ARM_work()
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await first.ReserveAsync(fingerprint);
        first.Store.LoseResponseWhen = (key, value) => key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
            && JsonNode.Parse(value)?["index"]?.GetValue<int>() == 0
            && (string?)JsonNode.Parse(value)?["state"] == "Completed";
        var provider = new FoundationTransport(first.Store) { CompleteCreates = true };
        first.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        Assert.Equal(1, (await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        Assert.Single(provider.Writes);
        var second = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        var restored = new FoundationTransport(second.Store);
        second.Boundary.Repository.ReservationHttp = new HttpClient(restored);

        var (exit, result, terminal) = await second.InvokeAsync("resume");

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)result["foundation"]?["state"]);
        Assert.Contains("resource-group incarnation", (string?)result["detail"]);
        Assert.Contains("administrator", (string?)result["detail"]);
        Assert.Empty(terminal.Error);
        Assert.Empty(restored.Writes);
        Assert.Empty(restored.Product.Writes);
        Assert.Equal("Completed", (string?)result["foundation"]!["operations"]![0]!["state"]);
        Assert.Null(result["foundation"]!["operations"]![0]!["incarnation"]);
    }

    [Fact]
    public async Task Resume_on_another_machine_continues_approved_pre_dispatch_boundary_with_original_identity()
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var reviewed = await first.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        var (_, reserved, _) = await first.ReserveAsync(fingerprint);
        first.Store.LoseResponseWhen = (key, value) => key == "dsf/onboarding/products/widget"
            && JsonNode.Parse(value)?["foundation"]?["operationCount"]?.GetValue<int>() == 0;
        var interrupted = new FoundationTransport(first.Store) { CompleteCreates = true, CompleteDeployment = true };
        first.Boundary.Repository.ReservationHttp = new HttpClient(interrupted);
        Assert.Equal(1, (await first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        Assert.Empty(interrupted.Writes);
        var saved = first.Store.Export();
        var originalOperation = JsonNode.Parse((string)JsonNode.Parse((string)JsonNode.Parse(saved)!["dsf/onboarding/products/widget"]!)!["value"]!)!["foundation"]!["operationId"]!.DeepClone();
        var second = new ReservationMachine(ReservationTestStore.Restore(saved));
        var provider = new FoundationTransport(second.Store)
        { CompleteCreates = true, CompleteDeployment = true, EnforceCorrelation = true };
        second.Boundary.Repository.ReservationHttp = new HttpClient(provider);

        var (exit, result, terminal) = await second.InvokeAsync("resume");

        Assert.Equal(0, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal("Inactive", (string?)result["foundation"]?["state"]);
        Assert.True(JsonNode.DeepEquals(reserved["identity"], result["identity"]));
        Assert.True(JsonNode.DeepEquals(originalOperation, result["foundation"]!["operationId"]));
        Assert.Equal(3, provider.Writes.Count);
        Assert.False((bool)result["isActivated"]!);
        Assert.NotEmpty(second.Store.Writes);
        second.Boundary.AssertReadOnly();
        var third = new ReservationMachine(ReservationTestStore.Restore(second.Store.Export()));
        third.Boundary.Repository.ReservationHttp = new HttpClient(new FoundationTransport(third.Store)
        { Product = ReservationTestStore.Restore(provider.Product.Export()) });
        Assert.Equal(0, (await third.InvokeAsync("status")).Exit);
        Assert.Empty(third.Store.Writes);
    }

    private sealed class DispatchBarrier(HttpMessageHandler provider, string boundary) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker client = new(provider, disposeHandler: false);
        private bool paused;
        private int retryRootWrites;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var match = false;
            if (!paused && request.Method == HttpMethod.Put)
            {
                if (request.RequestUri!.Host == "owner.azconfig.io")
                {
                    var envelope = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                    var value = JsonNode.Parse((string)envelope["value"]!)!;
                    match = boundary switch
                    {
                        "before-admission" or "after-admission" => value["foundation"]?["operationCount"]?.GetValue<int>() == 1,
                        "after-intent" => (int?)value["index"] == 0 && (string?)value["state"] == "Intent",
                        "after-response" => (int?)value["index"] == 0 && (string?)value["state"] == "Unknown",
                        "after-group" => (int?)value["index"] == 0 && (string?)value["state"] == "Completed",
                        "after-configuration" => (int?)value["index"] == 1 && (string?)value["state"] == "Completed",
                        "before-profile-admission" or "after-profile-admission" => value["foundation"]?["operationCount"]?.GetValue<int>() == 3,
                        "after-profile-intent" => (int?)value["index"] == 2 && (string?)value["state"] == "Intent",
                        "after-profile" => (int?)value["index"] == 2 && (string?)value["state"] == "Completed",
                        "after-target-intent" => (string?)value["deploymentTarget"] is not null && (string?)value["state"] == "Intent",
                        "after-retry-admission" => value["foundation"] is not null && ++retryRootWrites == 2,
                        "after-deployment" => ((string?)value["target"])?.Contains("/deployments/", StringComparison.Ordinal) == true
                            && (string?)value["state"] == "InFlight",
                        _ => false,
                    };
                }
                else if (request.RequestUri.Host == "management.azure.com")
                    match = boundary == "after-provider" && !request.RequestUri.AbsolutePath.Contains("/providers/", StringComparison.Ordinal)
                        || boundary is "before-deployment" or "after-deployment-provider" && request.RequestUri.AbsolutePath.Contains("/deployments/", StringComparison.Ordinal);
                else if (request.RequestUri.Host.StartsWith("dsfo-", StringComparison.Ordinal))
                    match = boundary == "after-profile-provider"
                        && Uri.UnescapeDataString(request.RequestUri.AbsolutePath) == "/kv/dsf/factory/profile";
            }
            if (!match) return await client.SendAsync(request, token);
            paused = true;
            var before = boundary is "before-admission" or "before-deployment" or "before-profile-admission";
            var response = before ? null : await client.SendAsync(request, token);
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            return response ?? await client.SendAsync(request, token);
        }
    }
}
