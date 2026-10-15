using System.Text.Json.Nodes;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryOffboardingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_progress_reattachment_blocks_new_detach(bool ownerPointerRetained)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var identity = FactoryReservationRecords.Parse<FactoryReservationAnchor>(
            scenario.Machine.Store.Values()[ProductConfigurationKeys.OwnerProductClaim("widget")]).Identity;
        var operation = Guid.NewGuid();
        var requestedAt = scenario.Source.Source!.Clock.Now;
        var request = new FactoryReattachmentRequest(1, FactoryReattachment.Kind, operation,
            identity.FactoryId, new string('a', 64), new string('b', 64), identity.AzureActorId, requestedAt);
        if (ownerPointerRetained)
            scenario.Machine.Store.Change(ProductConfigurationKeys.OwnerProductClaim("widget"), value =>
            {
                var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(value);
                return FactoryReservationRecords.Serialize(anchor with
                {
                    Reattachment = new(operation, FactoryReattachmentState.AwaitingReadiness,
                        request.PlanFingerprint, request.AuthorityRevision, requestedAt),
                });
            });
        scenario.Arm.Product.Seed(FactoryReattachment.Key, FactoryReservationRecords.Serialize(request));

        scenario.Reconstruct();
        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("reattachment", (string?)result.Result["detail"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FactoryOffboarding.Key, scenario.Arm.Product.Values().Keys);
    }

    [Fact]
    public async Task Completed_reattachment_pointer_is_cleared_before_later_detach_review()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var identity = FactoryReservationRecords.Parse<FactoryReservationAnchor>(
            scenario.Machine.Store.Values()[ProductConfigurationKeys.OwnerProductClaim("widget")]).Identity;
        var operation = Guid.NewGuid();
        var requestedAt = scenario.Source.Source!.Clock.Now;
        var request = new FactoryReattachmentRequest(1, FactoryReattachment.Kind, operation,
            identity.FactoryId, new string('a', 64), new string('b', 64), identity.AzureActorId, requestedAt);
        var requestValue = FactoryReservationRecords.Serialize(request);
        scenario.Machine.Store.Change(ProductConfigurationKeys.OwnerProductClaim("widget"), value =>
        {
            var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(value);
            return FactoryReservationRecords.Serialize(anchor with
            {
                Reattachment = new(operation, FactoryReattachmentState.AwaitingReadiness,
                    request.PlanFingerprint, request.AuthorityRevision, requestedAt),
            });
        });
        scenario.Arm.Product.Seed(FactoryReattachment.HistoryKey(operation), requestValue);
        var descriptor = new FactoryReattachmentRetiredRecord(1, operation, 0,
            FactoryReattachment.Key, "revision-completed", FactoryReservationRecords.Hash(requestValue),
            System.Text.Encoding.UTF8.GetByteCount(requestValue), 1);
        scenario.Arm.Product.Seed(FactoryReattachment.FinalizedKey(operation, 0),
            FactoryReservationRecords.Serialize(descriptor));
        scenario.Arm.Product.Seed(FactoryReattachment.FinalizedChunkKey(operation, 0, 0),
            FactoryReservationRecords.Serialize(new FactoryReattachmentRetiredChunk(
                1, operation, 0, 0, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(requestValue)))));

        scenario.Reconstruct();
        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("ReviewRequired", (string?)result.Result["state"]);
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(
            scenario.Machine.Store.Values()[ProductConfigurationKeys.OwnerProductClaim("widget")]);
        Assert.Null(anchor.Reattachment);
        var detached = await scenario.Machine.InvokeAsync("offboard",
            "--operation", (string)result.Result["plan"]!["operationId"]!,
            "--approve", (string)result.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Detached", (string?)detached.Result["state"]);
        anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(
            scenario.Machine.Store.Values()[ProductConfigurationKeys.OwnerProductClaim("widget")]);
        Assert.Null(anchor.Reattachment);
        Assert.Equal(FactoryOffboardingState.Detached, anchor.Offboarding?.State);

        scenario.Machine.Boundary.Repository.ReservationHttp =
            new HttpClient(scenario.Previous, disposeHandler: false);
        scenario.Reconstruct();
        var next = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("ReviewRequired", (string?)next.Result["state"]);
    }

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
    public async Task Detached_factory_blocks_stale_reconfiguration_resume_without_claim_or_configuration_mutation()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detachOperation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        var detached = await scenario.Machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Detached", (string?)detached.Result["state"]);
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(
            scenario.Machine.Store.Values()[ProductConfigurationKeys.OwnerProductClaim("widget")]);
        var manifest = FactoryReservationRecords.Parse<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest]);
        var operationId = Guid.NewGuid();
        var operation = new FactoryReconfigurationOperation(
            1, operationId, anchor.Identity, new string('a', 64), new string('b', 64), "retained-manifest",
            manifest, new string('c', 64), 1, 1, anchor.Identity.AzureActorId,
            DateTimeOffset.Parse("2026-09-15T22:30:00Z"), FactoryReconfigurationPhase.Quiescing,
            operationId, 0);
        scenario.Arm.Product.Seed(FactoryReconfigurationOperationRecords.RootKey,
            FactoryReservationRecords.Serialize(operation));
        var owner = scenario.Machine.Store.Export();
        var product = scenario.Arm.Product.Export();

        var resumed = await scenario.Machine.InvokeAsync("reconfigure", "--resume",
            "--operation", operationId.ToString());

        Assert.Equal("Blocked", (string?)resumed.Result["state"]);
        Assert.Contains("OffboardingOrDetached", (string?)resumed.Result["detail"], StringComparison.Ordinal);
        Assert.Equal(owner, scenario.Machine.Store.Export());
        Assert.Equal(product, scenario.Arm.Product.Export());
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

    [Theory]
    [InlineData("preview")]
    [InlineData("live")]
    [InlineData("publisher")]
    public async Task Real_preparation_effects_block_detached_certification_after_admission_closes(string kind)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var effect = SeedPreparation(scenario, kind, PreviewPreparationEffectState.Dispatched);
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

    [Theory]
    [InlineData("preview")]
    [InlineData("live")]
    [InlineData("publisher")]
    public async Task Uncertain_real_preparation_coordinators_block_detached_certification(string kind)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        SeedPreparation(scenario, kind, PreviewPreparationEffectState.Confirmed, uncertainCoordinator: true);
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.Contains(result.Result["blockedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("coordinator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Real_preparation_effect_completing_during_admission_close_is_reconciled_before_detached()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var effect = SeedPreparation(scenario, "preview", PreviewPreparationEffectState.Intended);
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
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
        scenario.Arm.Product.Change(FactoryPreviewPreparation.EffectKey(effect.OperationId, effect.Index), _ =>
            JsonSerializer.Serialize(effect with { State = PreviewPreparationEffectState.Confirmed },
                FactoryGitHubJson.Options));
        scenario.Arm.Product.Change(FactoryPreviewPreparation.RootKey, value =>
            JsonSerializer.Serialize(
                JsonSerializer.Deserialize<PreviewPreparationCoordinator>(value, FactoryGitHubJson.Options)! with
                {
                    State = PreviewPreparationState.ConfirmedNotReady,
                    ConfirmedEffects = 1,
                }, FactoryGitHubJson.Options));
        release.SetResult();

        var result = await execution;

        Assert.Equal("Detached", (string?)result.Result["state"]);
        scenario.Reconstruct();
        var repeated = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);
        Assert.Equal("Detached", (string?)repeated.Result["state"]);
    }

    [Fact]
    public async Task Grant_created_after_review_is_preserved_pending_a_new_review()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        var grant = NewGrant(scenario, "post-review");
        scenario.Arm.Product.BeforePut = (writeKey, _) =>
        {
            if (writeKey != FactoryOffboarding.Key) return Task.CompletedTask;
            scenario.Arm.Product.BeforePut = null;
            scenario.Arm.Product.Seed("dsf/factory/post-review-grant",
                JsonSerializer.Serialize(grant, FactoryGitHubJson.Options));
            scenario.Previous.SeedResource(grant.Target, GrantResource(grant));
            return Task.CompletedTask;
        };

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.DoesNotContain(grant.Target, scenario.Previous.DeletedGrants);
        Assert.Contains(result.Result["blockedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("newly reviewed plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Grant_with_changed_reviewed_ownership_is_preserved_pending_a_new_review()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        const string key = "dsf/factory/reviewed-grant";
        var original = NewGrant(scenario, "reviewed-grant");
        scenario.Arm.Product.Seed(key, JsonSerializer.Serialize(original, FactoryGitHubJson.Options));
        scenario.Previous.SeedResource(original.Target, GrantResource(original));
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString());
        var changed = original with { PrincipalId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee" };
        scenario.Arm.Product.BeforePut = (writeKey, _) =>
        {
            if (writeKey != FactoryOffboarding.Key) return Task.CompletedTask;
            scenario.Arm.Product.BeforePut = null;
            scenario.Arm.Product.Change(key, _ => JsonSerializer.Serialize(changed, FactoryGitHubJson.Options));
            scenario.Previous.ChangeResource(changed.Target,
                resource => resource["properties"]!["principalId"] = changed.PrincipalId);
            return Task.CompletedTask;
        };

        var result = await scenario.Machine.InvokeAsync("offboard", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.DoesNotContain(changed.Target, scenario.Previous.DeletedGrants);
        Assert.Contains(result.Result["blockedRemovals"]!.AsArray(),
            item => ((string)item!).Contains("newly reviewed plan", StringComparison.OrdinalIgnoreCase));
    }

    private static PreviewPreparationEffect SeedPreparation(
        CharterScenario scenario, string kind, PreviewPreparationEffectState state, bool uncertainCoordinator = false)
    {
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        var operation = Guid.NewGuid();
        var effect = new PreviewPreparationEffect(1, manifest.Identity.FactoryId, operation, new string('a', 64), 0,
            "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/widget-decide"
                + "/providers/Microsoft.App/containerApps/widget-decide",
            new string('b', 64), Guid.NewGuid(), DateTimeOffset.Parse("2026-09-15T22:00:00Z"),
            state, [], null);
        switch (kind)
        {
            case "preview":
                scenario.Arm.Product.Seed(FactoryPreviewPreparation.RootKey,
                    JsonSerializer.Serialize(new PreviewPreparationCoordinator(1, FactoryPreviewPreparation.Kind,
                        manifest.Identity.FactoryId, operation, effect.PlanFingerprint, new string('c', 64), 0, 0,
                        manifest.Identity.AzureActorId, effect.IntendedAt, Guid.NewGuid(),
                        uncertainCoordinator ? PreviewPreparationState.Unknown : PreviewPreparationState.Partial,
                        state == PreviewPreparationEffectState.Confirmed ? 1 : 0, null), FactoryGitHubJson.Options));
                scenario.Arm.Product.Seed(FactoryPreviewPreparation.EffectKey(operation, 0),
                    JsonSerializer.Serialize(effect, FactoryGitHubJson.Options));
                break;
            case "live":
                scenario.Arm.Product.Seed(FactoryLive.RootKey,
                    JsonSerializer.Serialize(new FactoryLiveCoordinator(1, manifest.Identity.FactoryId, operation,
                        effect.PlanFingerprint, new string('c', 64), 0, 0, manifest.Identity.AzureActorId,
                        effect.IntendedAt, uncertainCoordinator ? FactoryLiveState.Unknown : FactoryLiveState.Preparing,
                        state == PreviewPreparationEffectState.Confirmed ? 1 : 0), FactoryGitHubJson.Options));
                scenario.Arm.Product.Seed(FactoryLive.EffectKey(operation, 0),
                    JsonSerializer.Serialize(effect, FactoryGitHubJson.Options));
                break;
            case "publisher":
                scenario.Arm.Product.Seed(FactoryPublisher.RootKey,
                    JsonSerializer.Serialize(new FactoryPublisherCoordinator(1, manifest.Identity.FactoryId, operation,
                        effect.PlanFingerprint, new string('c', 64), 0, 0, manifest.Identity.AzureActorId,
                        effect.IntendedAt,
                        uncertainCoordinator ? FactoryPublisherState.Unknown : FactoryPublisherState.Preparing,
                        state == PreviewPreparationEffectState.Confirmed ? 1 : 0), FactoryGitHubJson.Options));
                scenario.Arm.Product.Seed(FactoryPublisher.EffectKey(operation, 0),
                    JsonSerializer.Serialize(effect, FactoryGitHubJson.Options));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return effect;
    }

    private static FoundationOperation NewGrant(CharterScenario scenario, string kind)
    {
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        var target = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/widget-decide"
            + $"/providers/Microsoft.Authorization/roleAssignments/{Guid.NewGuid():D}";
        return new(1, kind, manifest.Identity.FactoryId, Guid.NewGuid(), 0, target,
            "2022-04-01", new string('c', 64), Guid.NewGuid(), DateTimeOffset.Parse("2026-09-15T22:00:00Z"),
            FoundationOperationState.Completed, FoundationResourceDisposition.Created)
        {
            PrincipalId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
            RoleDefinitionId = "/subscriptions/22222222-2222-2222-2222-222222222222"
                + "/providers/Microsoft.Authorization/roleDefinitions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            RoleScope = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/widget-decide",
            Incarnation = "2026-09-15T22:00:00+00:00",
        };
    }

    private static JsonObject GrantResource(FoundationOperation grant) => new()
    {
        ["id"] = grant.Target,
        ["properties"] = new JsonObject
        {
            ["principalId"] = grant.PrincipalId,
            ["roleDefinitionId"] = grant.RoleDefinitionId,
            ["scope"] = grant.RoleScope,
            ["condition"] = grant.Condition,
            ["conditionVersion"] = grant.ConditionVersion,
            ["createdOn"] = grant.Incarnation,
        },
    };
}
