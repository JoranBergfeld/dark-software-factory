using System.Text.Json.Nodes;
using System.Text.Json;
using Dsf.Core.Onboarding;
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
    public async Task Partial_factory_remains_partial_until_owner_claim_release_is_confirmed()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewedBoundary = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)reviewedBoundary.Plan["fingerprint"]!);
        machine.Store.RejectDeleteKey = machine.Store.Values().Keys.Single(key =>
            key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "repositories/", StringComparison.Ordinal));
        var operation = Guid.NewGuid();
        var reviewed = await machine.InvokeAsync("offboard", "--operation", operation.ToString());

        var result = await machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        var anchor = JsonNode.Parse(machine.Store.Values()[ProductConfigurationKeys.OwnerProductClaim("widget")])!;
        Assert.Equal("Partial", (string?)anchor["offboarding"]!["state"]);
        Assert.Null(anchor["offboarding"]!["completedAt"]);
    }

    [Fact]
    public async Task Existing_partial_detach_requires_the_same_reviewed_approval_before_cleanup_resumes()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        scenario.Previous.CleanupDenied = true;
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        var fingerprint = (string)reviewed.Result["plan"]!["fingerprint"]!;
        Assert.Equal("Partial", (string?)(await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", fingerprint)).Result["state"]);
        scenario.Previous.CleanupDenied = false;
        var before = scenario.Previous.DeletedGrants.Count;

        var resumed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());

        Assert.Equal("ReviewRequired", (string?)resumed.Result["state"]);
        Assert.True((bool)resumed.Result["admissionClosed"]!);
        Assert.Equal(before, scenario.Previous.DeletedGrants.Count);
    }

    [Fact]
    public async Task Competing_detach_operation_cannot_replace_the_admission_closing_operation()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        scenario.Previous.CleanupDenied = true;
        var original = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", original.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", original.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        var competing = await scenario.Machine.InvokeAsync("offboard", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("Blocked", (string?)competing.Result["state"]);
        Assert.Equal(original.ToString(), (string?)competing.Result["request"]!["operationId"]);
    }

    [Fact]
    public async Task Reused_grants_are_preserved_and_excluded_from_approved_removals()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        const string grant = "/subscriptions/shared/providers/Microsoft.Authorization/roleAssignments/reused";
        var reused = new FoundationOperation(1, "test-reused", manifest.Identity.FactoryId, Guid.NewGuid(), 0,
            grant, "2022-04-01", new string('b', 64), Guid.NewGuid(), DateTimeOffset.Parse("2026-09-15T22:00:00Z"),
            FoundationOperationState.Completed, FoundationResourceDisposition.Reused)
        {
            PrincipalId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            RoleDefinitionId = "/subscriptions/shared/providers/Microsoft.Authorization/roleDefinitions/role",
            RoleScope = "/subscriptions/shared",
            Incarnation = "2026-09-15T22:00:00Z",
        };
        scenario.Arm.Product.Seed("dsf/factory/reused-grant", JsonSerializer.Serialize(reused, FactoryGitHubJson.Options));
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());

        Assert.DoesNotContain(grant, reviewed.Result["plan"]!["proposedRemovals"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);
        Assert.DoesNotContain(grant, scenario.Previous.DeletedGrants);
        Assert.Equal("Detached", (string?)result.Result["state"]);
    }

    [Fact]
    public async Task Every_approved_grant_removal_must_be_reconciled_before_detached()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var operation = Guid.NewGuid();
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        var orphan = new FoundationOperation(1, "test-orphan", manifest.Identity.FactoryId,
            Guid.NewGuid(), 0, "/subscriptions/orphan/providers/Microsoft.Authorization/roleAssignments/orphan",
            "2022-04-01", new string('a', 64), Guid.NewGuid(), DateTimeOffset.Parse("2026-09-15T22:00:00Z"),
            FoundationOperationState.Completed, FoundationResourceDisposition.Created)
        {
            PrincipalId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            RoleDefinitionId = "/subscriptions/orphan/providers/Microsoft.Authorization/roleDefinitions/role",
            RoleScope = "/subscriptions/orphan",
            Incarnation = "2026-09-15T22:00:00Z",
        };
        const string key = "dsf/factory/orphan-grant";
        scenario.Arm.Product.Seed(key, JsonSerializer.Serialize(orphan, FactoryGitHubJson.Options));
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        scenario.Arm.Product.BeforePut = (writeKey, _) =>
        {
            if (writeKey == FactoryOffboarding.Key) scenario.Arm.Product.Remove(key);
            return Task.CompletedTask;
        };

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.Contains(result.Result["blockedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("approved grant", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task Rejected_first_admission_close_write_never_reports_admission_closed()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        scenario.Arm.Product.RejectKey = FactoryOffboarding.Key;

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["admissionClosed"]!);
        Assert.DoesNotContain(scenario.Arm.Product.Values(), pair => pair.Key == FactoryOffboarding.Key);
    }

    [Fact]
    public async Task Pending_preparation_effect_blocks_detached_certification_after_admission_closes()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var effect = RacingGrant(scenario, FoundationOperationState.InFlight, FoundationResourceDisposition.CreateRequested);
        scenario.Arm.Product.Seed("dsf/factory/racing-grant", JsonSerializer.Serialize(effect, FactoryGitHubJson.Options));
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.True((bool)result.Result["admissionClosed"]!);
        Assert.Contains(result.Result["blockedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("pending", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(effect.Target, scenario.Previous.DeletedGrants);
    }

    [Fact]
    public async Task Preparation_completing_during_admission_close_is_reconciled_before_detached()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var effect = RacingGrant(scenario, FoundationOperationState.InFlight, FoundationResourceDisposition.CreateRequested);
        const string key = "dsf/factory/racing-grant";
        scenario.Arm.Product.Seed(key, JsonSerializer.Serialize(effect, FactoryGitHubJson.Options));
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        Assert.Contains(effect.Target, reviewed.Result["plan"]!["proposedRemovals"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Arm.Product.BeforePut = async (writeKey, _) =>
        {
            if (writeKey != FactoryOffboarding.Key) return;
            scenario.Arm.Product.BeforePut = null;
            reached.SetResult();
            await release.Task;
        };

        var execution = scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);
        await reached.Task;
        var completed = effect with
        {
            State = FoundationOperationState.Completed,
            Disposition = FoundationResourceDisposition.Created,
            HttpStatus = 201,
        };
        scenario.Arm.Product.Change(key, _ => JsonSerializer.Serialize(completed, FactoryGitHubJson.Options));
        scenario.Previous.SeedResource(effect.Target, new JsonObject
        {
            ["id"] = effect.Target,
            ["properties"] = new JsonObject
            {
                ["principalId"] = effect.PrincipalId,
                ["roleDefinitionId"] = effect.RoleDefinitionId,
                ["scope"] = effect.RoleScope,
                ["condition"] = null,
                ["conditionVersion"] = null,
                ["createdOn"] = effect.Incarnation,
            },
        });
        release.SetResult();

        var result = await execution;

        Assert.Equal("Detached", (string?)result.Result["state"]);
        Assert.Contains(effect.Target, scenario.Previous.DeletedGrants);
        scenario.Reconstruct();
        var repeated = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Detached", (string?)repeated.Result["state"]);
    }

    private static FoundationOperation RacingGrant(
        CharterScenario scenario, FoundationOperationState state, FoundationResourceDisposition disposition)
    {
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        const string target = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/widget-decide"
            + "/providers/Microsoft.Authorization/roleAssignments/cccccccc-cccc-cccc-cccc-cccccccccccc";
        return new(1, "racing-preparation-grant", manifest.Identity.FactoryId, Guid.NewGuid(), 0, target,
            "2022-04-01", new string('c', 64), Guid.NewGuid(), DateTimeOffset.Parse("2026-09-15T22:00:00Z"),
            state, disposition)
        {
            PrincipalId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
            RoleDefinitionId = "/subscriptions/22222222-2222-2222-2222-222222222222"
                + "/providers/Microsoft.Authorization/roleDefinitions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            RoleScope = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/widget-decide",
            Incarnation = "2026-09-15T22:00:00+00:00",
        };
    }
}
