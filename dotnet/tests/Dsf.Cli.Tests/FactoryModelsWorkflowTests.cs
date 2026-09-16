using System.Text.Json.Nodes;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryModelsWorkflowTests
{
    [Fact]
    public async Task Changed_native_deployment_revision_cannot_reuse_old_model_proof()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        var target = ModelsWorkflowTransport.Account + "/deployments/juror-2";
        transport.Resources[target] = ModelsWorkflowTransport.Deployment(transport.Topology["routes"]![3]!);
        transport.Resources[target]["etag"] = "\"replacement-revision\"";
        var result = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Equal(0, transport.Proofs);
    }

    [Fact]
    public async Task Native_account_endpoint_map_binds_the_reviewed_Azure_v1_route()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm) { Scenario = "native-endpoint-set" };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        Assert.Equal("Partial", (string?)result.Result["state"]);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task Native_host_runtime_status_changes_are_not_configuration_or_incarnation_replacements()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        transport.Resources.Single(p => p.Key.EndsWith("-models", StringComparison.Ordinal)).Value["properties"]!["runningStatus"] = "Running";
        var result = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(0, result.Exit);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("condition")]
    [InlineData("condition-version")]
    [InlineData("incarnation")]
    [InlineData("model")]
    [InlineData("missing-grant")]
    public async Task Changed_current_authority_or_exact_grant_during_proof_cannot_promote(string change)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        transport.OnProof = () =>
        {
            var grant = transport.Resources.First(p => p.Key.StartsWith(ModelsWorkflowTransport.Account + "/providers/microsoft.authorization/", StringComparison.Ordinal));
            if (change == "profile") arm.Product.Remove("dsf/factory/profile");
            if (change == "condition") grant.Value["properties"]!["condition"] = "unreviewed";
            if (change == "condition-version") grant.Value["properties"]!["conditionVersion"] = "2.0";
            if (change == "incarnation") grant.Value["properties"]!["createdOn"] = "2026-09-15T23:01:00Z";
            if (change == "missing-grant") transport.Resources.Remove(grant.Key);
            if (change == "model") transport.Resources.First(p => p.Key.EndsWith("-synthesis", StringComparison.Ordinal))
                .Value["properties"]!["model"]!["name"] = "gpt-4.1-mini";
        };
        var result = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(1, result.Exit);
        Assert.NotEqual("Verified", (string?)result.Result["models"]?["state"]);
        Assert.NotEqual("Verified", (string?)transport.Record("dsf/factory/models")["state"]);
        Assert.Equal(1, transport.Proofs);
        Assert.Equal(5, transport.Writes.Count);
    }

    [Theory]
    [InlineData("proof-stored")]
    [InlineData("promotion")]
    public async Task Governance_loss_during_proof_persistence_is_held_not_current_capability(string phase)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        arm.Product.AfterPut = (key, value) =>
        {
            if (phase == "proof-stored" && key.StartsWith("dsf/factory/model-proofs/", StringComparison.Ordinal)
                || phase == "promotion" && key == "dsf/factory/models" && (string?)JsonNode.Parse(value)!["state"] == "Verified")
                arm.Product.Remove("dsf/factory/profile");
        };
        var result = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(1, result.Exit);
        Assert.NotEqual("Verified", (string?)transport.Record("dsf/factory/models")["state"]);
    }

    [Fact]
    public async Task Failed_final_persistence_keeps_confirmed_checking_state_and_reports_unknown()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        transport.OnProof = () => arm.Product.RejectKey = "dsf/factory/models";
        var result = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal("checking", (string?)transport.Record("dsf/factory/models")["stage"]);
        Assert.Contains("unconfirmed", result.Terminal.Output);
    }

    [Theory]
    [InlineData("lost-create-response", 1, "Unknown")]
    [InlineData("denied-grant", 3, "Failed")]
    [InlineData("governance", 1, "Unknown")]
    public async Task Interrupted_preparation_retains_receipts_and_never_recreates_on_resume(string failure, int writes, string state)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm) { Scenario = failure };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        if (failure == "governance") transport.AfterWrite = _ => arm.Product.Remove("dsf/factory/profile");
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        var result = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        Assert.Equal(state, (string?)result.Result["state"]);
        Assert.Equal(writes, transport.Writes.Count);
        await machine.InvokeAsync("resume");
        await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(writes, transport.Writes.Count);
        Assert.Equal(0, transport.Proofs);
        Assert.DoesNotContain("unrelated-secret", result.Terminal.Output + arm.Product.Export());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interactive_approval_is_readonly_until_exact_fingerprint_and_current_governance(bool changeGovernance)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var topology = transport.Topology.ToJsonString();
        var preview = await machine.InvokeAsync("models", "--model-topology", topology);
        var terminal = new ScriptedTerminal(new(true, false, false), [(string)preview.Result["models"]!["approvalFingerprint"]!])
        {
            OnPrompt = _ => { if (changeGovernance) arm.Product.Remove("dsf/factory/profile"); },
        };
        var writes = arm.Product.Writes.Count;
        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", "models", "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
                "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription, "--model-topology", topology]);
        Assert.Equal(1, exit);
        Assert.Single(terminal.Prompts);
        Assert.Contains("dsf onboard decide models", terminal.Output);
        if (changeGovernance)
        {
            Assert.Empty(transport.Writes);
            Assert.Equal(writes, arm.Product.Writes.Count);
        }
        else Assert.Equal(5, transport.Writes.Count);
    }

    [Fact]
    public async Task Model_serving_review_cannot_turn_application_membership_into_shared_inference_authority()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync(configure: boundary =>
        {
            boundary.Resources[ModelsWorkflowTransport.Account] = BoundaryPlanFixture.Resource(ModelsWorkflowTransport.Account, "Microsoft.CognitiveServices/accounts");
            boundary.Input["applicationResourceIds"]!.AsArray().Add(ModelsWorkflowTransport.Account);
        });
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("application", result.Terminal.Output);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task Verification_holds_then_records_all_actual_host_routes_without_recreating_preparation()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        var writes = transport.Writes.Count;
        var result = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(0, result.Exit);
        Assert.Equal("Verified", (string?)result.Result["models"]?["state"]);
        Assert.Equal(5, result.Result["models"]!["proof"]!["observations"]!.AsArray().Count);
        Assert.NotNull(result.Result["models"]!["lastProofReference"]);
        Assert.DoesNotContain(result.Result["foundation"]!["pendingStages"]!.AsArray(), s => (string?)s == "models");
        Assert.Contains(result.Result["foundation"]!["pendingStages"]!.AsArray(), s => (string?)s == "council readiness");
        Assert.Equal(writes, transport.Writes.Count);
        Assert.Equal(1, transport.Proofs);
        var observed = await machine.InvokeAsync("status");
        Assert.Equal("Verified", (string?)observed.Result["models"]?["state"]);
        Assert.False((bool)observed.Result["isActivated"]!);
        Assert.Contains(arm.Product.Writes, w => w.Key == "dsf/factory/models" && (string?)JsonNode.Parse(w.Value)!["stage"] == "checking");
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public async Task Bounded_inputs_fit_the_complete_serialized_model_lifecycle_before_any_effect(int imageLength, bool accepted)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var topology = transport.Topology;
        topology["runtimeImage"] = "ghcr.io/" + new string('a', imageLength - 8 - 8 - 64) + "@sha256:" + new string('b', 64);
        foreach (var route in topology["routes"]!.AsArray()) route!["connectivityReview"] = new string('\uffff', 256);
        var preview = await machine.InvokeAsync("models", "--model-topology", topology.ToJsonString());
        if (!accepted)
        {
            Assert.Equal("Blocked", (string?)preview.Result["state"]);
            Assert.Empty(transport.Writes);
            return;
        }
        Assert.Equal("Partial", (string?)preview.Result["state"]);
        await machine.InvokeAsync("models", "--model-topology", topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        var verified = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(0, verified.Exit);
        foreach (var write in arm.Product.Writes.Where(w => w.Key.Contains("/model", StringComparison.Ordinal)))
        {
            var size = System.Text.Encoding.UTF8.GetByteCount(write.Key + "\0" + write.Value
                + "application/vnd.dsf.reservation+json;version=1") + 1024;
            Assert.True(size <= 10240, $"Full serialized lifecycle record {write.Key} used {size} bytes.");
        }
    }

    [Theory]
    [InlineData("initContainers")]
    [InlineData("volumes")]
    public async Task Unreviewed_host_execution_or_storage_is_not_adopted_as_model_preparation(string expansion)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        transport.AfterWrite = _ =>
        {
            var host = transport.Resources.FirstOrDefault(r => r.Key.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal)
                && !r.Key.EndsWith("/authconfigs/current", StringComparison.Ordinal)).Value;
            if (host is not null) host["properties"]!["template"]![expansion] = JsonNode.Parse("""[{"name":"unreviewed"}]""");
        };
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        var prepared = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        Assert.Equal("Unknown", (string?)prepared.Result["state"]);
        Assert.NotEqual("prepared", (string?)prepared.Result["models"]!["stage"]);
        Assert.Equal(0, transport.Proofs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_collection_elements_are_explicit_blockers_not_unhandled_success_or_crashes(bool hostedProof)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var topology = transport.Topology;
        if (!hostedProof)
        {
            topology["routes"]![2] = null;
            var malformed = await machine.InvokeAsync("models", "--model-topology", topology.ToJsonString());
            Assert.Equal("Blocked", (string?)malformed.Result["state"]);
            Assert.Empty(transport.Writes);
            return;
        }
        var preview = await machine.InvokeAsync("models", "--model-topology", topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        transport.MutateProof = proof => proof["observations"]![0] = null;
        var invalid = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal("Unknown", (string?)invalid.Result["state"]);
        Assert.NotEqual("Verified", (string?)transport.Record("dsf/factory/models")["state"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_cancellation_distinguishes_confirmed_holds_without_asserting_current_revocation(bool afterHold)
    {
        using var cancellation = new CancellationTokenSource();
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync(cancellation.Token);
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        Assert.Equal(0, (await machine.InvokeAsync("models", "--verify-only")).Exit);
        var effects = transport.Writes.Count;
        if (afterHold) transport.OnProof = cancellation.Cancel;
        else transport.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Put && Uri.UnescapeDataString(request.RequestUri!.AbsolutePath) == "/kv/dsf/factory/models")
                cancellation.Cancel();
        };
        var canceled = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(afterHold ? 130 : 1, canceled.Exit);
        Assert.Equal(afterHold ? "Canceled" : "Unknown", (string?)canceled.Result["state"]);
        if (afterHold)
        {
            Assert.Contains("after a hold was confirmed", canceled.Terminal.Output);
            Assert.Contains("No revocation asserted", canceled.Terminal.Output);
        }
        else Assert.DoesNotContain("hold was confirmed", canceled.Terminal.Output);
        Assert.Null(canceled.Result["models"]!["verifiedUntil"]);
        Assert.Equal(afterHold ? "Partial" : "Verified", (string?)transport.Record("dsf/factory/models")["state"]);
        Assert.Equal(effects, transport.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_native_role_must_cover_MaaS_chat_without_wildcards_or_definition_changes(bool maas)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm) { IncludeMaaSChat = maas };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        Assert.Equal(maas ? "Partial" : "Blocked", (string?)preview.Result["state"]);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task Approved_preparation_owns_only_two_deployments_exact_grants_and_the_isolated_host()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var owner = machine.Store.Export();
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        var result = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        Assert.Equal("prepared", (string?)result.Result["models"]?["stage"]);
        Assert.Equal(2, transport.Writes.Count(p => p.StartsWith(ModelsWorkflowTransport.Account + "/deployments/", StringComparison.Ordinal)));
        Assert.Equal(2, transport.Writes.Count(p => p.Contains("/roleassignments/", StringComparison.Ordinal)));
        Assert.DoesNotContain(transport.Writes, p => p.Contains("/juror-", StringComparison.Ordinal));
        Assert.Equal(owner, machine.Store.Export());
        Assert.Equal("Inactive", (string?)transport.Manifest["publication"]);
        var status = await machine.InvokeAsync("status");
        Assert.Equal(6, status.Result["models"]!["effects"]!.AsArray().Count);
        Assert.All(status.Result["models"]!["effects"]!.AsArray(), e =>
        {
            Assert.Equal("Created", (string?)e!["disposition"]);
            Assert.NotNull(e["incarnation"]);
            Assert.NotNull(e["providerRequestId"]);
            Assert.NotNull(e["requestSha256"]);
        });
    }

    [Fact]
    public async Task Preview_reviews_two_factory_deployments_and_three_existing_families_without_mutations()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var before = arm.Product.Export();
        var result = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        Assert.Equal(1, result.Exit);
        Assert.Equal("Partial", (string?)result.Result["state"]);
        var models = result.Result["models"]!;
        Assert.Equal(5, models["routes"]!.AsArray().Count);
        Assert.Equal(2, models["routes"]!.AsArray().Count(r => (bool)r!["selection"]!["create"]!));
        Assert.Equal(new[] { "gpt", "deepseek", "grok" }, models["routes"]!.AsArray().Skip(2)
            .Select(r => (string)r!["selection"]!["family"]!));
        Assert.All(models["routes"]!.AsArray().Skip(2), r => Assert.NotNull(r!["deploymentObservation"]));
        Assert.Equal(before, arm.Product.Export());
        Assert.Empty(transport.Writes);
        Assert.False((bool)result.Result["isActivated"]!);
    }

    [Fact]
    public async Task Missing_topology_is_a_named_capability_blocker_without_effects()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var before = arm.Product.Export();
        var result = await machine.InvokeAsync("models");
        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("--model-topology", result.Terminal.Output);
        Assert.Equal(before, arm.Product.Export());
    }
}
