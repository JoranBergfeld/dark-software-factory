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
        UseProbeTransport(scenario);
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
        Assert.Contains((string?)renewedModels.Result["models"]!["state"], new[] { "Blocked", "Unknown" });
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
        UseProbeTransport(scenario);
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
        UseProbeTransport(scenario);
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
        UseProbeTransport(scenario);
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

    [Fact]
    public async Task Reattachment_durably_archives_full_stale_authority_and_resumes_an_uncertain_delete()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        UseProbeTransport(scenario);
        var staleActivation = FactoryActivation.RootKey;
        const string staleValue = """{"kind":"preserved-proposal-provenance","proposal":"proposal-17"}""";
        scenario.Arm.Product.Seed(staleActivation, staleValue);
        scenario.Arm.Product.Seed(FactoryLive.Prefix + "current", """{"kind":"stale-live"}""");
        scenario.Arm.Product.Seed(FactoryPublisher.Prefix + "current", """{"kind":"stale-publisher"}""");
        scenario.Arm.Product.Seed(FactoryPromotion.Prefix + "current", """{"kind":"stale-promotion"}""");
        scenario.Arm.Product.LoseDeleteResponseKey = staleActivation;
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString());
        var fingerprint = (string)reviewed.Result["plan"]!["fingerprint"]!;

        var uncertain = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", operation.ToString(), "--approve", fingerprint);

        Assert.Equal("Unknown", (string?)uncertain.Result["state"]);
        Assert.DoesNotContain(staleActivation, scenario.Arm.Product.Values().Keys);
        var retired = scenario.Arm.Product.Values()
            .Where(pair => pair.Key.StartsWith(
                FactoryReattachment.HistoryPrefix + operation.ToString("N") + "/retired/", StringComparison.Ordinal))
            .Select(pair => pair.Value).ToArray();
        Assert.Contains(retired, value => value.Contains(staleActivation, StringComparison.Ordinal));
        Assert.Contains(retired, value => value.Contains(Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(staleValue)), StringComparison.Ordinal));

        scenario.Arm.Product.LoseDeleteResponseKey = null;
        var resumed = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", operation.ToString(), "--approve", fingerprint);

        Assert.Equal("AwaitingReadiness", (string?)resumed.Result["state"]);
        Assert.DoesNotContain(scenario.Arm.Product.Values().Keys, key =>
            key.StartsWith(FactoryActivation.Prefix, StringComparison.Ordinal)
            || key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)
            || key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal)
            || key.StartsWith(FactoryPromotion.Prefix, StringComparison.Ordinal));
        var activation = await scenario.Machine.InvokeAsync("activate", "--operation", Guid.NewGuid().ToString());
        Assert.DoesNotContain("Original approval operation already retained", (string?)activation.Result["detail"],
            StringComparison.Ordinal);
        Assert.Contains("preview", (string?)activation.Result["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reattachment_resumes_when_offboarding_root_delete_committed_but_response_was_lost()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        UseProbeTransport(scenario);
        scenario.Arm.Product.LoseDeleteResponseKey = FactoryOffboarding.Key;
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString());
        var fingerprint = (string)reviewed.Result["plan"]!["fingerprint"]!;

        var uncertain = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", operation.ToString(), "--approve", fingerprint);
        Assert.Equal("Unknown", (string?)uncertain.Result["state"]);
        Assert.DoesNotContain(FactoryOffboarding.Key, scenario.Arm.Product.Values().Keys);

        scenario.Arm.Product.LoseDeleteResponseKey = null;
        var resumed = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", operation.ToString(), "--approve", fingerprint);

        Assert.Equal("AwaitingReadiness", (string?)resumed.Result["state"]);
    }

    [Fact]
    public async Task Review_fingerprints_current_infrastructure_and_App_source_drift()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        UseProbeTransport(scenario);
        var resource = (string)JsonNode.Parse(
            scenario.Arm.Product.Values()[ProductConfigurationKeys.FactoryGitHubAuthority])!["hostResourceId"]!;
        scenario.Previous.ChangeResource(resource,
            value => value["properties"]!["configuration"]!["ingress"]!["fqdn"] = "changed.example");
        scenario.Previous.HostDenied = true;
        scenario.Previous.Fault = "host-repository-denied";

        var reviewed = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("Blocked", (string?)reviewed.Result["state"]);
        var findings = reviewed.Result["plan"]!["driftFindings"]!.AsArray()
            .Select(value => (string)value!).ToArray();
        Assert.Contains(findings, finding => finding.Contains("incarnation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, finding => finding.Contains("App", StringComparison.OrdinalIgnoreCase)
            || finding.Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Review_blocks_conflicting_destination_key_custody()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        UseProbeTransport(scenario);
        scenario.Previous.DeletedKeyVersions.Clear();
        scenario.Previous.Fault = "key-custody-conflict";

        var reviewed = await scenario.Machine.InvokeAsync(
            "reattach", "--operation", Guid.NewGuid().ToString());

        Assert.Equal("Blocked", (string?)reviewed.Result["state"]);
        Assert.Contains(reviewed.Result["plan"]!["driftFindings"]!.AsArray(),
            finding => ((string)finding!).Contains("KeyCustody", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changed_authority_makes_a_previously_reviewed_reattachment_approval_stale()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        UseProbeTransport(scenario);
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString());
        scenario.Previous.HostDenied = true;
        scenario.Previous.Fault = "host-repository-denied";

        var stale = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("Blocked", (string?)stale.Result["state"]);
        Assert.Contains("drift", (string?)stale.Result["detail"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FactoryReattachment.Key, scenario.Arm.Product.Values().Keys);
    }

    [Fact]
    public async Task Reattachment_retires_current_charter_adoption_but_preserves_protected_retention()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        const string adopted = """{"kind":"current-charter-adoption","revision":"stale"}""";
        const string adoptionOperation = """{"kind":"current-charter-adoption-operation","state":"Confirmed"}""";
        var detach = Guid.NewGuid();
        var detachReview = await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString());
        await scenario.Machine.InvokeAsync("offboard", "--operation", detach.ToString(),
            "--approve", (string)detachReview.Result["plan"]!["fingerprint"]!);
        scenario.Arm.Product.Seed(ProductConfigurationKeys.CharterAdopted, adopted);
        scenario.Arm.Product.Seed(ProductConfigurationKeys.CharterAdoptionOperation, adoptionOperation);
        scenario.Arm.Product.Seed(FactoryAuditRetention.PolicyKey, """{"kind":"protected-retention-policy"}""");
        UseProbeTransport(scenario);
        var operation = Guid.NewGuid();
        var reviewed = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString());

        var attached = await scenario.Machine.InvokeAsync("reattach", "--operation", operation.ToString(),
            "--approve", (string)reviewed.Result["plan"]!["fingerprint"]!);

        Assert.Equal("AwaitingReadiness", (string?)attached.Result["state"]);
        Assert.DoesNotContain(ProductConfigurationKeys.CharterAdopted, scenario.Arm.Product.Values().Keys);
        Assert.DoesNotContain(ProductConfigurationKeys.CharterAdoptionOperation, scenario.Arm.Product.Values().Keys);
        Assert.Equal("""{"kind":"protected-retention-policy"}""",
            scenario.Arm.Product.Values()[FactoryAuditRetention.PolicyKey]);
        var archives = scenario.Arm.Product.Values().Values.ToArray();
        Assert.Contains(archives, value => value.Contains(Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(adopted)), StringComparison.Ordinal));
        Assert.Contains(archives, value => value.Contains(Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(adoptionOperation)), StringComparison.Ordinal));
    }

    private static void UseProbeTransport(CharterScenario scenario) =>
        scenario.Machine.Boundary.Repository.ReservationHttp =
            new HttpClient(scenario.Previous, disposeHandler: false);

}
