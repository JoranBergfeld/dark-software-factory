using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryReattachmentTests
{
    [Fact]
    public async Task Detached_factory_requires_exact_review_then_reacquires_same_identity_claims()
    {
        using var scenario = await CharterScenario.ReadyAsync(telemetry: true);
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        var detached = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Detached", (string?)detached.Result["state"]);
        var identity = (string)detached.Result["plan"]!["factoryId"]!;
        var operation = Guid.NewGuid();
        var ownerBefore = scenario.Machine.Store.Export();
        var grantWrites = scenario.Previous.Grants.Count;

        var reviewed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString());

        Assert.Equal(1, reviewed.Exit);
        Assert.Equal("ReviewRequired", (string?)reviewed.Result["state"]);
        Assert.Equal(identity, (string?)reviewed.Result["plan"]!["factoryId"]);
        Assert.False((bool)reviewed.Result["admissionOpen"]!);
        Assert.Equal(ownerBefore, scenario.Machine.Store.Export());

        var attached = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.True(attached.Exit == 0, attached.Terminal.Output + attached.Terminal.Error);
        Assert.Equal("AwaitingReadiness", (string?)attached.Result["state"]);
        Assert.True((bool)attached.Result["admissionOpen"]!);
        Assert.False((bool)attached.Result["isActivated"]!);
        Assert.Equal(identity, (string?)attached.Result["plan"]!["factoryId"]);
        Assert.Contains(scenario.Machine.Store.Values(), pair =>
            pair.Key == ProductConfigurationKeys.OwnerRepositoryClaim(913));
        Assert.DoesNotContain(scenario.Arm.Product.Values(), pair => pair.Key == FactoryOffboarding.Key);
        Assert.Contains(scenario.Arm.Product.Values(), pair =>
            pair.Key == FactoryReattachment.Key);
        Assert.True(scenario.Previous.Grants.Count > grantWrites);
        Assert.Empty(scenario.Previous.DeletedKeyVersions);
        Assert.Contains(attached.Result["completedActions"]!.AsArray(),
            action => ((string)action!).Contains("role assignment", StringComparison.OrdinalIgnoreCase));
        var manifest = FactoryReservationRecords.Parse<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest]);
        Assert.Null(manifest.PreviewOperationId);
        Assert.Null(manifest.PreviewApprovalFingerprint);
        if (scenario.Arm.Product.Values().TryGetValue(ProductConfigurationKeys.FactoryTelemetry, out var telemetry))
            Assert.Equal("Partial", (string?)JsonNode.Parse(telemetry)!["state"]);
        Assert.Equal("Partial", (string?)JsonNode.Parse(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryModels])!["state"]);
        Assert.Equal("Partial", (string?)JsonNode.Parse(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactorySource])!["state"]);

        var status = await scenario.Machine.InvokeAsync("status");
        Assert.Equal("Reserved", (string?)status.Result["state"]);
        Assert.Contains("Reattachment", (string?)status.Result["detail"], StringComparison.Ordinal);
        var renewedModels = await scenario.Machine.InvokeAsync("models", "--verify-only");
        Assert.Equal("Blocked", (string?)renewedModels.Result["models"]!["state"]);
        Assert.Contains("MissingDeployment", (string?)renewedModels.Result["detail"], StringComparison.Ordinal);
        var staleActivation = await scenario.Machine.InvokeAsync(
            "activate", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("Blocked", (string?)staleActivation.Result["state"]);
    }

    [Fact]
    public async Task Competing_owner_claim_blocks_reattachment_without_takeover()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString());
        scenario.Machine.Store.Seed(ProductConfigurationKeys.OwnerRepositoryClaim(913), "{}");

        var result = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains(scenario.Arm.Product.Values(), pair => pair.Key == FactoryOffboarding.Key);
    }

    [Fact]
    public async Task Retired_identity_is_never_reattached()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        var detached = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        scenario.Machine.Store.Seed(FactoryReattachment.RetirementKey(
            Guid.Parse((string)detached.Result["plan"]!["factoryId"]!)), """{"kind":"retired"}""");

        var result = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("retired", (string?)result.Result["detail"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scenario.Arm.Product.Values(), pair => pair.Key == FactoryOffboarding.Key);
    }

    [Fact]
    public async Task Never_provisioned_partial_factory_cannot_be_upgraded_by_reattachment()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewedBoundary = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)reviewedBoundary.Plan["fingerprint"]!);
        var detach = Guid.NewGuid();
        var detachReview = await machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);

        var result = await machine.InvokeAsync("reattach", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("provisioned", (string?)result.Result["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_access_recovery_resumes_only_original_operation_and_fingerprint()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString());
        var fingerprint = (string)reviewed.Result["plan"]!["fingerprint"]!;
        scenario.Previous.Fault = "grant-denied";

        var partial = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString(),
            "--approve", fingerprint);

        Assert.Equal("Partial", (string?)partial.Result["state"]);
        Assert.False((bool)partial.Result["admissionOpen"]!);
        var competing = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", Guid.NewGuid().ToString(), "--approve", fingerprint);
        Assert.Equal("Blocked", (string?)competing.Result["state"]);

        scenario.Previous.Fault = null;
        var resumed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString(),
            "--approve", fingerprint);

        Assert.Equal("AwaitingReadiness", (string?)resumed.Result["state"]);
        Assert.True((bool)resumed.Result["admissionOpen"]!);
    }

}
