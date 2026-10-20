using System.Text.Json.Nodes;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryTelemetryWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unchanged_interactive_governance_preserves_readonly_preview_and_explicit_approval(bool approve)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var before = arm.Product.Export();
        var terminal = new ScriptedTerminal(new(true, false, false),
            [approve ? (string)preview.Result["telemetry"]!["approvalFingerprint"]! : ""]);
        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", "telemetry", .. Locator, .. Preparation, "--freshness-hours", "24"]);
        Assert.Equal(approve ? 0 : 1, exit);
        Assert.Single(terminal.Prompts);
        Assert.Equal(approve ? 2 : 0, transport.Grants.Count);
        if (!approve) Assert.Equal(before, arm.Product.Export());
    }

    [Fact]
    public async Task Cancellation_during_promotion_write_preserves_unknown_commit_without_claiming_a_hold()
    {
        using var cancellation = new CancellationTokenSource();
        var (machine, arm) = await FoundationAsync(cancellation.Token);
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        arm.Product.AfterPut = (key, value) =>
        {
            if (key == "dsf/factory/telemetry" && (string?)JsonNode.Parse(value)!["state"] == "Ready")
                cancellation.Cancel();
        };
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Contains("Conditional write response uncertain", result.Terminal.Output);
        Assert.DoesNotContain("confirmed readiness hold remains", result.Terminal.Output);
        Assert.Null(result.Result["telemetry"]!["verifiedProofReference"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Governance_change_at_promotion_is_reheld_or_reported_unconfirmed_never_ready(bool rejectHold)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        arm.Product.AfterPut = (key, value) =>
        {
            if (key != "dsf/factory/telemetry" || (string?)JsonNode.Parse(value)!["state"] != "Ready") return;
            ChangeGovernance(arm.Product, "profile-deleted");
            if (rejectHold) arm.Product.RejectKey = key;
        };

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.Equal(1, result.Exit);
        Assert.Equal(rejectHold ? "Unknown" : "Blocked", (string?)result.Result["state"]);
        Assert.Null(result.Result["telemetry"]!["verifiedProofReference"]);
        if (rejectHold) Assert.Contains("unconfirmed", result.Terminal.Output);
        else Assert.Equal("Blocked", (string?)ReadProductRecord(arm.Product, "dsf/factory/telemetry")["state"]);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Single(transport.Deployments);
    }

    public static IEnumerable<object[]> PromotionGovernanceChanges =>
        new[] { "proof", "persisting-proof" }.SelectMany(phase =>
            new[] { "profile-deleted", "profile-corrupt", "manifest-deleted", "manifest-corrupt",
                "publication", "identity", "pin", "reviewed-plan", "profile-repin" }.Select(change => new object[] { phase, change }));

    [Theory]
    [MemberData(nameof(PromotionGovernanceChanges))]
    public async Task Governance_changes_during_proof_or_final_persistence_cannot_promote_ready(string phase, string change)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        if (phase == "proof") transport.OnProof = () => ChangeGovernance(arm.Product, change);
        else arm.Product.AfterPut = (key, _) =>
        {
            if (key.StartsWith("dsf/factory/telemetry-proofs/", StringComparison.Ordinal))
                ChangeGovernance(arm.Product, change);
        };

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.Equal(1, result.Exit);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Single(transport.Deployments);
        Assert.Equal(1, transport.Proofs);
        var persisted = ReadProductRecord(arm.Product, "dsf/factory/telemetry");
        Assert.NotEqual("Ready", (string?)persisted["state"]);
        Assert.Null(persisted["verifiedProofReference"]);
    }

    private static JsonNode ReadProductRecord(ReservationTestStore store, string key) => JsonNode.Parse(
        (string)JsonNode.Parse(store.ListJson())!.AsArray().Single(r => (string?)r!["key"] == key)!["value"]!)!;

    [Theory]
    [InlineData("manifest-intent", 0)]
    [InlineData("grant-intent", 0)]
    [InlineData("first-grant", 1)]
    [InlineData("second-grant", 2)]
    [InlineData("host-intent", 2)]
    public async Task Governance_loss_between_admitted_effects_stops_later_dispatch_without_losing_owned_history(string phase, int grants)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        transport.OnGrant = count =>
        {
            if (phase == "first-grant" && count == 1 || phase == "second-grant" && count == 2)
                ChangeGovernance(arm.Product, "profile-deleted");
        };
        arm.Product.AfterPut = (key, _) =>
        {
            if (phase == "manifest-intent" && key == "dsf/factory/manifest"
                || phase == "grant-intent" && key.StartsWith("dsf/factory/telemetry-operations/", StringComparison.Ordinal) && key.EndsWith("/0000", StringComparison.Ordinal)
                || phase == "host-intent" && key.StartsWith("dsf/factory/telemetry-operations/", StringComparison.Ordinal) && key.EndsWith("/0003", StringComparison.Ordinal))
                ChangeGovernance(arm.Product, "profile-deleted");
        };

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        await machine.InvokeAsync("resume");

        Assert.Equal(1, result.Exit);
        Assert.Equal(grants, transport.Grants.Count);
        Assert.Empty(transport.Deployments);
        Assert.Equal(0, transport.Proofs);
        var operations = JsonNode.Parse(arm.Product.ListJson())!.AsArray()
            .Where(r => ((string)r!["key"]!).StartsWith("dsf/factory/telemetry-operations/", StringComparison.Ordinal))
            .Select(r => JsonNode.Parse((string)r!["value"]!)!).ToArray();
        Assert.Equal(grants, operations.Count(o => (string?)o["disposition"] == "Created"));
        Assert.True((bool)result.Result["hasRegistryEffects"]!);
    }

    [Theory]
    [InlineData("profile-deleted")]
    [InlineData("profile-corrupt")]
    [InlineData("manifest-deleted")]
    [InlineData("manifest-corrupt")]
    [InlineData("publication")]
    [InlineData("identity")]
    [InlineData("pin")]
    [InlineData("reviewed-plan")]
    [InlineData("profile-repin")]
    public async Task Governance_changed_while_interactive_approval_is_open_creates_no_grants_or_host(string change)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var writes = arm.Product.Writes.Count;
        var prompted = false;
        var terminal = new ScriptedTerminal(new(true, false, false), [(string)preview.Result["telemetry"]!["approvalFingerprint"]!])
        {
            OnPrompt = message =>
            {
                Assert.StartsWith("Approve exact source", message);
                prompted = true;
                ChangeGovernance(arm.Product, change);
            },
        };

        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", "telemetry", .. Locator, .. Preparation, "--freshness-hours", "24"]);

        Assert.True(prompted);
        Assert.Equal(1, exit);
        Assert.Empty(transport.Grants);
        Assert.Empty(transport.Deployments);
        Assert.Equal(0, transport.Proofs);
        Assert.Equal(writes, arm.Product.Writes.Count);
    }

    private static readonly string[] Locator = ["--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
        "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription];

    private static void ChangeGovernance(ReservationTestStore store, string change)
    {
        const string manifest = "dsf/factory/manifest";
        const string profile = "dsf/factory/profile";
        if (change is "profile-deleted" or "manifest-deleted") store.Remove(change == "profile-deleted" ? profile : manifest);
        else if (change is "profile-corrupt" or "manifest-corrupt") store.Change(change == "profile-corrupt" ? profile : manifest, _ => "{corrupt");
        else if (change == "profile-repin")
        {
            string? pin = null;
            store.Change(profile, value =>
            {
                var changed = value + " ";
                pin = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(changed)));
                return changed;
            });
            store.Change(manifest, value =>
            {
                var current = JsonNode.Parse(value)!;
                current["profilePin"] = pin;
                return current.ToJsonString();
            });
        }
        else store.Change(manifest, value =>
        {
            var current = JsonNode.Parse(value)!;
            if (change == "publication") current["publication"] = "Active";
            if (change == "identity") current["identity"]!["azureActorId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
            if (change == "pin") current["profilePin"] = new string('b', 64);
            if (change == "reviewed-plan") current["reviewedPlanSha256"] = new string('b', 64);
            return current.ToJsonString();
        });
    }

    [Theory]
    [InlineData("table", "missing")]
    [InlineData("table", "null")]
    [InlineData("table", "\"Unknown\"")]
    [InlineData("table", "{}")]
    [InlineData("workspace", "missing")]
    [InlineData("workspace", "null")]
    [InlineData("workspace", "true")]
    [InlineData("workspace", "\"DataActionsOnly\"")]
    [InlineData("workspace", "\"Unknown\"")]
    [InlineData("workspace", "{}")]
    public async Task Unproven_authorization_metadata_blocks_preparation_without_repair(string field, string value)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        if (field == "table")
        {
            transport.OmitProtectionLevel = value == "missing";
            transport.ProtectionLevel = value == "missing" ? null : JsonNode.Parse(value);
        }
        else
        {
            transport.OmitDataAuthorizationMode = value == "missing";
            transport.DataAuthorizationMode = value == "missing" ? null : JsonNode.Parse(value);
        }
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var before = arm.Product.Export();
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Empty(transport.Grants);
        Assert.Empty(transport.Deployments);
        Assert.Equal(0, transport.Proofs);
        Assert.Equal(before, arm.Product.Export());
    }

    [Theory]
    [InlineData("Protected", false)]
    [InlineData("General", true)]
    public async Task Mixed_table_preparation_requires_supported_authorization_before_any_grants(string protection, bool ready)
    {
        var (machine, arm) = await FoundationAsync(mixedTables: true);
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm)
        { MixedTables = true, ProtectionLevel = JsonValue.Create(protection) };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var before = arm.Product.Export();

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.Equal(ready ? 0 : 1, result.Exit);
        Assert.Equal(ready ? "Reserved" : "Blocked", (string?)result.Result["state"]);
        Assert.Equal(ready ? 2 : 0, transport.Grants.Count);
        Assert.Equal(ready ? 1 : 0, transport.Deployments.Count);
        Assert.Equal(ready ? 1 : 0, transport.Proofs);
        if (!ready) Assert.Equal(before, arm.Product.Export());
    }

    [Fact]
    public async Task Source_host_stays_in_exact_reviewed_group_when_group_name_contains_hosting()
    {
        var (machine, arm) = await FoundationAsync(factoryGroup: "widget-hosting");
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.True(result.Exit == 0, result.Terminal.Output);
        var host = Assert.Single(transport.Resources.Keys, id => id.EndsWith("-telemetry", StringComparison.Ordinal));
        Assert.StartsWith(BoundaryPlanFixture.Root + "/resourcegroups/widget-hosting/providers/microsoft.app/containerapps/", host);
        Assert.Equal(1, transport.Proofs);
    }

    [Fact]
    public async Task Approved_cli_prepares_only_exact_source_grants_and_real_host_then_persists_remote_proof()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var ownerBefore = machine.Store.Export();

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        var status = await machine.InvokeAsync("status");

        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
        Assert.Equal("Ready", (string?)result.Result["telemetry"]!["state"]);
        Assert.True(JsonNode.DeepEquals(result.Result["telemetry"], status.Result["telemetry"]));
        Assert.Equal("Inactive", (string?)transport.Manifest["publication"]);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Equal(new[] { "516239f1-63e1-4d78-a4de-a74fb236a071", "73c42c96-874c-492b-b04d-ab87d138a893" },
            transport.Grants.Select(g => ((string)g["properties"]!["roleDefinitionId"]!).Split('/').Last()).Order().ToArray());
        Assert.Single(transport.Deployments);
        Assert.Equal(1, transport.Proofs);
        Assert.Equal(ownerBefore, machine.Store.Export());
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + arm.Product.Export());
    }

    [Fact]
    public async Task Malformed_deployment_operation_reference_is_not_persisted_as_creation_provenance()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm) { InvalidDeploymentOperationId = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal(0, transport.Proofs);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + arm.Product.Export());
    }

    [Fact]
    public async Task Native_current_reader_role_keeps_its_key_exclusion_and_backend_only_export_read_permission()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm) { CurrentReaderRole = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);

        Assert.True(result.Exit == 0, result.Terminal.Output);
        Assert.Equal(2, transport.Grants.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("recreated")]
    [InlineData("principal")]
    [InlineData("role")]
    [InlineData("scope")]
    [InlineData("condition")]
    public async Task Current_grant_failure_holds_readiness_and_preserves_historical_proof_without_recreation(string damage)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var first = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal(0, first.Exit);
        var grant = transport.Grants[1];
        var id = (string)grant["id"]!;
        if (damage == "missing") transport.Resources.Remove(id);
        if (damage == "recreated") grant["properties"]!["createdOn"] = "2026-09-15T23:00:01Z";
        if (damage == "principal") grant["properties"]!["principalId"] = Guid.NewGuid().ToString();
        if (damage == "role") grant["properties"]!["roleDefinitionId"] = "wrong-role";
        if (damage == "scope") grant["properties"]!["scope"] = BoundaryPlanFixture.Root;
        if (damage == "condition") grant["properties"]!["condition"] = "not approved";

        var check = await machine.InvokeAsync("telemetry", "--verify-only");
        var status = await machine.InvokeAsync("status");

        Assert.Equal(1, check.Exit);
        Assert.True((string?)status.Result["telemetry"]!["state"] == "Blocked", (string?)check.Result["detail"]);
        Assert.Equal("Ready", (string?)status.Result["telemetry"]!["proof"]!["state"]);
        Assert.True(JsonNode.DeepEquals(first.Result["telemetry"]!["proof"], status.Result["telemetry"]!["proof"]));
        Assert.Equal(1, transport.Proofs);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Single(transport.Deployments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_readiness_hold_starts_no_source_check_and_claims_no_revocation(bool responseLost)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!])).Exit);
        if (responseLost) arm.Product.LoseResponseForKey = "dsf/factory/telemetry";
        else arm.Product.RejectKey = "dsf/factory/telemetry";

        var result = await machine.InvokeAsync("telemetry", "--verify-only");

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Contains("no proof request or revocation asserted", result.Terminal.Output);
        Assert.Equal(1, transport.Proofs);
        Assert.Equal(2, transport.Grants.Count);
    }

    [Fact]
    public async Task Read_only_status_never_treats_expired_application_records_as_current_readiness()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!])).Exit);
        var persisted = arm.Product.Export();
        machine.Boundary.Repository.ReservationClock = new ExpiredClock();

        var status = await machine.InvokeAsync("status");

        Assert.Equal("Blocked", (string?)status.Result["telemetry"]!["state"]);
        Assert.Equal("Stale", (string?)status.Result["telemetry"]!["lastFailure"]);
        Assert.Equal("Ready", (string?)status.Result["telemetry"]!["proof"]!["state"]);
        Assert.Equal(persisted, arm.Product.Export());
        Assert.Equal(1, transport.Proofs);
    }

    private sealed class ExpiredClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T22:00:00.0000001Z");
    }

    [Fact]
    public async Task Evidence_expiring_during_the_check_is_historical_not_current_ready()
    {
        var (machine, arm) = await FoundationAsync();
        var clock = new AdvancingClock();
        machine.Boundary.Repository.ReservationClock = clock;
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm)
        {
            OnProof = () => clock.Now = clock.Now.AddTicks(1),
            ChangeProof = proof =>
            {
                proof["records"]![0]!["recordTime"] = proof["windowStart"]!.DeepClone();
                proof["queries"]![0]!["latestRecord"]!["recordTime"] = proof["windowStart"]!.DeepClone();
            },
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);

        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        var status = await machine.InvokeAsync("status");

        Assert.Equal("Blocked", (string?)result.Result["telemetry"]!["state"]);
        Assert.Equal("Stale", (string?)result.Result["telemetry"]!["lastFailure"]);
        Assert.Equal("Ready", (string?)status.Result["telemetry"]!["proof"]!["state"]);
        Assert.True(JsonNode.DeepEquals(result.Result["telemetry"], status.Result["telemetry"]));
    }

    private sealed class AdvancingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new ReservationClock().GetUtcNow();
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Inherited_group_authority_is_disclosed_before_any_source_grants_are_created()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        var before = arm.Product.Export();
        using var transport = new TelemetryWorkflowTransport(arm)
        {
            ExternalAssignment = new JsonObject { ["id"] = BoundaryPlanFixture.Root + "/providers/microsoft.authorization/roleassignments/" + Guid.NewGuid(),
                ["properties"] = new JsonObject { ["principalId"] = Guid.NewGuid().ToString(), ["principalType"] = "Group", ["scope"] = BoundaryPlanFixture.Root } },
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("inherited, group, preexisting", result.Terminal.Output);
        Assert.Empty(transport.Grants);
        Assert.Equal(before, arm.Product.Export());
    }

    [Theory]
    [InlineData(false, "Unknown")]
    [InlineData(true, "Failed")]
    public async Task Lost_or_rejected_grant_dispatch_is_persisted_and_never_retried(bool denied, string state)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm) { LoseGrantResponse = !denied, DenyGrant = denied };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        var status = await machine.InvokeAsync("status");
        await machine.InvokeAsync("resume");
        await machine.InvokeAsync("telemetry", "--verify-only");
        Assert.Equal(state, (string?)result.Result["state"]);
        Assert.Equal(state, (string?)status.Result["telemetry"]!["state"]);
        Assert.Equal(denied ? 0 : 1, transport.Grants.Count);
        Assert.Empty(transport.Deployments);
        Assert.Equal(0, transport.Proofs);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + arm.Product.Export());
    }

    [Fact]
    public async Task Rejected_host_dispatch_stays_failed_and_never_recreates_the_host_or_grants()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm) { DenyHost = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        var status = await machine.InvokeAsync("status");
        await machine.InvokeAsync("resume");
        await machine.InvokeAsync("telemetry", "--verify-only");

        Assert.Equal("Failed", (string?)result.Result["state"]);
        Assert.Equal("Failed", (string?)status.Result["telemetry"]!["state"]);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Single(transport.Deployments);
        Assert.Equal(0, transport.Proofs);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + arm.Product.Export());
    }

    [Fact]
    public async Task A_grant_removed_during_real_source_observation_cannot_publish_ready()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        transport.OnProof = () => transport.Resources.Remove((string)transport.Grants[0]["id"]!);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal("Blocked", (string?)result.Result["telemetry"]!["state"]);
        Assert.Null(result.Result["telemetry"]!["proof"]);
        Assert.Equal(1, transport.Proofs);
        Assert.Equal(2, transport.Grants.Count);
    }

    [Fact]
    public async Task Changed_current_role_permissions_cannot_be_hidden_by_successful_query_access()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        transport.OnProof = () => transport.BroaderReaderRole = true;
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal("Blocked", (string?)result.Result["telemetry"]!["state"]);
        Assert.Null(result.Result["telemetry"]!["proof"]);
    }

    [Fact]
    public async Task Empty_source_check_is_persisted_as_not_ready_without_diagnosing_a_quiet_application()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm) { ChangeProof = proof =>
        {
            proof["state"] = "Blocked"; proof["reason"] = "Empty"; proof["records"] = new JsonArray();
            proof["queries"]![0]!["reason"] = "Empty"; proof["queries"]![0]!["records"] = 0;
            proof["queries"]![0]!["latestRecord"] = null;
            proof["detail"] = "NOT READY: empty telemetry; no unhealthy diagnosis.";
        } };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        var status = await machine.InvokeAsync("status");
        Assert.Equal("Empty", (string?)status.Result["telemetry"]!["lastFailure"]);
        Assert.Equal("Blocked", (string?)status.Result["telemetry"]!["state"]);
        Assert.Contains("no unhealthy diagnosis", result.Terminal.Output);
        Assert.NotNull(status.Result["telemetry"]!["lastCheckReference"]);
        Assert.Contains("telemetry-proofs", arm.Product.Export());
    }

    [Theory]
    [InlineData("contradictory-count")]
    [InlineData("wrong-backend")]
    [InlineData("replayed-check")]
    [InlineData("missing-coverage")]
    public async Task Malformed_or_replayed_source_proof_cannot_be_recorded_as_ready(string damage)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm) { ChangeProof = proof =>
        {
            if (damage == "contradictory-count") proof["queries"]![0]!["records"] = 0;
            if (damage == "wrong-backend") proof["records"]![0]!["workspaceId"] = Guid.NewGuid().ToString();
            if (damage == "replayed-check") proof["checkId"] = Guid.NewGuid().ToString();
            if (damage == "missing-coverage") proof["coverageLimitations"] = "";
        } };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal("Unknown", (string?)result.Result["telemetry"]!["state"]);
        Assert.Null(result.Result["telemetry"]!["verifiedProofReference"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_persistence_failure_or_cancellation_leaves_the_confirmed_readiness_hold(bool canceled)
    {
        using var cancellation = new CancellationTokenSource();
        var (machine, arm) = await FoundationAsync(cancellation.Token);
        var preview = await machine.InvokeAsync("telemetry", Preparation);
        using var transport = new TelemetryWorkflowTransport(arm);
        transport.OnProof = () =>
        {
            if (canceled) cancellation.Cancel();
            else arm.Product.RejectKey = "dsf/factory/telemetry";
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("telemetry", [.. Preparation, "--approve",
            (string)preview.Result["telemetry"]!["approvalFingerprint"]!]);
        Assert.Equal(canceled ? 130 : 1, result.Exit);
        Assert.Equal(canceled ? "Canceled" : "Unknown", (string?)result.Result["state"]);
        var records = JsonNode.Parse(arm.Product.ListJson())!.AsArray();
        var persisted = JsonNode.Parse((string)records.Single(r => (string?)r!["key"] == "dsf/factory/telemetry")!["value"]!)!;
        Assert.Equal("Partial", (string?)persisted["state"]);
        Assert.Null(persisted["verifiedProofReference"]);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Single(transport.Deployments);
    }

    internal const string Component = BoundaryPlanFixture.Root + "/resourcegroups/app-a/providers/microsoft.insights/components/application";
    internal const string Image = "ghcr.io/joranbergfeld/dark-software-factory/runtime@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Audience = "77777777-7777-7777-7777-777777777777";
    internal static readonly string[] Preparation = ["--runtime-image", Image, "--auth-audience", Audience];

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0.00000001")]
    public async Task Telemetry_rejects_nonpositive_reviewed_freshness_without_provider_or_registry_effects(string hours)
    {
        var machine = new ReservationMachine(new ReservationTestStore());

        var result = await machine.InvokeAsync("telemetry", "--freshness-hours", hours);

        Assert.Equal(1, result.Exit);
        Assert.Equal("Failed", (string?)result.Result["state"]);
        Assert.Contains("positive", result.Terminal.Output);
        Assert.Empty(machine.Store.Writes);
        Assert.Empty(machine.Boundary.Calls);
    }

    [Fact]
    public async Task Preview_binds_the_source_identity_dedicated_backend_native_attribution_and_default_window_without_effects()
    {
        var (machine, arm) = await FoundationAsync();
        var before = arm.Product.Export();

        var result = await machine.InvokeAsync("telemetry", Preparation);

        Assert.Equal(1, result.Exit);
        Assert.Equal("Partial", (string?)result.Result["state"]);
        var configuration = result.Result["telemetry"]!["configuration"]!;
        Assert.Equal(86400, (int)configuration["freshnessSeconds"]!);
        Assert.Equal("azuremonitor-workspace-native-v1", (string?)configuration["queryRevision"]);
        Assert.Equal("44444444-4444-4444-4444-444444444444", (string?)configuration["backends"]![0]!["workspaceId"]);
        Assert.Equal(Component, (string?)configuration["backends"]![0]!["applicationResourceIds"]![0]);
        Assert.Equal("factory:source-azuremonitor", (string?)configuration["sourceIdentity"]!["logicalIdentity"]);
        Assert.Equal(before, arm.Product.Export());
        Assert.Contains("not every component", result.Terminal.Output);
    }

    internal static async Task<(ReservationMachine Machine, FactoryFoundationTests.FoundationTransport Arm)> FoundationAsync(
        CancellationToken token = default, string factoryGroup = "widget-decide", bool mixedTables = false,
        Action<BoundaryPlanFixture>? configure = null, TimeProvider? creationClock = null)
    {
        var machine = new ReservationMachine(new ReservationTestStore()) { Token = token };
        machine.Boundary.Input["factoryResourceGroup"] = factoryGroup;
        if (mixedTables)
        {
            machine.Boundary.Input["evidenceBackends"]![0]!["tables"]!.AsArray().Add("AppTraces");
            machine.Boundary.Overrides["https://management.azure.com" + BoundaryPlanFixture.Logs.ToLowerInvariant() + "/tables?api-version=2022-10-01"] =
                new(0, """{"value":[{"name":"AppRequests","properties":{"plan":"Analytics"}},{"name":"AppTraces","properties":{"plan":"Analytics"}}]}""", "");
        }
        var component = BoundaryPlanFixture.Resource(Component, "Microsoft.Insights/components");
        component["properties"]!["WorkspaceResourceId"] = BoundaryPlanFixture.Logs;
        machine.Boundary.Resources[Component] = component;
        machine.Boundary.Input["applicationResourceIds"]!.AsArray().Add(Component);
        configure?.Invoke(machine.Boundary);
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        Assert.Equal(0, (await machine.ReserveAsync(fingerprint)).Exit);
        var arm = new FactoryFoundationTests.FoundationTransport(machine.Store, creationClock) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        var provisioned = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);
        Assert.True(provisioned.Exit == 0, provisioned.Terminal.Output + provisioned.Terminal.Error);
        return (machine, arm);
    }
}
