using System.Text.Json.Nodes;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactorySupplementalWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interactive_approval_prints_complete_explicit_command_and_rechecks_current_governance(bool changed)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Selection().ToJsonString());
            var preview = await machine.InvokeAsync("supplemental", "--input", file);
            var approval = (string)preview.Result["supplemental"]!["approvalFingerprint"]!;
            var terminal = new ScriptedTerminal(new(true, false, false), [approval])
            {
                OnPrompt = _ => { if (changed) arm.Product.Remove("dsf/factory/profile"); },
            };
            var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal,
                ["onboard", "decide", "supplemental", "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
                    "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription, "--input", file]);
            Assert.Equal(changed ? 1 : 0, exit);
            Assert.Single(terminal.Prompts);
            Assert.Contains("dsf onboard decide supplemental --owner-appconfig-endpoint https://owner.azconfig.io --product widget", terminal.Output);
            Assert.Contains("--input " + file + " --approve " + approval, terminal.Output);
            Assert.Equal(changed ? 0 : 3, transport.Writes.Count);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Identity_replaced_during_grant_intent_stops_dispatch_before_granting_old_principal()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        arm.Product.AfterPut = (key, _) =>
        {
            if (key.StartsWith("dsf/factory/supplemental-operations/", StringComparison.Ordinal) && key.EndsWith("/0001", StringComparison.Ordinal))
                transport.Resources.Single(p => p.Key.Contains("/userassignedidentities/", StringComparison.Ordinal))
                    .Value["properties"]!["principalId"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        };
        var result = await PrepareWebAsync(machine);
        Assert.Equal(1, result.Exit);
        Assert.Empty(transport.Grants);
        Assert.Single(transport.Writes);
        Assert.Equal(0, transport.Proofs);
    }

    internal static Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> PrepareWebAsync(ReservationMachine machine) =>
        PrepareSelectionAsync(machine, Selection());

    internal static async Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> PrepareSelectionAsync(ReservationMachine machine, JsonNode selection)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, selection.ToJsonString());
            var preview = await machine.InvokeAsync("supplemental", "--input", file);
            Assert.True(preview.Result["supplemental"] is not null, (string?)preview.Result["detail"]);
            return await machine.InvokeAsync("supplemental", "--input", file, "--approve", (string)preview.Result["supplemental"]!["approvalFingerprint"]!);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foundry_opt_in_pins_existing_search_and_deploys_only_selected_distinct_sources(bool webToo)
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var selection = Selection();
        if (!webToo) selection["sources"] = new JsonArray();
        selection["sources"]!.AsArray().Add(FoundrySelection());
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, selection.ToJsonString());
            var preview = await machine.InvokeAsync("supplemental", "--input", file);
            Assert.True(preview.Result["supplemental"] is not null, preview.Terminal.Output);
            var result = await machine.InvokeAsync("supplemental", "--input", file, "--approve",
                (string)preview.Result["supplemental"]!["approvalFingerprint"]!);
            Assert.True(result.Exit == 0, (string?)result.Result["detail"]);
            Assert.Equal(webToo ? 2 : 1, transport.Proofs);
            Assert.Equal(webToo ? 7 : 4, transport.Writes.Count);
            Assert.Equal(webToo ? 3 : 2, transport.Grants.Count);
            var identities = transport.Resources.Where(p => p.Key.Contains("/userassignedidentities/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(webToo ? 2 : 1, identities.Length);
            Assert.Equal(identities.Length, identities.Select(i => (string?)i.Value["properties"]!["principalId"]).Distinct().Count());
            Assert.DoesNotContain(transport.Writes, w => w.Contains("/searchservices/", StringComparison.Ordinal) && !w.Contains("/roleassignments/", StringComparison.Ordinal));
            Assert.Null(result.Result["telemetry"]);
        }
        finally { File.Delete(file); }
    }

    internal static JsonNode FoundrySelection() => new JsonObject
    {
        ["kind"] = "foundryiq", ["query"] = "Public retail accessibility research", ["publicQueryApproved"] = true,
        ["searchResourceId"] = SupplementalWorkflowTransport.Search,
        ["knowledgeBase"] = "product-kb", ["knowledgeBaseETag"] = "kb-v1", ["dedicatedSearchService"] = true,
        ["metadataRoleDefinitionId"] = BoundaryPlanFixture.Root.ToLowerInvariant() + "/providers/microsoft.authorization/roledefinitions/77777777-7777-7777-7777-777777777777",
        ["knowledgeSources"] = new JsonArray(new JsonObject
        {
            ["name"] = "product-docs", ["indexName"] = "product-index", ["sourceETag"] = "source-v1", ["indexETag"] = "index-v1",
        }),
    };

    [Fact]
    public async Task Approved_selection_creates_only_isolated_web_workload_and_secret_scoped_grant_and_persists_empty_proof()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new SupplementalWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Selection().ToJsonString());
            var preview = await machine.InvokeAsync("supplemental", "--input", file);
            Assert.True(preview.Result["supplemental"] is not null, preview.Terminal.Output);
            var approval = (string)preview.Result["supplemental"]!["approvalFingerprint"]!;
            var result = await machine.InvokeAsync("supplemental", "--input", file, "--approve", approval);

            Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
            Assert.Equal("ReadySupplemental", (string?)result.Result["supplemental"]!["state"]);
            Assert.Equal(3, transport.Writes.Count);
            Assert.Single(transport.Grants);
            Assert.EndsWith("/secrets/webiq-api-key", (string)transport.Grants[0]["properties"]!["scope"]!);
            Assert.Equal(1, transport.Proofs);
            Assert.Null(result.Result["telemetry"]);
            Assert.False((bool)result.Result["isActivated"]!);
            var observed = await machine.InvokeAsync("supplemental");
            Assert.Equal(approval, (string?)observed.Result["supplemental"]!["approvalFingerprint"]);
            Assert.Equal("Empty", (string?)observed.Result["supplemental"]!["checks"]![0]!["reason"]);
            Assert.DoesNotContain("secret-key-do-not-print", arm.Product.Export() + result.Terminal.Output);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Explicit_web_selection_reviews_only_product_secret_metadata_without_reading_or_seeding_the_key()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var before = arm.Product.Export();
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Selection().ToJsonString());
            var result = await machine.InvokeAsync("supplemental", "--input", file);

            Assert.Equal(1, result.Exit);
            Assert.Equal("Partial", (string?)result.Result["supplemental"]!["state"]);
            var configuration = result.Result["supplemental"]!["configuration"]!;
            Assert.Equal("webiq", (string?)configuration["selection"]!["sources"]![0]!["kind"]);
            Assert.Equal("webiq-api-key", (string?)configuration["selection"]!["sources"]![0]!["secretName"]);
            var review = result.Result["supplemental"]!["resourceReview"];
            Assert.NotNull(review);
            var source = Assert.Single(review["sources"]!.AsArray())!;
            Assert.EndsWith("-webiq", (string)source["identityTarget"]!);
            Assert.EndsWith("-webiq", (string)source["hostTarget"]!);
            var grant = Assert.Single(source["grants"]!.AsArray())!;
            Assert.Equal((string)configuration["vault"]!["target"]! + "/secrets/webiq-api-key", (string?)grant["scope"]);
            Assert.EndsWith("/4633458b-17de-408a-b874-0445c86b69e6", (string)grant["roleDefinitionId"]!);
            Assert.Equal(2, grant["dataActions"]!.AsArray().Count);
            Assert.Empty(grant["actions"]!.AsArray());
            Assert.Equal((string?)configuration["resourceApprovalSha256"], (string?)review["sha256"]);
            Assert.Equal(64, ((string)review["sha256"]!).Length);
            Assert.Contains("operator-seeded", result.Terminal.Output);
            Assert.Equal(before, arm.Product.Export());
        }
        finally { File.Delete(file); }
    }

    internal static JsonNode Selection() => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["runtimeImage"] = FactoryTelemetryWorkflowTests.Image,
        ["audienceClientId"] = FactoryTelemetryWorkflowTests.Audience,
        ["sources"] = new JsonArray(new JsonObject
        {
            ["kind"] = "webiq", ["query"] = "Public retail accessibility research",
            ["publicQueryApproved"] = true, ["secretName"] = "webiq-api-key",
            ["secretVersion"] = new string('a', 32),
        }),
    };

    [Fact]
    public async Task Azure_only_needs_no_supplemental_configuration_or_effects()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        var before = arm.Product.Export();

        var result = await machine.InvokeAsync("supplemental");

        Assert.Equal(0, result.Exit);
        Assert.Equal("NotSelected", (string?)result.Result["supplemental"]!["state"]);
        Assert.Contains("Azure-only", result.Terminal.Output);
        Assert.Equal(before, arm.Product.Export());
        Assert.DoesNotContain("supplemental", arm.Product.Export());
    }
}
