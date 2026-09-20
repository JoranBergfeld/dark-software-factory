using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryReconfigurationReviewTests
{
    [Fact]
    public async Task Current_input_export_and_exact_diff_are_read_only_across_reconstructed_clients()
    {
        using var transport = await FactoryActivationTests.ReadyAsync(selectedSources: true);
        var machine = transport.Scenario.Machine;
        var before = transport.Scenario.Arm.Product.Export();
        var owner = machine.Store.Export();
        var documents = transport.SeedDocuments.ToDictionary();
        var models = transport.EvaluationInference.Count;
        var requests = transport.Requests.Count;
        var exported = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(exported.Exit == 0, exported.Terminal.Output + exported.Terminal.Error);
        Assert.Equal("Inspected", (string?)exported.Result["state"]);
        var input = exported.Result["currentInputs"]!.DeepClone();
        input["telemetry"]!["freshnessSeconds"] = 7200;
        input["judgment"]!["threshold"] = 0.7;
        input["supplemental"]!["sources"]!.AsArray().RemoveAt(0);
        var staged = await ReviewAsync(machine, input);
        Assert.True(staged.Exit == 0, staged.Terminal.Output + staged.Terminal.Error);
        Assert.Equal("Reviewed", (string?)staged.Result["state"]);
        Assert.True((bool)staged.Result["renewedApprovalRequired"]!);
        Assert.False((bool)staged.Result["hasRemoteEffects"]!);
        Assert.False((bool)staged.Result["canApply"]!);
        AssertChange(staged.Result, "telemetry", "freshnessSeconds", "Replacement");
        AssertChange(staged.Result, "judgment", "configuration", "Replacement");
        Assert.Contains(staged.Result["changes"]!.AsArray(), c => (string?)c!["category"] == "source" && (string?)c["kind"] == "Removal");
        Assert.NotEmpty(staged.Result["ownershipHistory"]!.AsArray());
        Assert.NotEmpty(staged.Result["affectedEvidence"]!.AsArray());
        Assert.NotNull(staged.Result["keyCustody"]);
        Assert.Contains(staged.Result["changes"]!.AsArray(), c => (string?)c!["category"] == "grant" && (string?)c["kind"] == "Removal");
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(before);
        FactoryActivationTests.ConnectAudit(transport);
        var repeated = await ReviewAsync(machine, input, "--reviewed-fingerprint", (string)staged.Result["fingerprint"]!);
        Assert.True(repeated.Exit == 0, repeated.Terminal.Output);
        Assert.Equal((string?)staged.Result["fingerprint"], (string?)repeated.Result["fingerprint"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Equal(owner, machine.Store.Export());
        Assert.Equal(documents, transport.SeedDocuments);
        Assert.Equal(models, transport.EvaluationInference.Count);
        Assert.All(transport.Requests.Skip(requests), r => Assert.Equal("GET", r.Method));
        Assert.Empty(exported.Terminal.Prompts);
    }

    [Theory]
    [InlineData("tenantId", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("subscriptionId", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("applicationEnvironment", "staging")]
    [InlineData("productKey", "another")]
    [InlineData("ownerAppConfigEndpoint", "https://other.azconfig.io")]
    public async Task Identity_changes_require_new_onboarding_without_mutation(string field, string value)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        input["boundary"]![field] = value;
        var before = transport.Scenario.Arm.Product.Export();
        var reviewed = await ReviewAsync(machine, input);
        Assert.Equal(1, reviewed.Exit);
        Assert.Equal("Blocked", (string?)reviewed.Result["state"]);
        Assert.Contains("NewOnboardingRequired", (string?)reviewed.Result["detail"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task New_durable_repository_is_rejected_but_rename_and_default_branch_metadata_preserve_identity()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        input["boundary"]!["repository"] = "acme/renamed";
        input["boundary"]!["boundaryReview"] = null;
        var response = machine.Boundary.Repository.Responses["repos/acme/renamed"];
        var repository = JsonNode.Parse(response.StandardOutput)!;
        repository["default_branch"] = "release-next";
        machine.Boundary.Repository.Responses["repos/acme/renamed"] = response with
        { StandardOutput = repository.ToJsonString() };
        var metadata = await ReviewBoundaryAsync(machine, input);
        Assert.True(metadata.Exit == 0, metadata.Terminal.Output);
        Assert.False((bool)metadata.Result["renewedApprovalRequired"]!);
        Assert.Contains(metadata.Result["changes"]!.AsArray(), c => (string?)c!["category"] == "repository" && (string?)c["kind"] == "Metadata");
        Assert.Equal(transport.Manifest.Identity.FactoryId.ToString(), (string?)metadata.Result["factoryId"]);
        repository["id"] = 914;
        machine.Boundary.Repository.Responses["repos/acme/renamed"] = response with { StandardOutput = repository.ToJsonString() };
        var retarget = await ReviewAsync(machine, input);
        Assert.Equal("Blocked", (string?)retarget.Result["state"]);
        Assert.Contains("NewOnboardingRequired", (string?)retarget.Result["detail"]);
    }

    [Fact]
    public async Task Membership_addition_requires_fresh_dedication_and_distinguishes_claim_from_preserved_application_asset()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        const string added = BoundaryPlanFixture.Data + "-new";
        machine.Boundary.Resources[added] = BoundaryPlanFixture.Resource(added, "Microsoft.Storage/storageAccounts");
        input["boundary"]!["applicationResourceIds"]!.AsArray().Add(added);
        var owner = machine.Store.Export();
        var stale = await ReviewAsync(machine, input);
        Assert.Equal("Blocked", (string?)stale.Result["state"]);
        var reviewed = await ReviewBoundaryAsync(machine, input);
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        AssertChange(reviewed.Result, "application", added.ToLowerInvariant(), "Addition");
        Assert.Contains(reviewed.Result["changes"]!.AsArray(), c => (string?)c!["category"] == "claim" && (string?)c["kind"] == "Addition");
        Assert.Contains(reviewed.Result["preservedAssets"]!.AsArray(), c => (string?)c == added.ToLowerInvariant());
        Assert.DoesNotContain(reviewed.Result["ownershipHistory"]!.AsArray(), c => (string?)c!["target"] == added.ToLowerInvariant());
        Assert.Equal(owner, machine.Store.Export());
    }

    [Theory]
    [InlineData("missing-models")]
    [InlineData("missing-optional-opt-in")]
    [InlineData("query")]
    [InlineData("freshness")]
    [InlineData("juror-family")]
    [InlineData("judgment")]
    [InlineData("source")]
    [InlineData("schema")]
    public async Task Incomplete_or_unsupported_selections_never_become_a_reviewed_change(string invalid)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync(web: true);
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        switch (invalid)
        {
            case "missing-models": input.AsObject().Remove("models"); break;
            case "missing-optional-opt-in": input["supplemental"]!["sources"]![0]!["publicQueryApproved"] = false; break;
            case "query": input["telemetry"]!["queries"]!["AppRequests"] = "union *"; break;
            case "freshness": input["telemetry"]!["freshnessSeconds"] = 0; break;
            case "juror-family": input["models"]!["routes"]![3]!["family"] = "gpt"; break;
            case "judgment": input["judgment"]!["rounds"] = 0; break;
            case "source": input["source"]!["runtimeImage"] = "unreviewed:latest"; break;
            case "schema": input["schemaVersion"] = 2; break;
        }
        var before = transport.Scenario.Arm.Product.Export();
        var reviewed = await ReviewAsync(machine, input);
        Assert.Equal(1, reviewed.Exit);
        Assert.Equal("Blocked", (string?)reviewed.Result["state"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_optional_source_additions_review_real_prerequisites_without_creating_or_querying_sources(bool foundry)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        Assert.Null(input["supplemental"]);
        transport.ConfigureSupplementalPrerequisites();
        input["supplemental"] = FactorySupplementalWorkflowTests.Selection();
        if (foundry) input["supplemental"]!["sources"]!.AsArray().Add(FactorySupplementalWorkflowTests.FoundrySelection());
        var before = transport.Scenario.Arm.Product.Export();
        var owner = machine.Store.Export();
        var requests = transport.Requests.Count;
        var review = await ReviewAsync(machine, input);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        AssertChange(review.Result, "source", "webiq", "Addition");
        if (foundry) AssertChange(review.Result, "source", "foundryiq", "Addition");
        Assert.Contains(review.Result["changes"]!.AsArray(), c => (string?)c!["category"] == "grant" && (string?)c["kind"] == "Addition");
        Assert.Equal(0, transport.WebQueries);
        Assert.Equal(0, transport.FoundryQueries);
        Assert.All(transport.Requests.Skip(requests), r => Assert.Equal("GET", r.Method));
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Equal(owner, machine.Store.Export());
    }

    [Fact]
    public async Task Deliberate_pause_and_changed_current_revision_invalidate_replayed_diff_without_clearing_pause()
    {
        using var transport = await FactoryActivationTests.ReadyAsync();
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        input["telemetry"]!["freshnessSeconds"] = 7200;
        var reviewed = await ReviewAsync(machine, input);
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        var pause = await machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
        var before = transport.Scenario.Arm.Product.Export();
        var stale = await ReviewAsync(machine, input, "--reviewed-fingerprint", (string)reviewed.Result["fingerprint"]!);
        Assert.Equal("Blocked", (string?)stale.Result["state"]);
        Assert.Contains("StaleReconfigurationReview", (string?)stale.Result["detail"]);
        var fresh = await ReviewAsync(machine, input);
        Assert.True(fresh.Exit == 0, fresh.Terminal.Output);
        Assert.True((bool)fresh.Result["deliberatelyPaused"]!);
        Assert.NotEqual((string?)reviewed.Result["fingerprint"], (string?)fresh.Result["fingerprint"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Changed_model_and_removed_optional_sources_keep_original_grant_and_key_history()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync(web: true, foundry: true);
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        input["models"]!["routes"]![0]!["version"] = "2026-09-01";
        input["models"]!["runtimeImage"] = "registry.example/dsf@sha256:" + new string('b', 64);
        input["telemetry"]!["runtimeImage"] = "registry.example/dsf@sha256:" + new string('b', 64);
        input["supplemental"] = null;
        var before = transport.Scenario.Arm.Product.Export();
        var review = await ReviewAsync(machine, input);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        AssertChange(review.Result, "model", "synthesis", "Replacement");
        AssertChange(review.Result, "model", "host", "Replacement");
        AssertChange(review.Result, "telemetry", "host", "Replacement");
        AssertChange(review.Result, "source", "foundryiq", "Removal");
        AssertChange(review.Result, "source", "webiq", "Removal");
        Assert.True(JsonNode.DeepEquals(current.Result["keyCustody"], review.Result["keyCustody"]));
        Assert.True(JsonNode.DeepEquals(current.Result["ownershipHistory"], review.Result["ownershipHistory"]));
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("dsf-product")]
    public async Task Changed_membership_cannot_adopt_shared_or_tag_claimed_application_resources(string tag)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        const string added = BoundaryPlanFixture.Data + "-claimed";
        machine.Boundary.Resources[added] = BoundaryPlanFixture.Resource(added, "Microsoft.Storage/storageAccounts");
        machine.Boundary.Resources[added]["tags"]![tag] = tag == "shared" ? "true" : "private-value-do-not-print";
        input["boundary"]!["applicationResourceIds"]!.AsArray().Add(added);
        var before = machine.Store.Export();
        var review = await ReviewBoundaryAsync(machine, input);
        Assert.Equal(1, review.Exit);
        Assert.Equal("Blocked", (string?)review.Result["state"]);
        Assert.DoesNotContain("private-value-do-not-print", review.Terminal.Output + review.Terminal.Error);
        Assert.Equal(before, machine.Store.Export());
    }

    [Fact]
    public async Task Concurrent_owner_transition_during_native_inspection_does_not_release_a_current_review()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        input["telemetry"]!["freshnessSeconds"] = 7200;
        var raced = false;
        transport.ArmReadStatus = _ =>
        {
            if (!raced)
            {
                raced = true;
                machine.Store.Change("dsf/onboarding/products/widget", value =>
                {
                    var root = JsonNode.Parse(value)!;
                    root["foundation"]!["coordinatorId"] = Guid.NewGuid().ToString();
                    return root.ToJsonString();
                });
            }
            return null;
        };
        var before = transport.Scenario.Arm.Product.Export();
        var review = await ReviewAsync(machine, input);
        Assert.True(raced);
        Assert.Equal("Blocked", (string?)review.Result["state"]);
        Assert.Contains("ConcurrentReconfigurationReview", (string?)review.Result["detail"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Missing_current_governance_with_surviving_history_is_unknown_not_local_input_authority()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        transport.Scenario.Arm.Product.Remove("dsf/factory/models");
        var before = transport.Scenario.Arm.Product.Export();
        var review = await ReviewAsync(machine, input);
        Assert.Equal("Unknown", (string?)review.Result["state"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Malformed_native_preview_authority_is_unknown_and_never_a_desired_input_error()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryPreview, _ => "{}");
        var before = transport.Scenario.Arm.Product.Export();
        var review = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.Equal("Unknown", (string?)review.Result["state"]);
        Assert.False((bool)review.Result["hasRemoteEffects"]!);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Same_factory_marker_on_an_unadmitted_claim_does_not_authorize_adoption()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        var added = (BoundaryPlanFixture.Data + "-unadmitted").ToLowerInvariant();
        machine.Boundary.Resources[added] = BoundaryPlanFixture.Resource(added, "Microsoft.Storage/storageAccounts");
        input["boundary"]!["applicationResourceIds"]!.AsArray().Add(added);
        var key = ProductConfigurationKeys.OwnerResourceClaim(added);
        var value = new
        {
            schemaVersion = 1, kind = "decide-observation-claim", identity = transport.Manifest.Identity,
            claim = new { kind = "resource", target = added, key, label = "\0" },
        };
        using var client = new HttpClient(machine.Store.Client());
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://owner.azconfig.io/kv/" + Uri.EscapeDataString(key))
        { Content = new StringContent(JsonSerializer.Serialize(new
            { value = JsonSerializer.Serialize(value, new JsonSerializerOptions(Dsf.Core.Onboarding.FactoryGitHubJson.Options)
                { IgnoreReadOnlyProperties = false }),
                content_type = "application/vnd.dsf.reservation+json;version=1" }), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var before = machine.Store.Export();
        var review = await ReviewBoundaryAsync(machine, input);
        Assert.Equal("Blocked", (string?)review.Result["state"]);
        Assert.Contains("owner-admission", review.Result["findings"]!.ToJsonString());
        Assert.Equal(before, machine.Store.Export());
    }

    [Fact]
    public async Task Replacing_a_dedicated_backend_and_table_queries_lists_exact_claim_and_grant_changes_without_deleting_old_assets()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        var logs = (BoundaryPlanFixture.Logs + "-new").ToLowerInvariant();
        var component = FactoryTelemetryWorkflowTests.Component + "-new";
        machine.Boundary.Resources[logs] = BoundaryPlanFixture.Resource(logs, "Microsoft.OperationalInsights/workspaces");
        machine.Boundary.Resources[logs]["properties"]!["customerId"] = "66666666-6666-6666-6666-666666666666";
        machine.Boundary.Resources[component] = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
        machine.Boundary.Resources[component]["properties"]!["WorkspaceResourceId"] = logs;
        transport.Resources[logs] = machine.Boundary.Resources[logs].DeepClone();
        transport.Resources[logs]["properties"]!["features"] = new JsonObject
        { ["enableLogAccessUsingOnlyResourcePermissions"] = false, ["dataAuthorizationMode"] = false };
        transport.Resources[logs + "/tables/AppTraces"] = new JsonObject
        { ["id"] = logs + "/tables/AppTraces", ["properties"] = new JsonObject { ["plan"] = "Analytics", ["protectionLevel"] = "General" } };
        transport.Resources[component] = machine.Boundary.Resources[component].DeepClone();
        machine.Boundary.Overrides["https://management.azure.com" + logs + "/tables?api-version=2022-10-01"] =
            new(0, """{"value":[{"name":"AppTraces","properties":{"plan":"Analytics"}}]}""", "");
        input["boundary"]!["applicationResourceIds"] = new JsonArray(input["boundary"]!["applicationResourceIds"]!.AsArray()
            .Where(r => (string?)r != FactoryTelemetryWorkflowTests.Component).Select(r => r!.DeepClone()).Append(JsonValue.Create(component)).ToArray());
        input["boundary"]!["evidenceBackends"] = new JsonArray(new JsonObject { ["resourceId"] = logs, ["tables"] = new JsonArray("AppTraces") });
        input["telemetry"]!["queries"] = new JsonObject { ["AppTraces"] = "AppTraces | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type" };
        input["telemetry"]!["staleQueries"] = new JsonObject { ["AppTraces"] = "AppTraces | top 1 by TimeGenerated desc | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type" };
        var before = transport.Scenario.Arm.Product.Export();
        var owner = machine.Store.Export();
        var review = await ReviewBoundaryAsync(machine, input);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        AssertChange(review.Result, "backend", logs, "Addition");
        AssertChange(review.Result, "backend", BoundaryPlanFixture.Logs.ToLowerInvariant(), "Removal");
        AssertChange(review.Result, "query", "AppTraces", "Addition");
        AssertChange(review.Result, "query", "AppRequests", "Removal");
        Assert.Contains(review.Result["changes"]!.AsArray(), c => (string?)c!["category"] == "grant"
            && (string?)c["kind"] == "Addition" && (string?)c["after"]!["scope"] == logs);
        Assert.Contains(review.Result["changes"]!.AsArray(), c => (string?)c!["category"] == "grant"
            && (string?)c["kind"] == "Removal" && (string?)c["before"]!["scope"] == BoundaryPlanFixture.Logs.ToLowerInvariant());
        Assert.Contains(review.Result["preservedAssets"]!.AsArray(), r => (string?)r == BoundaryPlanFixture.Logs.ToLowerInvariant());
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Equal(owner, machine.Store.Export());
    }

    [Theory]
    [InlineData("telemetry-authorization")]
    [InlineData("search-network")]
    [InlineData("supplemental-delegation")]
    public async Task Unsupported_native_source_authority_cannot_produce_a_reviewed_diff(string failure)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync(foundry: true);
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        input["supplemental"]!["sources"]![0]!["query"] = "Updated public product documentation";
        if (failure == "telemetry-authorization") transport.Telemetry.DataAuthorizationMode = JsonValue.Create(true);
        if (failure == "supplemental-delegation") transport.DeniedAction = "Microsoft.Authorization/roleAssignments/write";
        if (failure == "search-network")
        {
            var search = (string)input["supplemental"]!["sources"]![0]!["searchResourceId"]!;
            transport.Resources[search] = new JsonObject { ["id"] = search,
                ["properties"] = new JsonObject { ["publicNetworkAccess"] = "Enabled", ["disableLocalAuth"] = true } };
        }
        var before = transport.Scenario.Arm.Product.Export();
        var review = await ReviewAsync(machine, input);
        Assert.Equal("Blocked", (string?)review.Result["state"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Changed_app_permission_authority_requires_administrator_review_not_reconfiguration_consent()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        var retained = machine.Boundary.Repository.AppResponses["/app"];
        var metadata = JsonNode.Parse(retained.Json)!;
        metadata["permissions"]!["contents"] = "write";
        machine.Boundary.Repository.AppResponses["/app"] = (retained.Status, metadata.ToJsonString());
        var before = transport.Scenario.Arm.Product.Export();
        var review = await ReviewAsync(machine, input);
        Assert.Equal("Blocked", (string?)review.Result["state"]);
        Assert.Contains("GitHubAuthorityAdministratorRequired", (string?)review.Result["detail"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Changed_accountable_dedication_owner_is_an_explicit_governing_difference()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        FactoryActivationTests.ConnectAudit(transport);
        var machine = transport.Scenario.Machine;
        var current = await machine.InvokeAsync("reconfigure", "--inspect");
        Assert.True(current.Exit == 0, current.Terminal.Output);
        var input = current.Result["currentInputs"]!.DeepClone();
        input["boundary"]!["boundaryReview"]!["accountableOwner"] = "acme/security";
        var before = transport.Scenario.Arm.Product.Export();
        var review = await ReviewAsync(machine, input);
        Assert.True(review.Exit == 0, review.Terminal.Output);
        Assert.True((bool)review.Result["renewedApprovalRequired"]!);
        AssertChange(review.Result, "boundary", "dedication", "Replacement");
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData()]
    [InlineData("--inspect", "--approve", "yes")]
    [InlineData("--reviewed-fingerprint", "yes")]
    public async Task Redirected_commands_require_explicit_supported_modes(params string[] arguments)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        var exit = await CliApplication.InvokeAsync(["onboard", "decide", "reconfigure",
            "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
            "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription,
            "--json", .. arguments], CancellationToken.None, terminal);
        Assert.NotEqual(0, exit);
        Assert.Empty(terminal.Prompts);
    }

    private static void AssertChange(JsonNode result, string category, string target, string kind) =>
        Assert.Contains(result["changes"]!.AsArray(), change => (string?)change!["category"] == category
            && (string?)change["target"] == target && (string?)change["kind"] == kind);

    private static async Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> ReviewBoundaryAsync(
        ReservationMachine machine, JsonNode input)
    {
        input["boundary"]!["boundaryReview"] = null;
        var draft = await ReviewAsync(machine, input);
        Assert.NotNull(draft.Result["selectionFingerprint"]);
        input["boundary"]!["boundaryReview"] = new JsonObject
        {
            ["selectionFingerprint"] = (string)draft.Result["selectionFingerprint"]!,
            ["accountableOwner"] = "acme/platform", ["dedicated"] = true, ["crossOwnerExclusive"] = true,
        };
        return await ReviewAsync(machine, input);
    }

    private static async Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> ReviewAsync(
        ReservationMachine machine, JsonNode input, params string[] arguments)
    {
        var directory = Path.Combine("artifacts", "onboard220", "inputs");
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"));
        try
        {
            await File.WriteAllTextAsync(path, input.ToJsonString());
            return await machine.InvokeAsync("reconfigure", ["--input", path, .. arguments]);
        }
        finally { File.Delete(path); }
    }
}
