using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewPreparationFailureTests
{
    [Fact]
    public async Task Explicit_selection_over_16_KiB_is_rejected_before_any_provider_request()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var json = transport.Selection.ToJsonString();
        var oversized = json.Insert(1, new string(' ', 16 * 1024 + 1 - System.Text.Encoding.UTF8.GetByteCount(json)));
        Assert.Equal(16 * 1024 + 1, System.Text.Encoding.UTF8.GetByteCount(oversized));
        var requests = transport.Requests.Count;
        var before = transport.Scenario.Arm.Product.Export();
        var result = await transport.Scenario.Machine.InvokeAsync("preview", "--preview-config", oversized);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Equal(requests, transport.Requests.Count);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task A_reused_native_name_is_retained_and_cannot_be_adopted_by_approval()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        var fingerprint = (string)plan.Result["approvalFingerprint"]!;
        var target = (string)plan.Result["plan"]!["steps"]![0]!["resourceTargets"]![0]!;
        var existing = new JsonObject { ["id"] = target, ["tags"] = new JsonObject { ["dsf"] = "matching-name-is-not-ownership" } };
        transport.Resources[target] = existing;
        var before = transport.Scenario.Arm.Product.Export();
        var result = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", fingerprint);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("CreateTargetNotProvenAbsent", (string?)result.Result["detail"]);
        Assert.Same(existing, transport.Resources[target]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Empty(transport.NativeWrites);
    }

    [Fact]
    public async Task Missing_native_host_deployment_permission_blocks_before_preparation_intent()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        transport.DeniedAction = "Microsoft.App/containerApps/write";
        var before = transport.Scenario.Arm.Product.Export();
        var plan = await transport.Scenario.Machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.Equal("Blocked", (string?)plan.Result["state"]);
        Assert.Contains("OperatorActionsRequired", (string?)plan.Result["detail"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Empty(transport.NativeWrites);
    }

    [Theory]
    [InlineData("denied", "KnownFailed", 0)]
    [InlineData("denied-root", "KnownFailed", 0)]
    [InlineData("lost", "Unknown", 1)]
    [InlineData("canceled", "Unknown", 1)]
    [InlineData("partial", "Unknown", 1)]
    [InlineData("lost-receipt", "Unknown", 1)]
    [InlineData("lost-intent", "Unknown", 0)]
    public async Task Native_and_conditional_acknowledgment_failures_never_replay_or_publish(string failure, string expected, int writes)
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(plan.Exit == 0, plan.Terminal.Output);
        var approval = (string)plan.Result["approvalFingerprint"]!;
        var store = transport.Scenario.Arm.Product;
        if (failure == "denied-root") store.RejectKey = FactoryPreviewPreparation.RootKey;
        else if (failure == "lost-receipt")
            store.LoseResponseWhen = (key, _) => key.Contains("/preview-preparation/", StringComparison.Ordinal) && key.Contains("/receipts/", StringComparison.Ordinal);
        else if (failure == "lost-intent")
            store.LoseResponseWhen = (key, _) => key.Contains("/preview-preparation/", StringComparison.Ordinal) && key.EndsWith("/plan/0000", StringComparison.Ordinal);
        else transport.NativeFailure = failure;
        var prepared = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.Equal(expected, (string?)prepared.Result["state"]);
        Assert.False((bool)prepared.Result["currentReady"]!);
        Assert.Equal(writes, transport.NativeWrites.Count);
        if (failure == "partial")
        {
            var acknowledgment = prepared.Result["effects"]![0]!["acknowledgment"];
            Assert.NotNull(acknowledgment);
            Assert.Equal(201, (int)acknowledgment!["statusCode"]!);
            Assert.Equal("Failed", (string?)acknowledgment["provisioningState"]);
            Assert.Equal(64, ((string)acknowledgment["bodySha256"]!).Length);
        }
        Assert.DoesNotContain("private-", prepared.Terminal.Error);
        var before = transport.Requests.Count;
        var retained = store.Export();
        var inspected = await machine.InvokeAsync("preview", "--status");
        if (expected == "Unknown") Assert.Equal("Unknown", (string?)inspected.Result["state"]);
        Assert.Equal(retained, store.Export());
        Assert.DoesNotContain(transport.Requests.Skip(before), r => r.Method is "PUT" or "DELETE" or "PATCH");
        _ = await machine.InvokeAsync("preview", "--resume", "--approve", approval);
        Assert.Equal(writes, transport.NativeWrites.Count);
        Assert.Null(transport.Manifest.PreviewOperationId);
        Assert.DoesNotContain(transport.Values().Keys, k => k.StartsWith("dsf/factory/preview/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("missing-receipt")]
    [InlineData("changed-native")]
    public async Task Readonly_inspection_preserves_orphaned_or_changed_confirmed_native_history(string change)
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        var approval = (string)plan.Result["approvalFingerprint"]!;
        var prepared = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.True(prepared.Exit == 0, prepared.Terminal.Output);
        var store = transport.Scenario.Arm.Product;
        if (change == "missing-root") store.Remove(FactoryPreviewPreparation.RootKey);
        else if (change == "missing-receipt")
            store.Remove(transport.Values().Keys.First(k => k.Contains("/preview-preparation/", StringComparison.Ordinal) && k.Contains("/receipts/", StringComparison.Ordinal)));
        else
        {
            var grant = transport.NativeWrites.Last();
            transport.Resources[grant]["properties"]!["condition"] = "changed-condition";
        }
        var before = transport.Requests.Count;
        var retained = store.Export();
        var inspected = await machine.InvokeAsync("preview", "--status");
        Assert.NotEqual(0, inspected.Exit);
        Assert.Equal(change == "changed-native" ? "Blocked" : "Unknown", (string?)inspected.Result["state"]);
        Assert.Equal(retained, store.Export());
        Assert.DoesNotContain(transport.Requests.Skip(before), r => r.Method is "PUT" or "DELETE" or "PATCH");
        _ = await machine.InvokeAsync("preview", "--resume", "--approve", approval);
        Assert.DoesNotContain(transport.Requests.Skip(before), r => r.Method is "PUT" or "DELETE" or "PATCH");
    }

    [Fact]
    public async Task Reviewed_configuration_and_mid_deployment_authority_drift_cannot_repin_or_continue_effects()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        var approval = (string)plan.Result["approvalFingerprint"]!;
        var original = transport.Selection.DeepClone();
        transport.Selection["telemetry"]!["rowsPerTable"] = 2;
        var unchanged = transport.Scenario.Arm.Product.Export();
        var refused = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.Equal("Blocked", (string?)refused.Result["state"]);
        Assert.Equal(unchanged, transport.Scenario.Arm.Product.Export());
        transport.Selection = original;
        transport.AfterNativeWrite = _ => transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryModels, raw =>
        {
            var current = JsonNode.Parse(raw)!;
            current["configuration"]!["runtimeImage"] = "registry.example.invalid/runtime@sha256:" + new string('a', 64);
            return current.ToJsonString();
        });
        var partial = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.Equal("Blocked", (string?)partial.Result["state"]);
        Assert.Single(transport.NativeWrites);
        Assert.Equal("Confirmed", (string?)partial.Result["effects"]![0]!["state"]);
        Assert.All(partial.Result["effects"]!.AsArray().Skip(1), e => Assert.Equal("Intended", (string?)e!["state"]));
        var count = transport.NativeWrites.Count;
        _ = await machine.InvokeAsync("preview", "--resume", "--approve", approval);
        Assert.Equal(count, transport.NativeWrites.Count);
    }

    [Fact]
    public async Task Selected_supplemental_history_cannot_disappear_from_an_azure_only_plan()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, raw =>
        {
            var manifest = JsonNode.Parse(raw)!;
            manifest["supplementalOperationId"] = "ac000000-0000-0000-0000-000000000214";
            manifest["supplementalApprovalFingerprint"] = new string('a', 64);
            return manifest.ToJsonString();
        });
        var before = transport.Scenario.Arm.Product.Export();
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True((string?)plan.Result["state"] == "Unknown", plan.Terminal.Output);
        Assert.Contains("OwnerRegistryAndFoundationRequired", (string?)plan.Result["detail"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Empty(transport.NativeWrites);
    }
}
