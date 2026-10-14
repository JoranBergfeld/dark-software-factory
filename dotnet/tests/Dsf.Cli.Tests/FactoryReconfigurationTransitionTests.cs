using System.Text.Json.Nodes;
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

    [Fact]
    public async Task Resume_conditionally_applies_configuration_and_invalidates_old_evidence_and_activation()
    {
        using var transport = await FactoryActivationTests.ReadyAsync();
        var machine = transport.Scenario.Machine;
        var inspected = await machine.InvokeAsync("reconfigure", "--inspect");
        var desired = inspected.Result["currentInputs"]!.DeepClone();
        desired["telemetry"]!["freshnessSeconds"] = 7200;
        desired["judgment"]!["threshold"] = 0.7;
        var reviewed = await InvokeAsync(machine, desired);
        var operation = Guid.NewGuid();
        var admitted = await InvokeAsync(machine, desired, "--apply", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["fingerprint"]!);
        Assert.Equal("Quiescing", (string?)admitted.Result["operation"]?["phase"]);

        var resumed = await InvokeAsync(machine, desired, "--resume", "--operation", operation.ToString());
        Assert.True((bool?)resumed.Result["configurationApplied"] == true, resumed.Terminal.Output + resumed.Terminal.Error);
        Assert.Equal("AwaitingEvidence", (string?)resumed.Result["operation"]?["phase"]);
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
        var activation = await machine.InvokeAsync("activate", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("Blocked", (string?)activation.Result["state"]);
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
