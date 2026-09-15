using System.Text.Json;
using Dsf.Cli;
using Xunit;
using System.Text.Json.Serialization;
using Dsf.Core.Onboarding;

namespace Dsf.Cli.Tests;

public sealed class ApplicationBoundaryPlanTests
{
    [Theory]
    [InlineData("number")]
    [InlineData("object")]
    [InlineData("invalid-id")]
    [InlineData("wrong-resource-type")]
    [InlineData("child-resource")]
    [InlineData("invalid-subscription")]
    [InlineData("empty")]
    public async Task Malformed_unselected_component_workspace_link_is_unknown_before_relevance_filtering(string scenario)
    {
        var fixture = new BoundaryPlanFixture();
        var component = BoundaryPlanFixture.Root + "/resourceGroups/other/providers/Microsoft.Insights/components/other";
        fixture.Resources[component] = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
        fixture.Resources[component]["properties"]!["WorkspaceResourceId"] = scenario switch
        {
            "number" => System.Text.Json.Nodes.JsonValue.Create(42),
            "object" => new System.Text.Json.Nodes.JsonObject { ["id"] = BoundaryPlanFixture.Logs },
            "invalid-id" => System.Text.Json.Nodes.JsonValue.Create("secret-invalid-id-do-not-print"),
            "wrong-resource-type" => System.Text.Json.Nodes.JsonValue.Create(BoundaryPlanFixture.Api),
            "child-resource" => System.Text.Json.Nodes.JsonValue.Create(BoundaryPlanFixture.Logs + "/tables/AppRequests"),
            "invalid-subscription" => System.Text.Json.Nodes.JsonValue.Create(BoundaryPlanFixture.Logs.Replace(BoundaryPlanFixture.Subscription, "not-a-guid")),
            _ => System.Text.Json.Nodes.JsonValue.Create(""),
        };

        var (exit, plan, terminal) = await fixture.ReviewAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)plan["state"]);
        Assert.Contains("workspace-topology", plan["findings"]!.ToJsonString());
        Assert.Equal(2, plan["selection"]!["applicationResourceIds"]!.AsArray().Count);
        Assert.Single(plan["selection"]!["evidenceBackends"]!.AsArray());
        Assert.DoesNotContain("secret-invalid-id-do-not-print", terminal.Output + terminal.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Classic_component_absent_or_null_workspace_does_not_invent_a_backing_link(bool selected, bool explicitNull)
    {
        var fixture = new BoundaryPlanFixture();
        var component = BoundaryPlanFixture.Root + "/resourceGroups/other/providers/Microsoft.Insights/components/classic";
        fixture.Resources[component] = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
        if (explicitNull)
            fixture.Resources[component]["properties"]!["WorkspaceResourceId"] = null;
        if (selected)
            fixture.Input["applicationResourceIds"]!.AsArray().Add(component);

        var (exit, plan, _) = await fixture.ReviewAsync();

        Assert.Equal(selected ? 1 : 0, exit);
        Assert.Equal(selected ? "Unknown" : "Reviewed", (string?)plan["state"]);
        Assert.DoesNotContain("workspace-topology", plan["findings"]!.ToJsonString());
        Assert.Single(plan["selection"]!["evidenceBackends"]!.AsArray());
        if (selected)
        {
            var resource = Assert.Single(plan["azure"]!["resources"]!.AsArray(),
                r => (string?)r!["id"] == component.ToLowerInvariant());
            Assert.Null(resource!["backingWorkspaceResourceId"]);
            Assert.Contains("backing-workspace", plan["findings"]!.ToJsonString());
        }
        else
        {
            Assert.Empty(plan["azure"]!["inspectedTopology"]!.AsArray());
        }
    }

    [Fact]
    public async Task Valid_workspace_link_is_canonicalized_before_checking_unselected_component_overlap()
    {
        var fixture = new BoundaryPlanFixture();
        var component = BoundaryPlanFixture.Root + "/resourceGroups/other/providers/Microsoft.Insights/components/other";
        fixture.Resources[component] = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
        fixture.Resources[component]["properties"]!["WorkspaceResourceId"] = BoundaryPlanFixture.Logs.ToUpperInvariant() + "/";

        var (exit, plan, _) = await fixture.ReviewAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)plan["state"]);
        Assert.Contains("shared-workspace", plan["findings"]!.ToJsonString());
        Assert.Equal(BoundaryPlanFixture.Logs.ToLowerInvariant(),
            (string?)Assert.Single(plan["azure"]!["inspectedTopology"]!.AsArray())!["backingWorkspaceResourceId"]);
    }

    [Theory]
    [InlineData("acme/old-name")]
    [InlineData("previous-owner/old-name")]
    public async Task Legacy_registration_blocks_the_same_durable_repository_after_rename_or_transfer(string legacyReference)
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Input["repository"] = "acme/renamed";
        fixture.SetLegacyRegistration(legacyReference);
        fixture.Repository.Responses["repos/" + legacyReference] = fixture.Repository.Responses["repos/acme/renamed"];

        var (exit, plan, _) = await fixture.ReviewAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)plan["state"]);
        Assert.Contains("owner-admission", plan["findings"]!.ToJsonString());
        var observation = Assert.Single(plan["ownerAdmission"]!["observations"]!.AsArray(),
            o => (string?)o!["key"] == "GITHUB_REPOSITORY");
        Assert.Equal(913, (long?)observation!["currentRepository"]?["id"]);
        Assert.Contains(fixture.Repository.Invocations, args => args[^1] == "repos/" + legacyReference);
        Assert.DoesNotContain(fixture.Repository.Invocations,
            args => args[^1].StartsWith("repos/" + legacyReference + "/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unauthorized")]
    [InlineData("forbidden")]
    [InlineData("malformed-response")]
    [InlineData("invalid-id")]
    [InlineData("malformed-reference")]
    [InlineData("malformed-json-reference")]
    public async Task Unresolved_legacy_repository_identity_remains_unknown_without_adoption_or_provider_disclosure(string scenario)
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Input["repository"] = "acme/renamed";
        fixture.SetLegacyRegistration(scenario switch
        {
            "malformed-reference" => "https://secret-do-not-print@example.test/acme/old-name",
            "malformed-json-reference" => "\"acme/old-name",
            _ => "acme/old-name",
        });
        fixture.Repository.Responses["repos/acme/old-name"] = scenario switch
        {
            "unauthorized" => new(1, "HTTP/2.0 401 Unauthorized\r\n\r\n{}", "secret-do-not-print"),
            "forbidden" => new(1, "HTTP/2.0 403 Forbidden\r\n\r\n{}", "secret-do-not-print"),
            "malformed-response" => new(0, """{"id":913,"secret":"secret-do-not-print"}""", ""),
            "invalid-id" => new(0, fixture.Repository.Responses["repos/acme/renamed"].StandardOutput.Replace("\"id\":913", "\"id\":0"), ""),
            _ => new(1, "HTTP/2.0 404 Not Found\r\n\r\n{}", "secret-do-not-print"),
        };

        var (exit, plan, terminal) = await fixture.ReviewAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)plan["state"]);
        Assert.Contains("owner-admission-repository", plan["findings"]!.ToJsonString());
        Assert.Contains("do not assume unclaimed state", terminal.Output);
        Assert.Null(Assert.Single(plan["ownerAdmission"]!["observations"]!.AsArray(),
            o => (string?)o!["key"] == "GITHUB_REPOSITORY")!["currentRepository"]);
        Assert.DoesNotContain("secret-do-not-print", terminal.Output + terminal.Error);
        if (scenario is "malformed-reference" or "malformed-json-reference")
            Assert.DoesNotContain(fixture.Repository.Invocations, args => args[^1] == "repos/acme/old-name");
    }

    [Fact]
    public async Task Current_product_collision_stays_blocked_when_legacy_repository_reference_is_unreadable()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.SetLegacyRegistration("acme/unavailable", "widget");
        fixture.Repository.Responses["repos/acme/unavailable"] = new(1, "HTTP/2.0 403 Forbidden\r\n\r\n{}", "");

        var (exit, plan, _) = await fixture.ReviewAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)plan["state"]);
        Assert.Contains(plan["findings"]!.AsArray(),
            f => (string?)f!["check"] == "owner-admission" && (string?)f!["state"] == "Blocked");
    }

    [Fact]
    public async Task Reused_legacy_name_resolves_current_identity_but_cannot_prove_historical_non_overlap()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Input["repository"] = "acme/renamed";
        fixture.SetLegacyRegistration("acme/old-name");
        fixture.Repository.Responses["repos/acme/old-name"] = new(0, """
            {"id":914,"owner":{"id":81,"login":"acme"},"name":"old-name","default_branch":"main",
             "visibility":"private","archived":false,"disabled":false,"has_issues":true}
            """, "");

        var (exit, plan, terminal) = await fixture.ReviewAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)plan["state"]);
        var current = Assert.Single(plan["ownerAdmission"]!["observations"]!.AsArray(),
            o => (string?)o!["key"] == "GITHUB_REPOSITORY")!["currentRepository"]!;
        Assert.Equal(914, (long?)current["id"]);
        Assert.Contains("Historical registration identity remains unverified", terminal.Output);
        Assert.Contains("not proof of non-overlap", terminal.Output);
        var fingerprint = (string)plan["selectionFingerprint"]!;
        fixture.Repository.Responses["repos/acme/old-name"] =
            fixture.Repository.Responses["repos/acme/old-name"] with
            {
                StandardOutput = fixture.Repository.Responses["repos/acme/old-name"].StandardOutput.Replace("\"id\":914", "\"id\":915"),
            };
        var (_, changed, _) = await fixture.ReviewAsync();
        Assert.NotEqual(fingerprint, (string?)changed["selectionFingerprint"]);
    }

    [Fact]
    public async Task Legacy_DSF_tag_values_are_conflict_fingerprints_not_copied_into_preview()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Resources[BoundaryPlanFixture.Api]["tags"]!["dsf-legacy-credential"] = "secret-do-not-print";

        var (_, plan, terminal) = await fixture.PlanAsync();

        Assert.Equal("Blocked", (string?)plan["state"]);
        Assert.Contains("existing-dsf-signal", terminal.Output);
        Assert.DoesNotContain("secret-do-not-print", terminal.Output + terminal.Error);
    }

    [Fact]
    public async Task Documented_ARM_permission_response_preserves_omitted_data_actions_as_unknown_not_empty()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Overrides["https://management.azure.com" + BoundaryPlanFixture.Root + "/providers/Microsoft.Authorization/permissions?api-version=2022-04-01"] =
            new(0, """{"value":[{"actions":["*/read"],"notActions":[]}]}""", "");

        var (_, plan, _) = await fixture.PlanAsync();

        Assert.Equal("Partial", (string?)plan["state"]);
        var observation = Assert.Single(plan["azure"]!["permissions"]!.AsArray(),
            p => (string?)p!["scope"] == BoundaryPlanFixture.Root);
        Assert.Null(observation!["dataActions"]);
        Assert.Null(observation["notDataActions"]);
        Assert.Contains("not delegation", plan["findings"]!.ToJsonString());
    }

    [Fact]
    public async Task Ambiguous_duplicate_input_fields_are_rejected_without_last_value_winning()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.InputTextOverride = """{"productKey":"another-product",""" + fixture.Input.ToJsonString()[1..];

        var (exit, plan, _) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Failed", (string?)plan["state"]);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Repository.Invocations);
    }

    [Theory]
    [InlineData("duplicate-table")]
    [InlineData("duplicate-json-property")]
    [InlineData("foreign-pagination")]
    [InlineData("denied")]
    public async Task Incomplete_backend_inspection_is_unknown_not_dedicated_or_ready(string scenario)
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Overrides["https://management.azure.com" + BoundaryPlanFixture.Logs.ToLowerInvariant() + "/tables?api-version=2022-10-01"] =
            scenario switch
            {
                "duplicate-table" => new(0, """{"value":[{"name":"AppRequests","properties":{"plan":"Analytics"}},{"name":"AppRequests","properties":{"plan":"Basic"}}]}""", ""),
                "duplicate-json-property" => new(0, """{"value":[{"name":"AppRequests","properties":{"plan":"Basic","plan":"Analytics"}}]}""", ""),
                "foreign-pagination" => new(0, """{"value":[],"nextLink":"https://secret-do-not-print.example/tables"}""", ""),
                _ => new(1, "", "secret-do-not-print"),
            };

        var (exit, plan, terminal) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)plan["state"]);
        Assert.Contains("evidence-table", plan["findings"]!.ToJsonString());
        Assert.Empty(terminal.Error);
        Assert.DoesNotContain("secret-do-not-print", terminal.Output);
        Assert.DoesNotContain(fixture.Calls, c => c.Any(a => a.Contains("secret-do-not-print")));
    }

    [Fact]
    public async Task Interactive_end_of_input_cancels_without_provider_contact()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Repository.BoundaryAzureRunner = fixture;
        var terminal = new ScriptedTerminal(new(true, false, false), []);

        var exit = await fixture.Repository.InvokeCommandAsync(terminal, ["onboard", "decide", "plan"]);

        Assert.Equal(130, exit);
        Assert.Contains("\"state\": \"Canceled\"", terminal.Output);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Repository.Invocations);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Unrelated_inventory_and_unselected_tables_do_not_masquerade_as_selection_changes()
    {
        var fixture = new BoundaryPlanFixture();
        var (_, first, _) = await fixture.PlanAsync();
        var component = BoundaryPlanFixture.Root + "/resourceGroups/other/providers/Microsoft.Insights/components/other";
        fixture.Resources[component] = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
        fixture.Resources[component]["properties"]!["WorkspaceResourceId"] = BoundaryPlanFixture.Logs + "-other";
        fixture.Overrides["https://management.azure.com" + BoundaryPlanFixture.Logs.ToLowerInvariant() + "/tables?api-version=2022-10-01"] =
            new(0, """{"value":[{"name":"Unused","properties":{"plan":"Basic"}},{"name":"AppRequests","properties":{"plan":"Analytics"}}]}""", "");

        var (_, second, _) = await fixture.PlanAsync();

        Assert.Equal((string?)first["selectionFingerprint"], (string?)second["selectionFingerprint"]);
        Assert.Equal((string?)first["fingerprint"], (string?)second["fingerprint"]);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("permissions")]
    [InlineData("declaration-owner")]
    [InlineData("repository-unknown")]
    public async Task Decision_relevant_authority_or_configuration_changes_invalidate_review(string change)
    {
        var fixture = new BoundaryPlanFixture();
        var (_, draft, _) = await fixture.PlanAsync();
        fixture.Input["boundaryReview"] = JsonSerializer.SerializeToNode(new
        {
            selectionFingerprint = (string)draft["selectionFingerprint"]!, accountableOwner = "acme/platform",
            dedicated = true, crossOwnerExclusive = true,
        });
        var (_, reviewed, _) = await fixture.PlanAsync();
        if (change == "network")
            fixture.Resources[BoundaryPlanFixture.Logs]["properties"]!["publicNetworkAccessForQuery"] = "Disabled";
        else if (change == "permissions")
            fixture.Overrides["https://management.azure.com" + BoundaryPlanFixture.Root + "/providers/Microsoft.Authorization/permissions?api-version=2022-04-01"] =
                new(0, """{"value":[{"actions":["*"],"notActions":[],"dataActions":[],"notDataActions":[]}]}""", "");
        else if (change == "declaration-owner")
            fixture.Input["automationOwner"] = "acme/new-team";
        else
            fixture.Repository.Responses["repos/acme/renamed/hooks?per_page=100&page=1"] = new(1, "HTTP/2.0 403 Forbidden\r\n\r\n{}", "");

        var (exit, changed, _) = await fixture.PlanAsync("--reviewed-fingerprint", (string)reviewed["fingerprint"]!);

        Assert.Equal(1, exit);
        Assert.Contains("stale-boundary-review", changed["findings"]!.ToJsonString());
        Assert.Contains("stale-plan-review", changed["findings"]!.ToJsonString());
        Assert.NotEqual((string?)reviewed["selectionFingerprint"], (string?)changed["selectionFingerprint"]);
    }

    [Fact]
    public async Task Owner_inspection_uses_the_explicit_Azure_subscription_without_changing_the_default_account()
    {
        var fixture = new BoundaryPlanFixture();

        await fixture.PlanAsync();

        Assert.Contains(fixture.Calls, c => c[0] == "appconfig");
        Assert.All(fixture.Calls.Where(c => c[0] == "appconfig"),
            c => Assert.Contains(BoundaryPlanFixture.Subscription, c));
        Assert.DoesNotContain(fixture.Calls, c => c.Take(2).SequenceEqual(new[] { "account", "set" }));
    }

    [Theory]
    [InlineData("existing-factory-group", "Blocked", "factory-group")]
    [InlineData("model-is-storage", "Blocked", "model-prerequisite")]
    [InlineData("wrong-tenant", "Blocked", "azure-account")]
    public async Task Existing_assets_or_wrong_authority_cannot_be_reclassified_as_new_factory_or_model_prerequisites(
        string scenario, string state, string check)
    {
        var fixture = new BoundaryPlanFixture();
        if (scenario == "existing-factory-group")
            fixture.Overrides["group list"] = new(0,
                """[{"id":"/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/widget-decide","name":"widget-decide"}]""", "");
        else if (scenario == "wrong-tenant")
            fixture.Input["tenantId"] = BoundaryPlanFixture.Principal;
        else
        {
            var storage = BoundaryPlanFixture.Data + "-shared";
            fixture.Resources[storage] = BoundaryPlanFixture.Resource(storage, "Microsoft.Storage/storageAccounts");
            fixture.Resources[storage]["tags"]!["shared"] = "true";
            fixture.Input["modelResourceIds"]!.AsArray().Add(storage);
        }

        var (exit, plan, _) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        Assert.Equal(state, (string?)plan["state"]);
        Assert.Contains(check, plan["findings"]!.ToJsonString());
    }

    [Theory]
    [InlineData("missing-subscription")]
    [InlineData("cross-subscription")]
    [InlineData("resource-group")]
    [InlineData("null-declarations")]
    [InlineData("missing-declaration")]
    [InlineData("numeric-declaration")]
    [InlineData("secret-endpoint")]
    [InlineData("unknown-field")]
    public async Task Incomplete_or_invalid_explicit_inputs_fail_before_any_provider_contact(string scenario)
    {
        var fixture = new BoundaryPlanFixture();
        switch (scenario)
        {
            case "missing-subscription": fixture.Input.Remove("subscriptionId"); break;
            case "cross-subscription": fixture.Input["applicationResourceIds"]![0] = BoundaryPlanFixture.Api.Replace(BoundaryPlanFixture.Subscription, BoundaryPlanFixture.Tenant); break;
            case "resource-group": fixture.Input["applicationResourceIds"]![0] = BoundaryPlanFixture.Root + "/resourceGroups/app-a"; break;
            case "null-declarations": fixture.Input["automationDeclarations"] = null; break;
            case "missing-declaration": fixture.Input["automationDeclarations"]!.AsObject().Remove("ProposalIssue"); break;
            case "numeric-declaration": fixture.Input["automationDeclarations"]!["ProposalIssue"] = 1; break;
            case "secret-endpoint": fixture.Input["ownerAppConfigEndpoint"] = "https://secret:do-not-print@owner.azconfig.io"; break;
            default: fixture.Input["force"] = true; break;
        }

        var (exit, plan, terminal) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Failed", (string?)plan["state"]);
        Assert.NotNull(plan["nextAction"]);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Repository.Invocations);
        Assert.DoesNotContain("do-not-print", terminal.Output + terminal.Error);
    }

    [Fact]
    public async Task Interactive_selection_and_complete_explicit_replay_produce_the_same_plan()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Repository.BoundaryAzureRunner = fixture;
        var terminal = new ScriptedTerminal(new(true, false, false),
        [
            "acme/old-name", "https://owner.azconfig.io", "https://owner.vault.azure.net/", "widget",
            "1", "production", BoundaryPlanFixture.Api + "," + BoundaryPlanFixture.Data,
            BoundaryPlanFixture.Logs, "AppRequests", "widget-decide", "swedencentral", "",
            "acme/platform", "clear", "clear", "clear", "acme/platform", "dedicated", "exclusive",
        ]);

        var exit = await fixture.Repository.InvokeCommandAsync(terminal, ["onboard", "decide", "plan"]);

        Assert.Equal(0, exit);
        var json = terminal.Output.Split("[dsf] Final plan JSON:\n", StringSplitOptions.None)[1];
        var plan = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        fixture.Input.Clear();
        foreach (var field in plan["selection"]!.AsObject())
            fixture.Input.Add(field.Key, field.Value?.DeepClone());
        var (replayExit, replay, _) = await fixture.PlanAsync("--reviewed-fingerprint", (string)plan["fingerprint"]!);
        Assert.Equal(0, replayExit);
        Assert.Equal((string?)plan["fingerprint"], (string?)replay["fingerprint"]);
        Assert.Contains("not a global lock", terminal.Output);
        Assert.Contains("Preserved assets", terminal.Output);
        Assert.Contains("New factory resources", terminal.Output);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Exact_review_replays_canonically_and_selection_drift_invalidates_both_confirmations()
    {
        var fixture = new BoundaryPlanFixture();
        var (_, draft, _) = await fixture.PlanAsync();
        Assert.NotNull(draft["selectionFingerprint"]);
        fixture.Input["boundaryReview"] = JsonSerializer.SerializeToNode(new
        {
            selectionFingerprint = (string)draft["selectionFingerprint"]!, accountableOwner = "acme/platform",
            dedicated = true, crossOwnerExclusive = true,
        });
        var (reviewedExit, reviewed, _) = await fixture.PlanAsync();
        Assert.Equal(0, reviewedExit);
        Assert.Equal("Reviewed", (string?)reviewed["state"]);
        var fingerprint = (string)reviewed["fingerprint"]!;
        fixture.Input["applicationResourceIds"] = new System.Text.Json.Nodes.JsonArray(
            "  " + BoundaryPlanFixture.Data.ToUpperInvariant() + "/  ", BoundaryPlanFixture.Api, BoundaryPlanFixture.Api);
        fixture.Input["repository"] = " ACME/OLD-NAME ";
        var (replayExit, replay, _) = await fixture.PlanAsync("--reviewed-fingerprint", fingerprint);
        Assert.Equal(0, replayExit);
        Assert.Equal(fingerprint, (string?)replay["fingerprint"]);

        fixture.Input["applicationResourceIds"]!.AsArray().RemoveAt(0);
        var (changedExit, changed, _) = await fixture.PlanAsync("--reviewed-fingerprint", fingerprint);
        Assert.Equal(1, changedExit);
        Assert.Equal("Blocked", (string?)changed["state"]);
        Assert.Contains("stale-boundary-review", changed["findings"]!.ToJsonString());
        Assert.Contains("stale-plan-review", changed["findings"]!.ToJsonString());
    }

    [Fact]
    public async Task Categorized_plan_binds_exact_external_scopes_to_symbolic_factory_identities()
    {
        var fixture = new BoundaryPlanFixture();

        var (_, plan, _) = await fixture.PlanAsync();

        var categories = plan["categories"]!;
        Assert.Equal(4, categories["preservedAssets"]!.AsArray().Count);
        Assert.Contains("factory:cosmos", categories["newFactoryResources"]!.ToJsonString());
        Assert.Contains("https://owner.azconfig.io", categories["reusedPrerequisites"]!.ToJsonString());
        var grants = categories["proposedGrants"]!.AsArray();
        var external = Assert.Single(grants, g => (string?)g!["scope"] == BoundaryPlanFixture.Logs.ToLowerInvariant());
        Assert.Equal("Log Analytics Reader", (string?)external!["role"]);
        Assert.Equal("factory:source-azuremonitor", (string?)external["identity"]);
        Assert.Null(external["principalId"]);
        Assert.True((bool)external["requiresBindingRecheck"]!);
        Assert.DoesNotContain(grants, g => (string?)g!["scope"] == BoundaryPlanFixture.Root);
        Assert.Equal(5, categories["registryClaims"]!.AsArray().Count);
        Assert.Contains("unassigned", categories["allowedRepositoryAdditions"]!.ToJsonString());
        Assert.Contains("create-once", categories["allowedRepositoryAdditions"]!.ToJsonString());
        Assert.NotEmpty(categories["costsAndOmissions"]!.AsArray());
        Assert.NotEmpty(categories["blockedAdministratorPrerequisites"]!.AsArray());
        Assert.Null(plan["factoryId"]);
    }

    [Theory]
    [InlineData("unreadable", "Unknown")]
    [InlineData("matching", "Blocked")]
    [InlineData("inconsistent", "Unknown")]
    [InlineData("unrecognized", "Unknown")]
    [InlineData("inconsistent-pages", "Unknown")]
    [InlineData("partial-legacy", "Unknown")]
    [InlineData("legacy", "Blocked")]
    public async Task Owner_admission_signals_never_authorize_name_based_adoption(string scenario, string state)
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Repository.Responses["repos/acme/other"] = new(1, "HTTP/2.0 404 Not Found\r\n\r\n{}", "");
        fixture.Overrides["appconfig kv"] = scenario switch
        {
            "unreadable" => new(1, "", "secret-owner-response"),
            "matching" => new(0, """[{"key":"dsf/onboarding/products/widget","label":null,"etag":"revision-7","value":"{\"productKey\":\"widget\"}"}]""", ""),
            "inconsistent" => new(0, """[{"key":"dsf/onboarding/products/widget","label":null,"value":null}]""", ""),
            "unrecognized" => new(0, """[{"key":"dsf/onboarding/partial/operation","label":null,"etag":"revision-1","value":"{}"}]""", ""),
            "inconsistent-pages" => new(0, """[{"key":"GITHUB_REPOSITORY","label":"other","etag":"one","value":"acme/other"},{"key":"GITHUB_REPOSITORY","label":"other","etag":"two","value":"acme/renamed"}]""", ""),
            "partial-legacy" => new(0, """[{"key":"GITHUB_REPOSITORY","label":"other","etag":"one","value":"acme/other"}]""", ""),
            _ => new(0, """[{"key":"GITHUB_REPOSITORY","label":"widget","etag":"revision-7","value":"acme/old-name"}]""", ""),
        };

        var (exit, plan, terminal) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        Assert.Equal(state, (string?)plan["state"]);
        Assert.Contains("owner-admission", plan["findings"]!.ToJsonString());
        Assert.DoesNotContain("secret-owner-response", terminal.Output + terminal.Error);
        Assert.All(fixture.Calls.Where(c => c[0] == "appconfig"), c =>
        {
            Assert.Equal("login", c[c.IndexOf("--auth-mode") + 1]);
            Assert.Contains("--all", c);
        });
    }

    [Theory]
    [InlineData("environment", "environment-conflict")]
    [InlineData("legacy-tag", "existing-dsf-signal")]
    [InlineData("unsupported-backend", "evidence-route")]
    [InlineData("basic-table", "evidence-table")]
    [InlineData("unselected-backing", "backing-workspace")]
    [InlineData("other-component", "shared-workspace")]
    [InlineData("shared-host", "shared-host")]
    public async Task Known_boundary_contradictions_block_without_repair_or_implicit_membership(string scenario, string check)
    {
        var fixture = new BoundaryPlanFixture();
        var component = BoundaryPlanFixture.Root + "/resourceGroups/telemetry/providers/Microsoft.Insights/components/app";
        switch (scenario)
        {
            case "environment":
                fixture.Resources[BoundaryPlanFixture.Data]["tags"]!["environment"] = "staging";
                break;
            case "legacy-tag":
                fixture.Resources[BoundaryPlanFixture.Api]["tags"]!["dsf-product"] = "widget";
                break;
            case "unsupported-backend":
                fixture.Input["applicationResourceIds"]!.AsArray().RemoveAt(1);
                fixture.Input["evidenceBackends"]![0]!["resourceId"] = BoundaryPlanFixture.Data;
                break;
            case "basic-table":
                fixture.Overrides["https://management.azure.com" + BoundaryPlanFixture.Logs.ToLowerInvariant() + "/tables?api-version=2022-10-01"] =
                    new(0, """{"value":[{"name":"AppRequests","properties":{"plan":"Basic"}}]}""", "");
                break;
            case "shared-host":
                fixture.Resources[BoundaryPlanFixture.Data]["type"] = "Microsoft.Web/serverfarms";
                break;
            default:
                fixture.Resources[component] = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
                fixture.Resources[component]["properties"]!["WorkspaceResourceId"] = scenario == "unselected-backing"
                    ? BoundaryPlanFixture.Logs + "-other" : BoundaryPlanFixture.Logs;
                if (scenario == "unselected-backing") fixture.Input["applicationResourceIds"]!.AsArray().Add(component);
                break;
        }

        var (exit, plan, _) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)plan["state"]);
        Assert.Contains(check, plan["findings"]!.ToJsonString());
        Assert.Single(plan["selection"]!["evidenceBackends"]!.AsArray());
    }

    [Fact]
    public async Task Shared_storage_is_blocked_even_when_component_metadata_and_operator_declaration_claim_dedication()
    {
        var fixture = new BoundaryPlanFixture();
        fixture.Resources[BoundaryPlanFixture.Logs]["tags"]!["shared"] = "true";
        var component = BoundaryPlanFixture.Root + "/resourceGroups/app-a/providers/Microsoft.Insights/components/telemetry";
        fixture.Resources[component] = BoundaryPlanFixture.Resource(component, "Microsoft.Insights/components");
        fixture.Resources[component]["properties"]!["WorkspaceResourceId"] = BoundaryPlanFixture.Logs;
        fixture.Input["applicationResourceIds"]!.AsArray().Add(component);
        fixture.Input["boundaryReview"] = JsonSerializer.SerializeToNode(new
        {
            selectionFingerprint = "claimed-current", accountableOwner = "acme/platform",
            dedicated = true, crossOwnerExclusive = true,
        });

        var (exit, plan, _) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)plan["state"]);
        Assert.Contains("shared", plan["findings"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, plan["selection"]!["applicationResourceIds"]!.AsArray().Count);
        Assert.Single(plan["selection"]!["evidenceBackends"]!.AsArray());
        Assert.DoesNotContain("tag write", plan["findings"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plan_discovers_operator_and_selected_backend_without_claiming_delegation_or_workload_access()
    {
        var fixture = new BoundaryPlanFixture();

        var (_, plan, _) = await fixture.PlanAsync();

        Assert.Equal(BoundaryPlanFixture.Principal, (string?)plan["azure"]?["operatorPrincipalId"]);
        Assert.Equal(3, plan["azure"]!["resources"]!.AsArray().Count);
        Assert.Equal("44444444-4444-4444-4444-444444444444",
            (string?)plan["azure"]!["resources"]!.AsArray().Single(r => (string?)r!["type"] == "microsoft.operationalinsights/workspaces")!["workspaceId"]);
        Assert.Contains("binding", plan["findings"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", plan.ToJsonString());
        Assert.All(fixture.Calls.Where(args => args[0] is "resource" or "group"),
            args => Assert.Contains(BoundaryPlanFixture.Subscription, args));
    }

    [Fact]
    public async Task Explicit_plan_preserves_exact_multi_group_selection_without_writes()
    {
        var fixture = new BoundaryPlanFixture();

        var (exit, _, terminal) = await fixture.PlanAsync();

        Assert.Equal(1, exit);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        var plan = JsonSerializer.Deserialize<ApplicationBoundaryPlan>(terminal.Output, options)!;
        var roundTrip = JsonSerializer.Deserialize<ApplicationBoundaryPlan>(JsonSerializer.Serialize(plan, options), options)!;
        Assert.Equal(BoundaryPlanState.Partial, roundTrip.State);
        Assert.Equal(new[] { BoundaryPlanFixture.Api.ToLowerInvariant(), BoundaryPlanFixture.Data.ToLowerInvariant() },
            roundTrip.Selection.ApplicationResourceIds);
        Assert.Equal(BoundaryPlanFixture.Logs.ToLowerInvariant(), Assert.Single(roundTrip.Selection.EvidenceBackends).ResourceId);
        Assert.Equal(913, roundTrip.RepositoryReview.Repository!.Id);
        Assert.False(roundTrip.HasRemoteEffects);
        fixture.AssertReadOnly();
    }
}
