using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryReconfigurationTransitionTests
{
    internal const string Root = "dsf/factory/reconfiguration/current";

    [Fact]
    public async Task Exact_application_admission_closes_old_work_and_retains_the_original_review_and_configuration()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(inspected.Exit == 0, inspected.Terminal.Output);
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["telemetry"]!["freshnessSeconds"] = 7200;
        var reviewed = await InvokeAsync(machine, desired);
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        var before = transport.Scenario.Arm.Product.Values().ToDictionary();
        var owner = machine.Store.Export();
        var native = transport.SeedDocuments.ToDictionary();
        var operation = Guid.NewGuid();
        var applied = await InvokeAsync(machine, desired, "--apply", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        Assert.True((bool?)applied.Result["admissionClosed"] == true, applied.Terminal.Output + applied.Terminal.Error);
        Assert.DoesNotContain("Read-only reconfiguration review", applied.Terminal.Output);
        Assert.Contains("resume", (string?)applied.Result["nextAction"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Quiescing", (string?)applied.Result["operation"]?["phase"]);
        Assert.Equal(operation, (Guid?)applied.Result["operation"]?["operationId"]);
        Assert.False((bool)applied.Result["configurationApplied"]!);
        Assert.False((bool)applied.Result["isActivated"]!);
        Assert.Equal((string?)inspected.Result["factoryId"], (string?)applied.Result["factoryId"]);
        var after = transport.Scenario.Arm.Product.Values();
        Assert.Equal(before[ProductConfigurationKeys.FactoryManifest], after[ProductConfigurationKeys.FactoryManifest]);
        Assert.Equal(before[ProductConfigurationKeys.FactoryProfile], after[ProductConfigurationKeys.FactoryProfile]);
        Assert.Equal(before[ProductConfigurationKeys.FactoryTelemetry], after[ProductConfigurationKeys.FactoryTelemetry]);
        Assert.Equal(owner, machine.Store.Export());
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Empty(transport.EvaluationInference);
        var transitionWrites = transport.Scenario.Arm.Product.Writes.Where(w => w.Key.StartsWith("dsf/factory/reconfiguration/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(transitionWrites);
        Assert.Equal(Root, transitionWrites[0].Key);
        Assert.Equal("*", transitionWrites[0].IfNoneMatch);
        var retained = transport.Scenario.Arm.Product.Export();
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(retained);
        FactoryActivationTests.ConnectAudit(transport);
        var status = await machine.InvokeAsync("reconfigure", "--status", "--operation", operation.ToString());
        Assert.Equal("Quiescing", (string?)status.Result["operation"]?["phase"]);
        Assert.Equal(retained, transport.Scenario.Arm.Product.Export());
        var evaluation = await machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True((string?)evaluation.Result["readiness"] == "Blocked", evaluation.Terminal.Output + evaluation.Terminal.Error);
        Assert.Contains("Reconfiguration", (string?)evaluation.Result["reason"]);
        Assert.Empty(transport.EvaluationInference);
        Assert.Equal(native, transport.SeedDocuments);
    }

    [Fact]
    public async Task Offboarding_admission_winning_the_race_blocks_reconfiguration_without_transition_mutation()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["telemetry"]!["freshnessSeconds"] = 7200;
        var reviewed = await InvokeAsync(machine, desired);
        var detachOperation = Guid.NewGuid();
        var detachReview = await machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        var reconfigurationReachedDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReconfigurationDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLifecycleDispatch = 0;
        transport.Scenario.Arm.Product.BeforePut = async (key, _) =>
        {
            if (key != "dsf/factory/lifecycle" || Interlocked.Exchange(ref firstLifecycleDispatch, 1) != 0) return;
            reconfigurationReachedDispatch.TrySetResult();
            await allowReconfigurationDispatch.Task;
        };

        var applying = InvokeAsync(machine, desired, "--apply", "--operation", Guid.NewGuid().ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        await reconfigurationReachedDispatch.Task.WaitAsync(TimeSpan.FromMinutes(1));
        transport.Scenario.Arm.Product.BeforePut = null;
        var detached = await machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        allowReconfigurationDispatch.TrySetResult();
        var applied = await applying;

        Assert.Contains((string?)detached.Result["state"], new[] { "Partial", "Detached" });
        Assert.True((bool?)detached.Result["admissionClosed"]);
        Assert.Equal("Blocked", (string?)applied.Result["state"]);
        Assert.DoesNotContain(transport.Scenario.Arm.Product.Values().Keys,
            key => key.StartsWith("dsf/factory/reconfiguration/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconfiguration_admission_winning_the_race_keeps_offboarding_blocked_while_transition_is_active()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["telemetry"]!["freshnessSeconds"] = 7200;
        var reviewed = await InvokeAsync(machine, desired);
        var detachOperation = Guid.NewGuid();
        var detachReview = await machine.InvokeAsync("offboard", "--operation", detachOperation.ToString());
        var offboardingReachedDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOffboardingDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLifecycleDispatch = 0;
        transport.Scenario.Arm.Product.BeforePut = async (key, _) =>
        {
            if (key != "dsf/factory/lifecycle" || Interlocked.Exchange(ref firstLifecycleDispatch, 1) != 0) return;
            offboardingReachedDispatch.TrySetResult();
            await allowOffboardingDispatch.Task;
        };

        var offboarding = machine.InvokeAsync("offboard", "--operation", detachOperation.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        await offboardingReachedDispatch.Task.WaitAsync(TimeSpan.FromMinutes(1));
        transport.Scenario.Arm.Product.BeforePut = null;
        var applied = await InvokeAsync(machine, desired, "--apply", "--operation", Guid.NewGuid().ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        allowOffboardingDispatch.TrySetResult();
        var detached = await offboarding;

        Assert.Equal("Quiescing", (string?)applied.Result["operation"]?["phase"]);
        Assert.Equal("Blocked", (string?)detached.Result["state"]);
        Assert.DoesNotContain(FactoryOffboarding.Key, transport.Scenario.Arm.Product.Values().Keys);
        var currentOffboarding = await machine.InvokeAsync("offboard", "--operation", Guid.NewGuid().ToString());
        Assert.Contains(currentOffboarding.Result["plan"]!["ownershipBlockers"]!.AsArray(),
            blocker => ((string)blocker!).Contains("Reconfiguration operation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retained_transition_is_audit_linked_to_reviewed_manifest_and_inputs()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["telemetry"]!["freshnessSeconds"] = 7200;
        var reviewed = await InvokeAsync(machine, desired);
        var operation = Guid.NewGuid();
        var applied = await InvokeAsync(machine, desired, "--apply", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        Assert.True((bool?)applied.Result["admissionClosed"] == true, applied.Terminal.Output);

        var audit = await machine.InvokeAsync("audit");
        Assert.True(audit.Exit == 0, audit.Terminal.Output + audit.Terminal.Error);
        Assert.False((bool)audit.Result["currentAuthorization"]!);
    }

    [Theory]
    [InlineData("root-response")]
    [InlineData("input-rejected")]
    public async Task Interrupted_admission_recovers_only_original_exact_inputs_without_reopening_old_work(string failure)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["judgment"]!["threshold"] = 0.7;
        var reviewed = await InvokeAsync(machine, desired);
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        var operation = Guid.NewGuid();
        if (failure == "root-response") transport.Scenario.Arm.Product.LoseResponseForKey = Root;
        else transport.Scenario.Arm.Product.RejectKey = $"dsf/factory/reconfiguration/{operation:N}/inputs/0000";
        var applied = await InvokeAsync(machine, desired, "--apply", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        Assert.NotEqual(0, applied.Exit);
        var serialized = transport.Scenario.Arm.Product.Export();
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(serialized);
        FactoryActivationTests.ConnectAudit(transport);
        var wrong = desired.DeepClone();
        wrong["judgment"]!["threshold"] = 0.8;
        var rejected = await InvokeAsync(machine, wrong, "--resume", "--operation", operation.ToString());
        Assert.Equal("Blocked", (string?)rejected.Result["state"]);
        Assert.Equal(serialized, transport.Scenario.Arm.Product.Export());
        var resumed = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.True((bool?)resumed.Result["admissionClosed"] == true, resumed.Terminal.Output + resumed.Terminal.Error);
        Assert.Equal(operation, (Guid?)resumed.Result["operation"]?["operationId"]);
        Assert.Equal((int?)resumed.Result["operation"]?["inputChunks"], (int?)resumed.Result["operation"]?["confirmedChunks"]);
        Assert.DoesNotContain(transport.Scenario.Arm.Product.Writes, w => w.Key == Root && w.IfNoneMatch == "*");
        var complete = transport.Scenario.Arm.Product.Export();
        var status = await machine.InvokeAsync("reconfigure", "--status", "--operation", operation.ToString());
        Assert.Equal("AwaitingEvidence", (string?)status.Result["operation"]?["phase"]);
        Assert.Equal(complete, transport.Scenario.Arm.Product.Export());
        transport.Scenario.Arm.Product.Remove(Root);
        var missing = transport.Scenario.Arm.Product.Export();
        var erased = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.Equal("Unknown", (string?)erased.Result["state"]);
        Assert.True((bool?)erased.Result["admissionClosed"]);
        Assert.Equal(missing, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("stale")]
    public async Task Application_requires_current_exact_approval_before_closing_admission(string invalid)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["telemetry"]!["freshnessSeconds"] = 7200;
        var reviewed = await InvokeAsync(machine, desired);
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        var approval = invalid == "yes" ? "yes" : (string)reviewed.Result["fingerprint"]!;
        if (invalid == "stale") desired["telemetry"]!["freshnessSeconds"] = 7300;
        var before = transport.Scenario.Arm.Product.Export();
        var applied = await InvokeAsync(machine, desired, "--apply", "--operation", Guid.NewGuid().ToString(), "--approve", approval);
        Assert.Equal("Blocked", (string?)applied.Result["state"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("model")]
    public async Task Application_blocks_reviewed_categories_without_complete_desired_state_reconciliation(string category)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        if (category == "source")
            desired["source"]!["runtimeImage"] = "ghcr.io/example/source@sha256:" + new string('b', 64);
        else if (category == "model")
            desired["models"]!["validForSeconds"] = (int)desired["models"]!["validForSeconds"]! + 60;
        var reviewed = await InvokeAsync(machine, desired);
        Assert.Equal("Reviewed", (string?)reviewed.Result["state"]);
        var before = transport.Scenario.Arm.Product.Export();

        var applied = await InvokeAsync(machine, desired, "--apply", "--operation", Guid.NewGuid().ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);

        Assert.Equal("Blocked", (string?)applied.Result["state"]);
        Assert.Contains("UnsupportedReconfigurationCategory", (string?)applied.Result["detail"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData("automation-owner")]
    [InlineData("automation-declarations")]
    [InlineData("boundary-review")]
    [InlineData("model-resources")]
    public async Task Application_blocks_unapplied_boundary_resident_categories_without_closing_admission(string category)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        if (category == "automation-owner")
            desired["boundary"]!["automationOwner"] = "acme/security";
        else if (category == "automation-declarations")
            desired["boundary"]!["automationDeclarations"]!["ProposalIssue"] = "Conflict";
        else if (category == "boundary-review")
            desired["boundary"]!["boundaryReview"]!["accountableOwner"] = "acme/security";
        else
        {
            const string model = BoundaryPlanFixture.Root
                + "/resourceGroups/models/providers/Microsoft.CognitiveServices/accounts/shared";
            machine.Boundary.Resources[model] = BoundaryPlanFixture.Resource(
                model, "Microsoft.CognitiveServices/accounts");
            desired["boundary"]!["modelResourceIds"]!.AsArray().Add(model);
        }
        desired["boundary"]!["boundaryReview"] = null;
        var draft = await InvokeAsync(machine, desired);
        desired["boundary"]!["boundaryReview"] = new JsonObject
        {
            ["selectionFingerprint"] = (string)draft.Result["selectionFingerprint"]!,
            ["accountableOwner"] = category == "boundary-review" ? "acme/security" : "acme/platform",
            ["dedicated"] = true,
            ["crossOwnerExclusive"] = true,
        };
        var reviewed = await InvokeAsync(machine, desired);
        var before = transport.Scenario.Arm.Product.Export();

        var applied = await InvokeAsync(machine, desired, "--apply", "--operation", Guid.NewGuid().ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);

        Assert.Equal("Blocked", (string?)applied.Result["state"]);
        Assert.Contains("UnsupportedReconfigurationCategory", (string?)applied.Result["detail"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Resume_conditionally_applies_configuration_and_invalidates_old_evidence_and_activation()
    {
        using var transport = await FactoryActivationTests.ReadyAsync(publisher: true);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["telemetry"]!["freshnessSeconds"] = 7200;
        desired["judgment"]!["threshold"] = 0.7;
        var reviewed = await InvokeAsync(machine, desired);
        var operation = Guid.NewGuid();
        var admitted = await InvokeAsync(machine, desired, "--apply", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        Assert.NotEqual(0, admitted.Exit);
        Assert.Equal("Quiescing", (string?)admitted.Result["operation"]?["phase"]);
        foreach (var command in new[]
        {
            new[] { "telemetry", "--verify-only" },
            new[] { "models", "--verify-only" },
            new[] { "source", "--status-only" },
            new[] { "supplemental", "--verify-only" },
        })
        {
            var blocked = await machine.InvokeAsync(command[0], command[1..]);
            Assert.NotEqual(0, blocked.Exit);
            Assert.Contains("Quiescing", blocked.Terminal.Output + blocked.Terminal.Error);
        }

        var resumed = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.NotEqual(0, resumed.Exit);
        Assert.True((bool?)resumed.Result["configurationApplied"] == true, resumed.Terminal.Output + resumed.Terminal.Error);
        Assert.True((string?)resumed.Result["operation"]?["phase"] == "AwaitingEvidence",
            resumed.Terminal.Output + resumed.Terminal.Error);
        Assert.False((bool)resumed.Result["isActivated"]!);
        var values = transport.Scenario.Arm.Product.Values();
        var telemetryRecord = JsonNode.Parse(values[ProductConfigurationKeys.FactoryTelemetry])!;
        Assert.Equal(7200, (int?)telemetryRecord["configuration"]?["freshnessSeconds"]);
        Assert.Equal("Partial", (string?)telemetryRecord["state"]);
        Assert.Null(telemetryRecord["proof"]);
        var manifest = JsonNode.Parse(values[ProductConfigurationKeys.FactoryManifest])!;
        Assert.Null(manifest["previewOperationId"]);
        Assert.Null(manifest["previewApprovalFingerprint"]);
        Assert.Contains(values.Keys, k => k.StartsWith($"dsf/factory/reconfiguration/{operation:N}/snapshots/", StringComparison.Ordinal));
        var telemetry = await machine.InvokeAsync("telemetry", "--verify-only");
        Assert.True(telemetry.Exit == 0, telemetry.Terminal.Output + telemetry.Terminal.Error);
        Assert.Equal("Ready", (string?)telemetry.Result["telemetry"]?["state"]);
        var retired = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.True((string?)retired.Result["operation"]?["phase"] == "AwaitingEvidence",
            retired.Terminal.Output + retired.Terminal.Error);
        Assert.True((int?)retired.Result["operation"]?["retiredRecords"] > 0, retired.Terminal.Output);
        Assert.Equal((int?)retired.Result["operation"]?["retiredRecords"], (int?)retired.Result["operation"]?["retiredApplied"]);
        values = transport.Scenario.Arm.Product.Values();
        Assert.DoesNotContain(ProductConfigurationKeys.FactoryPreview, values.Keys);
        Assert.DoesNotContain(values.Keys, key => key.StartsWith(FactoryActivation.Prefix, StringComparison.Ordinal));
        Assert.Contains(values.Keys, key => key.StartsWith(
            $"dsf/factory/reconfiguration/{operation:N}/retired/", StringComparison.Ordinal));
        var activation = await machine.InvokeAsync("activate", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("Blocked", (string?)activation.Result["state"]);

        var configuration = await machine.InvokeAsync("preview", "--configure");
        Assert.True(configuration.Exit == 0, configuration.Terminal.Output + configuration.Terminal.Error);
        var seeded = await machine.InvokeAsync("preview", "--configure",
            "--approve", (string)configuration.Result["approvalFingerprint"]!);
        Assert.True(seeded.Exit == 0, seeded.Terminal.Output + seeded.Terminal.Error + transport.LastPreviewResponse);
        var configured = await machine.InvokeAsync("preview", "--configure",
            "--approve", (string)seeded.Result["approvalFingerprint"]!);
        Assert.True(configured.Exit == 0, configured.Terminal.Output + configured.Terminal.Error);
        var capability = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryPreview])!;
        Assert.Equal(0.7, (double?)capability["configuration"]?["inputs"]?["judgment"]?["threshold"]);
        var evaluated = await machine.InvokeAsync("preview", "--evaluate",
            "--operation", (string)capability["operationId"]!,
            "--approve", (string)capability["approvalFingerprint"]!);
        Assert.True(evaluated.Exit == 0, evaluated.Terminal.Output + evaluated.Terminal.Error + "\n"
            + string.Join("\n", transport.HostFailures));
        var awaitingActivation = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.True((string?)awaitingActivation.Result["operation"]?["phase"] == "AwaitingActivation",
            awaitingActivation.Terminal.Output + awaitingActivation.Terminal.Error);

        var activationOperation = Guid.NewGuid().ToString();
        var activationReview = await machine.InvokeAsync("activate", "--operation", activationOperation);
        Assert.True(activationReview.Exit == 0, activationReview.Terminal.Output + activationReview.Terminal.Error);
        var activationApproval = await machine.InvokeAsync("activate", "--operation", activationOperation,
            "--approve", (string)activationReview.Result["approvalFingerprint"]!);
        Assert.Equal("ApprovalOnly", (string?)activationApproval.Result["state"]);
        var liveSelection = FactoryPublisherTests.Selection().DeepClone();
        liveSelection["operationId"] = Guid.NewGuid();
        var liveReview = await machine.InvokeAsync("live", "--selection", liveSelection.ToJsonString());
        var live = await machine.InvokeAsync("live", "--selection", liveSelection.ToJsonString(),
            "--approve", (string)liveReview.Result["approvalFingerprint"]!);
        Assert.True(live.Exit == 0, live.Terminal.Output + live.Terminal.Error + "\n"
            + string.Join("\n", transport.HostFailures));
        var publisherReview = await machine.InvokeAsync("live", "--publication", "--selection", liveSelection.ToJsonString());
        var publisher = await machine.InvokeAsync("live", "--publication", "--selection", liveSelection.ToJsonString(),
            "--approve", (string)publisherReview.Result["approvalFingerprint"]!);
        Assert.True(publisher.Exit == 0, publisher.Terminal.Output + publisher.Terminal.Error);
        var promotionOperation = Guid.NewGuid().ToString();
        var promotionReview = await machine.InvokeAsync("activate", "--promote", "--operation", promotionOperation);
        var promoted = await machine.InvokeAsync("activate", "--promote", "--operation", promotionOperation,
            "--approve", (string)promotionReview.Result["approvalFingerprint"]!);
        Assert.True(promoted.Exit == 0, promoted.Terminal.Output + promoted.Terminal.Error);
        var completed = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.Equal("Completed", (string?)completed.Result["operation"]?["phase"]);
        Assert.True((bool?)completed.Result["isActivated"]);
        var offboarding = await machine.InvokeAsync("offboard", "--operation", Guid.NewGuid().ToString());
        Assert.DoesNotContain(offboarding.Result["plan"]!["ownershipBlockers"]!.AsArray(),
            blocker => ((string)blocker!).Contains("Reconfiguration operation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Application_reserves_added_claim_before_configuration_without_mutating_application_tags()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        const string added = BoundaryPlanFixture.Data + "-new";
        machine.Boundary.Resources[added] = BoundaryPlanFixture.Resource(added, "Microsoft.Storage/storageAccounts");
        desired["boundary"]!["applicationResourceIds"]!.AsArray().Add(added);
        desired["boundary"]!["boundaryReview"] = null;
        var draft = await InvokeAsync(machine, desired);
        desired["boundary"]!["boundaryReview"] = new JsonObject
        {
            ["selectionFingerprint"] = (string)draft.Result["selectionFingerprint"]!,
            ["accountableOwner"] = "acme/platform",
            ["dedicated"] = true,
            ["crossOwnerExclusive"] = true,
        };
        var reviewed = await InvokeAsync(machine, desired);
        Assert.Equal("Reviewed", (string?)reviewed.Result["state"]);
        var operation = Guid.NewGuid();
        await InvokeAsync(machine, desired, "--apply", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        var beforeTags = machine.Boundary.Resources[added]["tags"]!.DeepClone();
        var resumed = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.Equal("AwaitingEvidence", (string?)resumed.Result["operation"]?["phase"]);
        var claim = ProductConfigurationKeys.OwnerResourceClaim(added.ToLowerInvariant());
        Assert.Contains(claim, machine.Store.Export());
        Assert.True(JsonNode.DeepEquals(beforeTags, machine.Boundary.Resources[added]["tags"]));
        Assert.True((bool?)resumed.Result["admissionClosed"]);
    }

    [Fact]
    public async Task Application_releases_removed_claim_only_after_configuration_is_applied()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        var applications = desired["boundary"]!["applicationResourceIds"]!.AsArray();
        var removed = (string)applications[^1]!;
        applications.RemoveAt(applications.Count - 1);
        desired["boundary"]!["boundaryReview"] = null;
        var draft = await InvokeAsync(machine, desired);
        desired["boundary"]!["boundaryReview"] = new JsonObject
        {
            ["selectionFingerprint"] = (string)draft.Result["selectionFingerprint"]!,
            ["accountableOwner"] = "acme/platform",
            ["dedicated"] = true,
            ["crossOwnerExclusive"] = true,
        };
        var reviewed = await InvokeAsync(machine, desired);
        var operation = Guid.NewGuid();
        await InvokeAsync(machine, desired, "--apply", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        var claim = ProductConfigurationKeys.OwnerResourceClaim(removed.ToLowerInvariant());
        Assert.Contains(claim, machine.Store.Export());

        var resumed = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());

        Assert.True((string?)resumed.Result["operation"]?["phase"] == "AwaitingEvidence",
            resumed.Terminal.Output + resumed.Terminal.Error);
        Assert.DoesNotContain(claim, machine.Store.Export());
        Assert.True((int?)resumed.Result["operation"]?["appliedRecords"] > 0);
    }

    internal static async Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> InvokeAsync(
        ReservationMachine machine, JsonNode desired, params string[] arguments)
    {
        var directory = Path.Combine("artifacts", "onboard220", "inputs");
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"));
        try
        {
            await File.WriteAllTextAsync(path, desired.ToJsonString());
            return await machine.InvokeAsync("reconfigure", ["--input", path, .. arguments]);
        }
        finally { File.Delete(path); }
    }
}
