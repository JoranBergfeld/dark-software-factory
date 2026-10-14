using Xunit;
using Dsf.Core.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using System.Runtime.Versioning;

namespace Dsf.Cli.Tests;

public sealed class FactoryPurgeTests
{
    [Fact]
    public async Task Detached_factory_requires_separate_exact_purge_review()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        var detached = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Detached", (string?)detached.Result["state"]);
        var beforeOwner = scenario.Machine.Store.Export();
        var beforeProduct = scenario.Arm.Product.Export();

        var reviewed = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.True(reviewed.Result["state"] is not null, reviewed.Terminal.Output + reviewed.Terminal.Error);
        Assert.Equal(1, reviewed.Exit);
        Assert.Equal("ReviewRequired", (string?)reviewed.Result["state"]);
        Assert.NotEqual((string?)detachReview.Result["plan"]!["fingerprint"],
            (string?)reviewed.Result["plan"]!["fingerprint"]);
        Assert.Contains("application resources", reviewed.Result["plan"]!["preservedAssets"]!.ToJsonString());
        Assert.Contains("repository", reviewed.Result["plan"]!["preservedAssets"]!.ToJsonString());
        Assert.Contains("shared owner", reviewed.Result["plan"]!["preservedAssets"]!.ToJsonString());
        Assert.Contains("audit", reviewed.Result["plan"]!["blockedDeletions"]!.ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeOwner, scenario.Machine.Store.Export());
        Assert.Equal(beforeProduct, scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Active_factory_cannot_be_purged()
    {
        using var scenario = await CharterScenario.ReadyAsync();

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("detach", (string?)result.Result["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verified_current_export_allows_owned_resource_deletion_and_permanent_retirement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var transport = new FactoryAuditTests.AuditTransport(scenario, scenario.Previous);
        scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var root = Path.Combine(Environment.CurrentDirectory, ".purge-test-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var export = Path.Combine(root, "audit");
        try
        {
            var detachOperation = Guid.NewGuid();
            var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
            Assert.Equal("Detached", (string?)(await scenario.Machine.InvokeAsync("offboard",
                "--operation", detachOperation.ToString(),
                "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!)).Result["state"]);
            var exportReview = await scenario.Machine.InvokeAsync("audit", "--export", export);
            var exported = await scenario.Machine.InvokeAsync("audit", "--export", export,
                "--approve", (string)exportReview.Result["approvalFingerprint"]!);
            Assert.Equal("Exported", (string?)exported.Result["state"]);
            var operation = Guid.NewGuid();

            var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
                "--audit-export", export);

            Assert.True(review.Result["state"] is not null, review.Terminal.Output + review.Terminal.Error);
            Assert.Equal("ReviewRequired", (string?)review.Result["state"]);
            Assert.Empty(review.Result["plan"]!["blockedDeletions"]!.AsArray());
            Assert.NotEmpty(review.Result["plan"]!["proposedDeletions"]!.AsArray());
            var purged = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
                "--audit-export", export, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
            Assert.True(purged.Exit == 0, purged.Terminal.Output + purged.Terminal.Error);
            Assert.Equal("Purged", (string?)purged.Result["state"]);
            Assert.True((bool)purged.Result["identityRetired"]!);
            Assert.NotEmpty(scenario.Previous.DeletedFactoryResources);
            var retirement = scenario.Machine.Store.Values()[ProductConfigurationKeys.OwnerProductClaim("widget")];
            Assert.Contains("decide-factory-retirement", retirement);
            Assert.DoesNotContain(export, retirement, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(export))
                foreach (var file in Directory.GetFiles(export)) File.Delete(file);
            if (Directory.Exists(export)) Directory.Delete(export);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Recreated_or_human_modified_resource_is_preserved_and_reports_partial()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        var before = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());
        var resource = before.Result["plan"]!["proposedDeletions"]!.AsArray()
            .Select(item => (string)item!["resourceId"]!)
            .First(scenario.Previous.ResourceIds.Contains);
        scenario.Previous.ChangeResource(resource, value =>
            value["systemData"]!["lastModifiedAt"] = "2026-09-16T00:00:00Z");

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.True(result.Result["state"] is not null, result.Terminal.Output + result.Terminal.Error);
        Assert.Equal("ReviewRequired", (string?)result.Result["state"]);
        Assert.Contains(result.Result["plan"]!["blockedDeletions"]!.AsArray(),
            item => ((string)item!).Contains("changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Human_added_child_blocks_its_parent_without_blocking_independent_leaf_review()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        var environment = scenario.Arm.CreatedResources.Keys.Single(id =>
            id.Contains("/microsoft.app/managedenvironments/", StringComparison.OrdinalIgnoreCase));
        var child = environment + "/storages/human-added";
        scenario.Arm.SeedResource(child, new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = child,
            ["systemData"] = new System.Text.Json.Nodes.JsonObject
            {
                ["createdAt"] = "2026-10-14T12:00:00Z",
                ["lastModifiedAt"] = "2026-10-14T12:00:00Z",
            },
            ["properties"] = new System.Text.Json.Nodes.JsonObject(),
        });

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("ReviewRequired", (string?)result.Result["state"]);
        Assert.Contains(result.Result["plan"]!["blockedDeletions"]!.AsArray(),
            item => ((string)item!).Contains(child, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Result["plan"]!["proposedDeletions"]!.AsArray(),
            item => string.Equals((string?)item!["resourceId"], environment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Lost_delete_response_is_reconciled_after_client_reconstruction_without_repeating_delete()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var target = review.Result["plan"]!["proposedDeletions"]!.AsArray()
            .Select(item => (string)item!["resourceId"]!)
            .OrderByDescending(id => id.Count(character => character == '/'))
            .First(scenario.Previous.ResourceIds.Contains);
        scenario.Previous.LoseFactoryDeleteResponseFor = target;

        var uncertain = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Unknown", (string?)uncertain.Result["state"]);
        Assert.Single(scenario.Previous.DeletedFactoryResources, target);
        scenario.Previous.LoseFactoryDeleteResponseFor = null;
        scenario.Reconstruct();
        var recovered = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Purged", (string?)recovered.Result["state"]);
        Assert.Single(scenario.Previous.DeletedFactoryResources, target);
    }

    [Fact]
    public async Task Uncertain_delete_is_never_repeated_against_a_recreated_resource()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var target = review.Result["plan"]!["proposedDeletions"]!.AsArray()
            .Select(item => (string)item!["resourceId"]!)
            .OrderByDescending(id => id.Count(character => character == '/'))
            .First(scenario.Previous.ResourceIds.Contains);
        scenario.Previous.RejectFactoryDeleteFor = target;
        var original = scenario.Previous.ResourceIds.Contains(target);

        var uncertain = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Unknown", (string?)uncertain.Result["state"]);
        Assert.True(original);
        scenario.Previous.RejectFactoryDeleteFor = null;
        scenario.Previous.ChangeResource(target,
            resource => resource["systemData"]!["createdAt"] = "2026-10-14T00:00:00Z");
        scenario.Reconstruct();

        var recovered = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)recovered.Result["state"]);
        Assert.DoesNotContain(target, scenario.Previous.DeletedFactoryResources);
        Assert.Contains(recovered.Result["blocked"]!.AsArray(),
            item => ((string)item!).Contains("recreated", StringComparison.OrdinalIgnoreCase)
                || ((string)item!).Contains("incarnation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Authority_change_at_effect_boundary_blocks_dispatch_and_retirement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var changed = false;
        scenario.Previous.OnFactoryDelete = _ =>
        {
            if (changed) return;
            changed = true;
            scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, value => value);
        };

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.NotEqual("Purged", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["identityRetired"]!);
        Assert.Contains(result.Result["blocked"]!.AsArray(),
            item => ((string)item!).Contains("authority", StringComparison.OrdinalIgnoreCase)
                || ((string)item!).Contains("audit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Retired_identity_is_explicitly_blocked_from_status_and_resume()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var purged = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Purged", (string?)purged.Result["state"]);

        var status = await scenario.Machine.InvokeAsync("status");
        var resume = await scenario.Machine.InvokeAsync("resume");

        Assert.Equal("Blocked", (string?)status.Result["state"]);
        Assert.Equal("Blocked", (string?)resume.Result["state"]);
        Assert.Contains("retired", (string?)status.Result["detail"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retired", (string?)resume.Result["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lost_final_store_delete_is_reconciled_and_retired_after_reconstruction()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var store = review.Result["plan"]!["proposedDeletions"]!.AsArray()
            .Select(item => (string)item!["resourceId"]!)
            .Single(id => id.Contains("/configurationstores/", StringComparison.OrdinalIgnoreCase));
        scenario.Arm.LoseDeleteResponseFor = store;

        var uncertain = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Unknown", (string?)uncertain.Result["state"]);
        Assert.False((bool)uncertain.Result["identityRetired"]!);
        scenario.Arm.LoseDeleteResponseFor = null;
        scenario.Reconstruct();
        var recovered = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Purged", (string?)recovered.Result["state"]);
        Assert.True((bool)recovered.Result["identityRetired"]!);
        Assert.Single(scenario.Arm.DeletedResources, store);
    }

    [Fact]
    public async Task Competing_purge_cannot_replace_an_uncertain_original_operation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var original = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", original.ToString(),
            "--audit-export", export.Path);
        var target = review.Result["plan"]!["proposedDeletions"]!.AsArray()
            .Select(item => (string)item!["resourceId"]!)
            .OrderByDescending(id => id.Count(character => character == '/'))
            .First(scenario.Previous.ResourceIds.Contains);
        scenario.Previous.RejectFactoryDeleteFor = target;
        Assert.Equal("Unknown", (string?)(await scenario.Machine.InvokeAsync("purge",
            "--operation", original.ToString(), "--audit-export", export.Path,
            "--approve", (string)review.Result["plan"]!["fingerprint"]!)).Result["state"]);

        var competing = await scenario.Machine.InvokeAsync("purge",
            "--operation", Guid.NewGuid().ToString(), "--audit-export", export.Path);

        Assert.Equal("Blocked", (string?)competing.Result["state"]);
        Assert.Contains("operation", (string?)competing.Result["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_export_after_audit_root_change_blocks_all_deletion()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, value => value);
        var before = scenario.Previous.DeletedFactoryResources.Count;

        var review = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString(),
            "--audit-export", export.Path);

        Assert.Equal("ReviewRequired", (string?)review.Result["state"]);
        Assert.Contains(review.Result["plan"]!["blockedDeletions"]!.AsArray(),
            item => ((string)item!).Contains("export", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, scenario.Previous.DeletedFactoryResources.Count);
    }

    [Fact]
    public async Task Denied_delete_is_preserved_and_reported_without_retirement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        scenario.Previous.CleanupDenied = true;

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["identityRetired"]!);
        Assert.Contains(result.Result["preserved"]!.AsArray(),
            item => ((string)item!).Contains("denied", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(result.Result["residual"]!.AsArray());
    }

    [Fact]
    public async Task Denied_delete_can_retry_same_operation_after_authority_restoration_and_renewed_approval()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        scenario.Previous.CleanupDenied = true;
        var denied = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Partial", (string?)denied.Result["state"]);
        scenario.Previous.CleanupDenied = false;
        scenario.Reconstruct();

        var recovered = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Purged", (string?)recovered.Result["state"]);
        Assert.True((bool)recovered.Result["identityRetired"]!);
    }

    [Fact]
    public async Task Unresolved_foundation_effect_blocks_purge_review()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        var pending = new FoundationOperation(1, "test-pending", manifest.Identity.FactoryId,
            Guid.NewGuid(), 0, "/subscriptions/pending/resourceGroups/pending/providers/Microsoft.Storage/storageAccounts/pending",
            "2023-05-01", new string('a', 64), Guid.NewGuid(), DateTimeOffset.Parse("2026-09-15T23:00:00Z"),
            FoundationOperationState.InFlight, FoundationResourceDisposition.CreateRequested);
        scenario.Arm.Product.Seed("dsf/factory/pending-effect",
            JsonSerializer.Serialize(pending, FactoryGitHubJson.Options));

        var review = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("ReviewRequired", (string?)review.Result["state"]);
        Assert.Contains(review.Result["plan"]!["blockedDeletions"]!.AsArray(),
            item => ((string)item!).Contains("unresolved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Audit_stores_are_deleted_last_without_rereading_deleted_data_planes()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        scenario.Arm.OnDelete = resource =>
        {
            if (resource.Contains("/configurationstores/", StringComparison.OrdinalIgnoreCase)
                || resource.Contains("/databaseaccounts/", StringComparison.OrdinalIgnoreCase))
                export.Transport.FailDataPlaneReads = true;
        };
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
        Assert.Equal("Purged", (string?)result.Result["state"]);
        Assert.True((bool)result.Result["identityRetired"]!);
    }

    [Fact]
    public async Task Audit_store_recovery_uses_retained_final_boundary_after_reconstruction()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        scenario.Arm.OnDelete = resource =>
        {
            if (resource.Contains("/configurationstores/", StringComparison.OrdinalIgnoreCase)
                || resource.Contains("/databaseaccounts/", StringComparison.OrdinalIgnoreCase))
                export.Transport.FailDataPlaneReads = true;
        };
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var firstAudit = review.Result["plan"]!["proposedDeletions"]!.AsArray()
            .Where(item => (bool)item!["auditBacking"]!)
            .OrderByDescending(item => ((string)item!["resourceId"]!).Count(character => character == '/'))
            .Select(item => (string)item!["resourceId"]!)
            .First();
        if (scenario.Previous.ResourceIds.Contains(firstAudit))
            scenario.Previous.RejectFactoryDeleteFor = firstAudit;
        else
            scenario.Arm.LoseDeleteResponseFor = firstAudit;
        var uncertain = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Unknown", (string?)uncertain.Result["state"]);
        scenario.Previous.RejectFactoryDeleteFor = null;
        scenario.Arm.LoseDeleteResponseFor = null;
        export.Transport.FailDataPlaneReads = true;
        scenario.Reconstruct();

        var recovered = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Purged", (string?)recovered.Result["state"]);
        Assert.True((bool)recovered.Result["identityRetired"]!);
    }

    [Fact]
    public async Task Owner_authority_change_after_first_audit_delete_blocks_later_deletion_and_retirement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        Assert.True(review.Result["plan"]!["proposedDeletions"]!.AsArray()
            .Count(item => (bool)item!["auditBacking"]!) > 1);
        var ownerAuthority = scenario.Machine.Store.Values().Keys.First(key =>
            key.StartsWith("dsf/foundation/", StringComparison.Ordinal));
        var changed = false;
        scenario.Arm.OnDelete = resource =>
        {
            if (changed || !review.Result["plan"]!["proposedDeletions"]!.AsArray().Any(item =>
                    (bool)item!["auditBacking"]!
                    && string.Equals((string)item["resourceId"]!, resource,
                        StringComparison.OrdinalIgnoreCase))) return;
            changed = true;
            scenario.Machine.Store.Change(ownerAuthority, value => value);
        };

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["identityRetired"]!);
        Assert.Contains("authority", result.Result["blocked"]!.ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unreadable_factory_authority_blocks_purge_instead_of_disappearing()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        scenario.Arm.Product.Seed("dsf/factory/unreadable-operation", "{");

        var review = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("ReviewRequired", (string?)review.Result["state"]);
        Assert.Contains(review.Result["plan"]!["blockedDeletions"]!.AsArray(),
            item => ((string)item!).Contains("unreadable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unrecognized_factory_authority_kind_blocks_purge_instead_of_disappearing()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        scenario.Arm.Product.Seed("dsf/factory/future-operation",
            """{"protocol":1,"kind":"decide-future-operation"}""");

        var review = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("ReviewRequired", (string?)review.Result["state"]);
        Assert.Contains(review.Result["plan"]!["blockedDeletions"]!.AsArray(),
            item => ((string)item!).Contains("unrecognized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Shared_and_reused_authority_remains_preserved()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        const string reused = "/subscriptions/shared/resourceGroups/shared/providers/Microsoft.Storage/storageAccounts/shared";
        scenario.Arm.Product.Seed("dsf/factory/reused-resource", JsonSerializer.Serialize(
            new FoundationOperation(1, "test-reused", manifest.Identity.FactoryId, Guid.NewGuid(), 0,
                reused, "2023-05-01", new string('a', 64), Guid.NewGuid(),
                DateTimeOffset.Parse("2026-09-15T23:00:00Z"), FoundationOperationState.Completed,
                FoundationResourceDisposition.Reused)
            { ResourceId = reused, Incarnation = "2026-09-15T23:00:00Z" }, FactoryGitHubJson.Options));
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);

        var review = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.DoesNotContain(reused, review.Result["plan"]!["proposedDeletions"]!.ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shared owner", review.Result["preserved"]!.ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub App", review.Result["preserved"]!.ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retired_product_can_admit_only_a_fresh_reviewed_identity_with_retirement_history()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var purgeReview = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var purged = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)purgeReview.Result["plan"]!["fingerprint"]!);
        var retiredFactory = (string)purged.Result["plan"]!["factoryId"]!;

        var boundary = await scenario.Machine.Boundary.ReviewAsync();

        Assert.True((string?)boundary.Plan["state"] == "Reviewed",
            boundary.Plan["findings"]!.ToJsonString());
        var reserved = await scenario.Machine.ReserveAsync((string)boundary.Plan["fingerprint"]!);
        Assert.True((string?)reserved.Result["state"] == "Reserved",
            reserved.Terminal.Output + reserved.Terminal.Error);
        Assert.NotEqual(retiredFactory, (string?)reserved.Result["factoryId"]);
        Assert.Contains(scenario.Machine.Store.Values(), pair =>
            pair.Key.Contains("/retired/", StringComparison.Ordinal)
            && pair.Value.Contains(retiredFactory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unresolved_retired_effect_blocks_new_identity()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var purgeReview = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        Assert.Equal("Purged", (string?)(await scenario.Machine.InvokeAsync("purge",
            "--operation", operation.ToString(), "--audit-export", export.Path,
            "--approve", (string)purgeReview.Result["plan"]!["fingerprint"]!)).Result["state"]);
        var effect = scenario.Machine.Store.Values().Keys.First(key =>
            key.StartsWith(FactoryPurge.HistoryPrefix, StringComparison.Ordinal)
            && key.Contains("/effects/", StringComparison.Ordinal));
        scenario.Machine.Store.Change(effect, value =>
        {
            var node = JsonNode.Parse(value)!;
            node["state"] = "Unknown";
            return node.ToJsonString();
        });

        var boundary = await scenario.Machine.Boundary.ReviewAsync();

        Assert.Equal("Blocked", (string?)boundary.Plan["state"]);
        Assert.Contains("unresolved", boundary.Plan["findings"]!.ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unrecognized_extra_retired_effect_blocks_new_identity()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var purgeReview = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        Assert.Equal("Purged", (string?)(await scenario.Machine.InvokeAsync("purge",
            "--operation", operation.ToString(), "--audit-export", export.Path,
            "--approve", (string)purgeReview.Result["plan"]!["fingerprint"]!)).Result["state"]);
        var effect = scenario.Machine.Store.Values().First(pair =>
            pair.Key.StartsWith(FactoryPurge.HistoryPrefix, StringComparison.Ordinal)
            && pair.Key.Contains("/effects/", StringComparison.Ordinal));
        var extra = JsonNode.Parse(effect.Value)!;
        extra["index"] = 9999;
        scenario.Machine.Store.Seed(
            FactoryPurge.HistoryPrefix + operation.ToString("N") + "/effects/9999", extra.ToJsonString());

        var boundary = await scenario.Machine.Boundary.ReviewAsync();
        var replacement = await scenario.Machine.ReserveAsync((string)boundary.Plan["fingerprint"]!);

        Assert.Equal("Blocked", (string?)replacement.Result["state"]);
        Assert.Contains("effect set", (string?)replacement.Result["detail"],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_exception_returns_structured_unknown_json()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        var pause = new FactoryPauseRequest(1, FactoryPause.Kind, Guid.NewGuid(),
            manifest.Identity.FactoryId, manifest.Identity.RepositoryId, manifest.ProfilePin,
            "synthetic-revision", FactoryModelsValidation.Hash(manifest), manifest,
            manifest.Identity.AzureActorId, DateTimeOffset.Parse("2026-09-15T23:00:00Z"));
        scenario.Arm.Product.Seed(FactoryPause.Key,
            JsonSerializer.Serialize(pause, FactoryGitHubJson.Options));

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString(),
            "--audit-export", export.Path);

        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Contains("audit", (string?)result.Result["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_argument_exception_is_unknown_not_input_blocked()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        scenario.Previous.ThrowArgumentOnResourceRead = true;

        var result = await scenario.Machine.InvokeAsync("purge", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.DoesNotContain("locators", (string?)result.Result["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unreached_approved_expiry_does_not_authorize_audit_disposal()
    {
        var graph = AuditGraphWith(AuditRetentionStatus.Eligible);
        var policy = new AuditRetentionApproval(1, graph.FactoryId, graph.Policy, 90,
            Guid.NewGuid().ToString(), DateTimeOffset.Parse("2026-08-01T00:00:00Z"));

        var disposition = FactoryPurgeAuditDisposition.Evaluate(
            graph, policy, DateTimeOffset.Parse("2026-10-14T00:00:00Z"));

        Assert.False(disposition.Eligible);
        Assert.Contains("unreached", disposition.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_ninety_day_policy_never_erases_protected_or_referenced_evidence()
    {
        var graph = AuditGraphWith(AuditRetentionStatus.Protected);
        var policy = new AuditRetentionApproval(1, graph.FactoryId, graph.Policy, 90,
            Guid.NewGuid().ToString(), DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        var disposition = FactoryPurgeAuditDisposition.Evaluate(
            graph, policy, DateTimeOffset.Parse("2026-10-14T00:00:00Z"));

        Assert.False(disposition.Eligible);
        Assert.Contains("protected", disposition.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reached_approved_expiry_authorizes_only_fully_eligible_disposed_records()
    {
        var graph = AuditGraphWith(AuditRetentionStatus.Eligible);
        var policy = new AuditRetentionApproval(1, graph.FactoryId, graph.Policy, 90,
            Guid.NewGuid().ToString(), DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        var disposition = FactoryPurgeAuditDisposition.Evaluate(
            graph, policy, DateTimeOffset.Parse("2026-10-14T00:00:00Z"));

        Assert.True(disposition.Eligible);
        Assert.Contains("reached approved", disposition.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retirement_compare_exchange_race_requires_original_operation_recovery()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        using var export = await PurgeExport.CreateAsync(scenario);
        var operation = Guid.NewGuid();
        var review = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path);
        var raced = false;
        var root = ProductConfigurationKeys.OwnerProductClaim("widget");
        scenario.Machine.Store.BeforePut = (key, create) =>
        {
            if (key != root || create || raced) return Task.CompletedTask;
            var current = JsonNode.Parse(scenario.Machine.Store.Values()[root])!;
            if (current["purge"] is null) return Task.CompletedTask;
            raced = true;
            scenario.Machine.Store.Change(root, value => value);
            return Task.CompletedTask;
        };

        var uncertain = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Unknown", (string?)uncertain.Result["state"]);
        Assert.False((bool)uncertain.Result["identityRetired"]!);
        scenario.Machine.Store.BeforePut = null;
        var recovered = await scenario.Machine.InvokeAsync("purge", "--operation", operation.ToString(),
            "--audit-export", export.Path, "--approve", (string)review.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Purged", (string?)recovered.Result["state"]);
    }

    private static AuditGraph AuditGraphWith(AuditRetentionStatus status)
    {
        var factory = Guid.NewGuid();
        var identity = new AuditRecordIdentity(factory, AuditStoreKind.Analysis,
            "intent/history/example", "revision", new string('a', 64));
        return new AuditGraph(1, factory, FactoryAudit.Policy, [identity],
            [new FactoryAuditRecord(identity, DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                "{}", [], status)]);
    }

    private sealed class PurgeExport : IDisposable
    {
        private readonly string root;
        private readonly FactoryAuditTests.AuditTransport transport;
        public string Path { get; }
        public FactoryAuditTests.AuditTransport Transport => transport;

        [SupportedOSPlatform("linux")]
        public static async Task<PurgeExport> CreateAsync(CharterScenario scenario)
        {
            var transport = new FactoryAuditTests.AuditTransport(scenario, scenario.Previous);
            scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
            var root = System.IO.Path.Combine(Environment.CurrentDirectory,
                ".purge-test-artifacts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = System.IO.Path.Combine(root, "audit");
            var detachOperation = Guid.NewGuid();
            var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
            var detached = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
                "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
            Assert.Equal("Detached", (string?)detached.Result["state"]);
            var exportReview = await scenario.Machine.InvokeAsync("audit", "--export", path);
            Assert.True((string?)exportReview.Result["state"] == "Reviewed",
                exportReview.Terminal.Output + exportReview.Terminal.Error);
            var exported = await scenario.Machine.InvokeAsync("audit", "--export", path,
                "--approve", (string)exportReview.Result["approvalFingerprint"]!);
            Assert.Equal("Exported", (string?)exported.Result["state"]);
            return new(root, path, transport);
        }

        private PurgeExport(string root, string path, FactoryAuditTests.AuditTransport transport)
        {
            this.root = root;
            Path = path;
            this.transport = transport;
        }

        public void Dispose()
        {
            transport.Dispose();
            if (Directory.Exists(Path))
                foreach (var file in Directory.GetFiles(Path)) File.Delete(file);
            if (Directory.Exists(Path)) Directory.Delete(Path);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
