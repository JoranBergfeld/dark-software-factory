using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryOffboardingTests
{
    [Fact]
    public async Task Active_factory_requires_exact_review_before_detach_and_preserves_product_assets()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var beforeOwner = scenario.Machine.Store.Export();
        var beforeProduct = scenario.Arm.Product.Export();

        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", Guid.NewGuid().ToString());

        Assert.Equal(1, reviewed.Exit);
        Assert.Equal("ReviewRequired", (string?)reviewed.Result["state"]);
        Assert.False((bool)reviewed.Result["admissionClosed"]!);
        Assert.False((bool)reviewed.Result["stopCompleted"]!);
        Assert.NotEmpty((string)reviewed.Result["plan"]!["fingerprint"]!);
        Assert.Contains("application resources", reviewed.Result["plan"]!["preservedAssets"]!.ToJsonString());
        Assert.Contains("repository", reviewed.Result["plan"]!["preservedAssets"]!.ToJsonString());
        Assert.Contains("disabled factory infrastructure", reviewed.Result["plan"]!["preservedAssets"]!.ToJsonString());
        Assert.Contains("continuing", reviewed.Result["plan"]!["continuingCosts"]!.ToJsonString());
        Assert.Contains("principal=", reviewed.Result["plan"]!["proposedRemovals"]!.ToJsonString());
        Assert.Contains("/roleassignments/", reviewed.Result["plan"]!["proposedRemovals"]!.ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeOwner, scenario.Machine.Store.Export());
        Assert.Equal(beforeProduct, scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Approved_detach_closes_runtime_admission_and_retains_discoverable_registration()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        var fingerprint = (string)reviewed.Result["plan"]!["fingerprint"]!;

        var detached = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", fingerprint);

        Assert.True(detached.Exit == 0, detached.Terminal.Output + detached.Terminal.Error);
        Assert.Equal("Detached", (string?)detached.Result["state"]);
        Assert.True((bool)detached.Result["admissionClosed"]!);
        Assert.True((bool)detached.Result["stopCompleted"]!);
        Assert.True((bool)detached.Result["registrationRetained"]!);
        Assert.False((bool)detached.Result["infrastructurePurged"]!);
        Assert.False((bool)detached.Result["identityRetired"]!);
        Assert.Contains("already-issued tokens", (string)detached.Result["detail"]!);
        Assert.Contains(detached.Result["completedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("GitHub authentication role assignment", StringComparison.Ordinal));
        Assert.Contains(detached.Result["completedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("destination key version", StringComparison.Ordinal));
        Assert.Equal(2, scenario.Previous.DeletedGrants.Count);
        Assert.Single(scenario.Previous.DeletedKeyVersions);
        Assert.Contains(scenario.Arm.Product.Values(), pair => pair.Key == "dsf/factory/offboarding");
        Assert.Contains(scenario.Machine.Store.Values(), pair =>
            pair.Key == ProductConfigurationKeys.OwnerProductClaim("widget")
            && pair.Value.Contains("\"offboarding\"", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Machine.Store.Values(), pair =>
            pair.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "repositories/", StringComparison.Ordinal)
            || pair.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "resources/", StringComparison.Ordinal));
        Assert.Contains(scenario.Machine.Store.Values(), pair =>
            pair.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "plans/", StringComparison.Ordinal));

        scenario.Reconstruct();
        var repeated = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", fingerprint);
        Assert.Equal("Detached", (string?)repeated.Result["state"]);
        Assert.Empty(scenario.Arm.Product.Writes);
    }

    [Fact]
    public async Task Denied_factory_grant_cleanup_keeps_admission_closed_and_reports_partial_cleanup()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        scenario.Previous.CleanupDenied = true;
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal(1, result.Exit);
        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.True((bool)result.Result["admissionClosed"]!);
        Assert.False((bool)result.Result["stopCompleted"]!);
        Assert.Contains(result.Result["blockedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("denied", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(scenario.Arm.Product.Values(), pair => pair.Key == "dsf/factory/offboarding");
    }

    [Fact]
    public async Task Recreated_grant_identity_is_preserved_and_blocks_completed_detach()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var grant = scenario.Previous.ResourceIds.First(id =>
            id.Contains("/roleassignments/", StringComparison.OrdinalIgnoreCase));
        scenario.Previous.ChangeResource(grant,
            resource => resource["properties"]!["principalId"] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.DoesNotContain(grant, scenario.Previous.DeletedGrants);
        Assert.Single(scenario.Previous.DeletedGrants);
        Assert.Contains(result.Result["blockedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("changed or recreated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Partial_factory_uses_trusted_owner_recovery_without_adopting_selected_resources()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewedBoundary = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewedBoundary.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var before = machine.Store.Export();
        var operation = Guid.NewGuid();

        var reviewed = await machine.InvokeAsync("offboard", "--operation", operation.ToString());

        Assert.Equal("ReviewRequired", (string?)reviewed.Result["state"]);
        Assert.Contains("partial onboarding", reviewed.Result["plan"]!["preservedAssets"]!.ToJsonString());
        Assert.DoesNotContain("delete application", reviewed.Result["plan"]!["proposedRemovals"]!.ToJsonString());
        Assert.Equal(before, machine.Store.Export());
        var approved = await machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Detached", (string?)approved.Result["state"]);
        Assert.True((bool)approved.Result["admissionClosed"]!);
        Assert.True((bool)approved.Result["registrationRetained"]!);
        Assert.DoesNotContain(machine.Store.Values(), pair =>
            pair.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "repositories/", StringComparison.Ordinal)
            || pair.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "resources/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changed_or_uncertain_factory_state_blocks_cleanup_without_hiding_partial_stop()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        var fingerprint = (string)reviewed.Result["plan"]!["fingerprint"]!;
        scenario.Arm.Product.LoseResponseForKey = "dsf/factory/offboarding";

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", fingerprint);

        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.True((bool)result.Result["admissionClosed"]!);
        Assert.False((bool)result.Result["stopCompleted"]!);
        Assert.Contains("inspect", (string)result.Result["nextAction"]!, StringComparison.OrdinalIgnoreCase);
    }
}
