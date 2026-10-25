using System.Text.Json.Nodes;
using Xunit;
using static Dsf.Cli.Tests.FactoryFoundationTests;

namespace Dsf.Cli.Tests;

public sealed class FactoryFoundationCloseoutRecoveryTests
{
    [Fact]
    public async Task Lost_blocked_handoff_acknowledgment_retains_original_receipts_and_reloadable_blocker()
    {
        var (owner, product, native) = await InterruptedCloseoutAsync();
        var machine = new ReservationMachine(ReservationTestStore.Restore(owner));
        machine.Store.LoseResponseWhen = (key, value) => key == "dsf/onboarding/products/widget"
            && JsonNode.Parse(value)!["foundation"]!["recoveryBlockers"] is JsonArray { Count: > 0 };
        var provider = new FoundationTransport(machine.Store) { Product = ReservationTestStore.Restore(product) };
        provider.RestoreNative(native.ToJsonString());
        machine.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        var historical = machine.Store.Values().Where(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)).ToDictionary();

        var uncertain = await machine.InvokeAsync("resume");

        Assert.Equal(1, uncertain.Exit);
        Assert.Equal("Unknown", (string?)uncertain.Result["foundation"]?["state"]);
        var second = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        var reloaded = new FoundationTransport(second.Store) { Product = ReservationTestStore.Restore(provider.Product.Export()) };
        reloaded.RestoreNative(provider.ExportNative());
        second.Boundary.Repository.ReservationHttp = new HttpClient(reloaded);
        var status = await second.InvokeAsync("status");
        Assert.Equal("Blocked", (string?)status.Result["foundation"]?["state"]);
        Assert.Single(status.Result["foundation"]!["recoveryBlockers"]!.AsArray());
        Assert.Contains("administrator", (string?)status.Result["detail"]);
        Assert.All(historical, original => Assert.Equal(original.Value, second.Store.Values()[original.Key]));
        Assert.Empty(provider.Writes);
        Assert.Empty(provider.Product.Writes);
        Assert.Empty(reloaded.Writes);
        Assert.Empty(reloaded.Product.Writes);
        Assert.Empty(second.Store.Writes);
    }

    [Theory]
    [InlineData(false, "unchanged")]
    [InlineData(false, "recreated")]
    [InlineData(false, "deleted")]
    [InlineData(false, "changed-association")]
    [InlineData(false, "changed-parent-group")]
    [InlineData(true, "unchanged")]
    [InlineData(true, "recreated")]
    public async Task Hosting_group_association_never_proves_current_group_generation_after_interruption(
        bool legacyReceipt, string change)
    {
        var (owner, product, native) = await InterruptedCloseoutAsync();
        var machine = new ReservationMachine(ReservationTestStore.Restore(owner));
        var group = native.Single(p => p.Key.EndsWith("-hosting-infra", StringComparison.Ordinal)).Key;
        var groupRecord = machine.Store.Values().Single(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
            && (string?)JsonNode.Parse(p.Value)?["target"] == group);
        var parent = native[group]!["managedBy"]!.GetValue<string>();
        if (legacyReceipt)
        {
            var parentReceipt = JsonNode.Parse(machine.Store.Values().Single(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)
                && (string?)JsonNode.Parse(p.Value)?["target"] == parent).Value)!;
            machine.Store.Change(groupRecord.Key, value =>
            {
                var original = JsonNode.Parse(value)!;
                original["disposition"] = "Created";
                original["incarnation"] = "provider-managed-by:" + parent + ";creation:" + (string?)parentReceipt["incarnation"];
                original["providerOperationId"] = parentReceipt["providerOperationId"]!.DeepClone();
                original["providerCorrelationId"] = parentReceipt["providerCorrelationId"]!.DeepClone();
                original.AsObject().Remove("association");
                return original.ToJsonString();
            });
        }
        var historical = machine.Store.Values().Where(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)).ToDictionary();
        if (change == "recreated")
        {
            // The actual RG GET contract cannot distinguish these different generations.
            var sameObservableFields = JsonNode.Parse(native[group]!.ToJsonString())!;
            native.Remove(group);
            native.Add(group, sameObservableFields);
        }
        if (change == "deleted") native.Remove(group);
        if (change == "changed-association") native[group]!["managedBy"] = parent + "-other";
        if (change == "changed-parent-group") native[parent]!["properties"]!["infrastructureResourceGroup"] = "other-group";
        var provider = new FoundationTransport(machine.Store) { Product = ReservationTestStore.Restore(product) };
        provider.RestoreNative(native.ToJsonString());
        machine.Boundary.Repository.ReservationHttp = new HttpClient(provider);

        var result = await machine.InvokeAsync("resume");

        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["foundation"]?["state"]);
        Assert.False((bool)result.Result["foundation"]!["isBaseProvisioned"]!);
        Assert.Contains("administrator", (string?)result.Result["detail"]);
        var blocker = Assert.Single(result.Result["foundation"]!["recoveryBlockers"]!.AsArray())!;
        Assert.Equal(group, (string?)blocker["target"]);
        Assert.Equal("Unproven", (string?)blocker["currentOwnership"]);
        var root = JsonNode.Parse(machine.Store.Values()["dsf/onboarding/products/widget"])!;
        Assert.Equal("Blocked", (string?)root["foundation"]!["state"]);
        Assert.True(JsonNode.DeepEquals(root["foundation"]!["recoveryBlockers"], result.Result["foundation"]!["recoveryBlockers"]));
        Assert.All(historical, original => Assert.Equal(original.Value, machine.Store.Values()[original.Key]));
        Assert.Empty(provider.Writes);
        Assert.Empty(provider.Product.Writes);
        var status = await machine.InvokeAsync("status");
        Assert.Equal("Blocked", (string?)status.Result["foundation"]?["state"]);
        Assert.True(JsonNode.DeepEquals(status.Result["foundation"]!["recoveryBlockers"], result.Result["foundation"]!["recoveryBlockers"]));
        var third = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        var reloaded = new FoundationTransport(third.Store) { Product = ReservationTestStore.Restore(provider.Product.Export()) };
        reloaded.RestoreNative(provider.ExportNative());
        third.Boundary.Repository.ReservationHttp = new HttpClient(reloaded);
        var repeated = await third.InvokeAsync("resume");
        Assert.Equal(1, repeated.Exit);
        Assert.Equal("Blocked", (string?)repeated.Result["foundation"]?["state"]);
        Assert.Contains("administrator", (string?)repeated.Result["detail"]);
        Assert.True(JsonNode.DeepEquals(repeated.Result["foundation"]!["recoveryBlockers"], result.Result["foundation"]!["recoveryBlockers"]));
        Assert.All(historical, original => Assert.Equal(original.Value, third.Store.Values()[original.Key]));
        Assert.Empty(reloaded.Writes);
        Assert.Empty(reloaded.Product.Writes);
        if (!legacyReceipt)
        {
            var association = JsonNode.Parse(historical[groupRecord.Key])!;
            Assert.Equal("Associated", (string?)association["disposition"]);
            Assert.Null(association["incarnation"]);
            Assert.Null(association["providerOperationId"]);
            Assert.Equal(parent, (string?)association["association"]!["parentResourceId"]);
        }
    }

    [Theory]
    [InlineData("recreated", "Blocked")]
    [InlineData("deleted", "Unknown")]
    public async Task Completed_receipt_and_unique_operation_still_require_current_native_resource_validation(string change, string state)
    {
        var (owner, product, native) = await InterruptedCloseoutAsync();
        var target = native.Single(p => p.Key.EndsWith("/containers/runs", StringComparison.Ordinal)).Key;
        if (change == "deleted") native.Remove(target);
        else native[target]!["properties"]!["resource"]!["_rid"] = "AAAAAAABAAA=";
        var machine = new ReservationMachine(ReservationTestStore.Restore(owner));
        var provider = new FoundationTransport(machine.Store) { Product = ReservationTestStore.Restore(product) };
        provider.RestoreNative(native.ToJsonString());
        machine.Boundary.Repository.ReservationHttp = new HttpClient(provider);

        var result = await machine.InvokeAsync("resume");

        Assert.Equal(1, result.Exit);
        Assert.Equal(state, (string?)result.Result["foundation"]?["state"]);
        Assert.False((bool)result.Result["foundation"]!["isBaseProvisioned"]!);
        Assert.Equal(owner, machine.Store.Export());
        Assert.Empty(provider.Writes);
        Assert.Empty(provider.Product.Writes);
        Assert.Equal("Completed", (string?)result.Result["foundation"]!["operations"]!.AsArray()
            .Single(o => (string?)o!["target"] == target)!["state"]);
    }

    [Theory]
    [InlineData("missing", "unchanged")]
    [InlineData("missing", "recreated")]
    [InlineData("missing", "deleted")]
    [InlineData("duplicate", "unchanged")]
    [InlineData("duplicate", "recreated")]
    [InlineData("duplicate", "deleted")]
    public async Task Lost_final_closeout_never_reuses_completed_receipts_without_current_native_operation_proof(
        string listing, string resource)
    {
        var (owner, product, native) = await InterruptedCloseoutAsync();
        var target = native.Single(p => p.Key.EndsWith("/containers/runs", StringComparison.Ordinal)).Key;
        var operations = native.Single(p => p.Key.EndsWith("/operations", StringComparison.Ordinal)).Value!["value"]!.AsArray();
        var entry = operations.Single(o => (string?)o!["properties"]!["targetResource"]!["id"] == target)!;
        if (listing == "missing") operations.Remove(entry);
        else operations.Add(entry.DeepClone());
        if (resource == "deleted") native.Remove(target);
        else if (resource == "recreated") native[target]!["properties"]!["resource"]!["_rid"] = "AAAAAAABAAA=";
        var machine = new ReservationMachine(ReservationTestStore.Restore(owner));
        var provider = new FoundationTransport(machine.Store) { Product = ReservationTestStore.Restore(product) };
        provider.RestoreNative(native.ToJsonString());
        machine.Boundary.Repository.ReservationHttp = new HttpClient(provider);

        var result = await machine.InvokeAsync("resume");

        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["foundation"]?["state"]);
        Assert.Contains("exactly one native creation operation", (string?)result.Result["detail"]);
        Assert.False((bool)result.Result["foundation"]!["isBaseProvisioned"]!);
        Assert.Equal(owner, machine.Store.Export());
        Assert.Empty(provider.Writes);
        Assert.Empty(provider.Product.Writes);
        Assert.Empty(result.Terminal.Error);
        Assert.Equal("Completed", (string?)result.Result["foundation"]!["operations"]!.AsArray()
            .Single(o => (string?)o!["target"] == target)!["state"]);
    }

    private static async Task<(string Owner, string Product, JsonObject Native)> InterruptedCloseoutAsync()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        machine.Store.BeforePut = (key, _) =>
        {
            var operations = machine.Store.Values().Where(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal))
                .Select(p => JsonNode.Parse(p.Value)!).ToArray();
            if (key == "dsf/onboarding/products/widget" && operations.Length > 5
                && operations.Any(o => ((string?)o["target"])?.Contains("/deployments/", StringComparison.Ordinal) == true)
                && operations.All(o => (string?)o["state"] == "Completed"))
                machine.Store.RejectKey = key;
            return Task.CompletedTask;
        };
        var provider = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, IncludeCreationResponses = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(provider);
        Assert.Equal(1, (await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        Assert.Equal("base-deployment",
            (string?)JsonNode.Parse(machine.Store.Values()["dsf/onboarding/products/widget"])!["foundation"]!["stage"]);
        Assert.All(machine.Store.Values().Where(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)),
            p => Assert.Equal("Completed", (string?)JsonNode.Parse(p.Value)!["state"]));
        return (machine.Store.Export(), provider.Product.Export(), JsonNode.Parse(provider.ExportNative())!.AsObject());
    }
}
