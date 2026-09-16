using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.Runtime;
using Dsf.Runtime.Tests;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryCharterWorkflowTests
{
    [Fact]
    public async Task Unchanged_onboarding_charter_with_CRLF_is_adopted_with_its_original_native_identity()
    {
        var content = CharterScenario.ExistingCharter.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.NotEqual(CharterMarkdown.GitBlobSha(CharterScenario.ExistingCharter), CharterMarkdown.GitBlobSha(content));
        await AssertExactUnchangedAdoptionAsync(content);
    }

    [Fact]
    public async Task Unchanged_legacy_charter_with_multiline_prose_is_adopted_without_rewriting_human_intent()
    {
        const string content = """
            <!-- dsf:charter schema_version=1 -->
            # Product Charter: widget

            ## Vision
            Help coordinators coordinate volunteer shifts.
            Keep shift planning understandable for volunteer teams.

            ## Target Users
            Volunteer coordinators arranging staffed shifts.
            Team leads responsible for the staffing schedule.

            ## Goals
            - Ensure scheduling prevents double booking.

            ## Non-Goals

            ## Success Metrics
            - Measure weekly reductions in double-booked volunteer shifts.

            ## Constraints
            Existing volunteer schedules remain human-owned.

            ## Glossary
            - Coordinator: person responsible for a volunteer schedule
            """;
        Assert.Contains('\n', CharterMarkdown.Parse(content, "widget").Vision);
        await AssertExactUnchangedAdoptionAsync(content);
    }

    private static async Task AssertExactUnchangedAdoptionAsync(string content)
    {
        using var scenario = await CharterScenario.ReadyAsync(content);
        var nativeBlob = scenario.Source.Source!.Blobs.Single(blob => blob.Value.SequenceEqual(Encoding.UTF8.GetBytes(content))).Key;
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.True(review.Exit == 0, review.Terminal.Output);
        Assert.Equal(content, (string?)review.Result["review"]?["before"]);
        Assert.Equal(content, (string?)review.Result["review"]?["after"]);
        Assert.Equal(nativeBlob, (string?)review.Result["review"]?["beforeBlob"]);
        Assert.Equal(nativeBlob, (string?)review.Result["review"]?["afterBlob"]);
        Assert.Equal("DraftReady", (string?)review.Result["evidence"]?["analysis"]?["state"]);
        var approval = (string)review.Result["revision"]!;
        var adopted = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", approval);
        Assert.True(adopted.Exit == 0, adopted.Terminal.Output);
        Assert.Equal("Adopted", (string?)adopted.Result["state"]);
        Assert.Equal(nativeBlob, (string?)adopted.Result["adoption"]?["charterBlob"]);
        Assert.Equal(FactoryModelsValidation.Hash(content), (string?)adopted.Result["adoption"]?["charterSha256"]);
        Assert.Equal(approval, (string?)adopted.Result["adoption"]?["reviewFingerprint"]);
        Assert.Null(adopted.Result["publication"]);
        Assert.False((bool)adopted.Result["activated"]!);
        scenario.Reconstruct();
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.True(status.Exit == 0, status.Terminal.Output);
        Assert.Equal("Adopted", (string?)status.Result["state"]);
        Assert.Equal(content, (string?)status.Result["evidence"]?["source"]?["content"]);
        Assert.Equal(nativeBlob, (string?)status.Result["adoption"]?["charterBlob"]);
        Assert.Equal(adopted.Result["adoption"]!["revision"]!.ToJsonString(), status.Result["adoption"]!["revision"]!.ToJsonString());
        Assert.Equal(content, scenario.CurrentCharter);
        Assert.Empty(scenario.GitHubWrites);
    }

    [Theory]
    [InlineData(520, "Reviewed")]
    [InlineData(8220, "Reviewed")]
    [InlineData(8221, "Partial")]
    public async Task Private_charter_audit_has_a_separate_bounded_budget_not_the_source_governance_key_budget(int records, string state)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var persisted = JsonNode.Parse(scenario.Arm.Product.Export())!.AsObject();
        var sample = JsonNode.Parse((string)persisted.First().Value!)!;
        for (var i = 0; i < records; i++)
        {
            var key = "dsf/factory/charter/publications/audit/payload/" + i.ToString("D4");
            var record = sample.DeepClone();
            record["key"] = key;
            record["value"] = "\"private publication analysis\"";
            persisted[key] = record.ToJsonString();
        }
        scenario.Arm.Product = ReservationTestStore.Restore(persisted.ToJsonString());
        var before = scenario.Arm.Product.Export();
        var result = await scenario.Machine.InvokeAsync("charter");
        Assert.Equal(state, (string?)result.Result["state"]);
        Assert.Equal(before, scenario.Arm.Product.Export());
        Assert.Empty(scenario.GitHubWrites);
    }

    [Fact]
    public async Task Cancellation_after_native_branch_creation_is_not_rollback_or_permission_to_recreate()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        using var cancellation = new CancellationTokenSource();
        scenario.AfterEffect = _ => cancellation.Cancel();
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        var exit = await scenario.Machine.Boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", "charter", "--publish", "--approve", (string)review.Result["revision"]!,
                "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget", "--tenant", BoundaryPlanFixture.Tenant,
                "--subscription", BoundaryPlanFixture.Subscription, "--json"], cancellation.Token);
        Assert.Equal(130, exit);
        Assert.Equal("Canceled", (string?)JsonNode.Parse(terminal.Output)!["state"]);
        scenario.Reconstruct();
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Unknown", (string?)status.Result["state"]);
        Assert.Equal("Dispatched", (string?)status.Result["publication"]?["effects"]?[0]?["state"]);
        Assert.Equal("Unknown", (string?)(await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!)).Result["state"]);
        Assert.Single(scenario.GitHubWrites);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("revoked")]
    public async Task A_last_good_adoption_is_only_history_when_current_charter_or_access_is_invalid(string change)
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        var adopted = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)review.Result["revision"]!);
        Assert.Equal(0, adopted.Exit);
        var source = scenario.Source.Source!;
        if (change == "revoked") source.GitHubStatus = 403;
        else
        {
            source.Head = new string('e', 40);
            source.RootTree = change == "missing" ? source.Tree(("README.md", "100644", source.Blob(IntentModelFixture.SourceText)))
                : source.Tree((".dsf", "040000", source.Tree(("charter.md", "100644", source.Blob("invalid current charter")))),
                    ("README.md", "100644", source.Blob(IntentModelFixture.SourceText)));
        }
        var result = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Null(result.Result["adoption"]);
        Assert.Equal(adopted.Result["adoption"]!["revision"]!.ToJsonString(), result.Result["historicalAdoption"]!["revision"]!.ToJsonString());
        Assert.Empty(scenario.GitHubWrites);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("analysis")]
    public async Task Authority_changed_during_current_charter_fetch_withholds_the_review(string change)
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var source = scenario.Source.Source!;
        source.AfterBlobResponse = () =>
        {
            if (change == "analysis") source.Progress.Remove("intent/current");
            else
            {
                var manifest = JsonSerializer.Deserialize<FactoryManifest>(source.Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
                var models = FactoryModelsValidation.ReadPreparation(manifest, source.Values);
                var grant = models.Grants!.Single(g => g.Scope == models.Routes![0].Selection.AccountResourceId);
                scenario.Models.Resources.Remove(grant.Target);
            }
        };
        var result = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.Equal(change == "analysis" ? "Unknown" : "Blocked", (string?)result.Result["state"]);
        Assert.Null(result.Result["review"]);
        Assert.Empty(scenario.GitHubWrites);
    }

    [Fact]
    public async Task Charter_review_host_rejects_code_identity_and_keeps_operator_route_read_only()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var source = scenario.Source.Source!;
        using var workload = new FactoryIntentCommandTests.IntentCliWorkloadTransport(source, scenario.Models);
        using var http = new HttpClient(workload);
        var runner = new SourceHostRunner(source) { Route = "/charter-review", Request = new JsonObject(), CodeCaller = true };
        var persisted = JsonSerializer.Serialize(source.Progress);
        var result = await RuntimeCliApplication.InvokeAsync(["serve-source"], source.Environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, SourceHttp = http,
                SourceCredential = new SourceTestCredential(source), SourceClock = source.Clock }, CancellationToken.None);
        Assert.Equal(0, result);
        Assert.Equal("Blocked", (string?)JsonNode.Parse(runner.Body)!["state"]);
        Assert.Equal(persisted, JsonSerializer.Serialize(source.Progress));
        Assert.Empty(scenario.GitHubWrites);
    }

    [Fact]
    public async Task Missing_publication_root_with_surviving_receipts_never_creates_another_intent()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        scenario.LoseEffect = 3;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        scenario.Reconstruct();
        scenario.Arm.Product.Remove("dsf/factory/charter/publication");
        scenario.Arm.Product.Remove("dsf/factory/charter/publications/initialized");
        var persisted = scenario.Arm.Product.Export();
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Unknown", (string?)status.Result["state"]);
        var retry = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.Equal("Unknown", (string?)retry.Result["state"]);
        Assert.Equal(persisted, scenario.Arm.Product.Export());
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_adoption_coordinator_cannot_recreate_or_reauthorize_surviving_adoption(bool removePointer)
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)review.Result["revision"]!)).Exit);
        scenario.Reconstruct();
        scenario.Arm.Product.Remove("dsf/factory/charter/adoption-operation");
        if (removePointer) scenario.Arm.Product.Remove("dsf/factory/charter/adopted");
        var persisted = scenario.Arm.Product.Export();
        var result = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal(persisted, scenario.Arm.Product.Export());
        Assert.Equal("Unknown", (string?)(await scenario.Machine.InvokeAsync("charter", "--status-only")).Result["state"]);
    }

    [Fact]
    public async Task Changed_model_record_after_host_review_withholds_the_draft()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        scenario.Source.AfterHost = (path, _) =>
        {
            if (path == "/charter-review")
                scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryModels, value => value);
        };
        var result = await scenario.Machine.InvokeAsync("charter");
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Null(result.Result["review"]);
        Assert.Empty(scenario.GitHubWrites);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("state")]
    public async Task Confirmed_publication_receipts_must_match_the_original_native_requests(string damage)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!)).Exit);
        scenario.Reconstruct();
        scenario.Arm.Product.Change("dsf/factory/charter/publication", text =>
        {
            var record = JsonNode.Parse(text)!;
            if (damage == "request") record["effects"]![1]!["requestSha256"] = new string('0', 64);
            else record["state"] = 999;
            return record.ToJsonString();
        });
        var result = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }

    [Fact]
    public async Task Existing_route_cannot_bypass_human_merge_review_for_a_new_charter()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        scenario.HumanMerge();
        scenario.Pull!["merged_by"]!["type"] = "Bot";
        var result = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Null(result.Result["adoption"]);
    }
    [Fact]
    public async Task A_later_amendment_needs_its_own_approval_and_preserves_original_publication_receipts()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var first = await scenario.Machine.InvokeAsync("charter");
        var published = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)first.Result["revision"]!);
        scenario.HumanMerge();
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--adopt", "--approve", (string)review.Result["revision"]!)).Exit);
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("source", "--new-snapshot")).Exit);
        scenario.Source.WorkloadTransport = transport => new FactoryIntentCommandTests.IntentCliWorkloadTransport(transport, scenario.Models, input =>
        {
            if (input.Contains("CHECK_EVERY_CLAIM", StringComparison.Ordinal) && input.Contains("\"path\":\".dsf/charter.md\"", StringComparison.Ordinal))
                return """{"complete":true,"checks":[{"claim":0,"verdict":"Neutral","explanation":"Charter records intent rather than independent implementation evidence.","excerpt":""},{"claim":1,"verdict":"Neutral","explanation":"Charter records intent rather than independent implementation evidence.","excerpt":""},{"claim":2,"verdict":"Neutral","explanation":"Charter records intent rather than independent implementation evidence.","excerpt":""}],"limitations":[]}""";
            return IntentModelFixture.Complete(input).Replace("\"pathIndex\":1", "\"pathIndex\":2", StringComparison.Ordinal)
                .Replace("Coordinate volunteer shifts without double booking.", "Support volunteer shift assignment without double booking.", StringComparison.Ordinal);
        });
        var analyzed = await scenario.Machine.InvokeAsync("infer", "--new-analysis");
        for (var i = 0; i < 5 && analyzed.Exit != 0; i++) analyzed = await scenario.Machine.InvokeAsync("infer");
        Assert.True(analyzed.Exit == 0, analyzed.Terminal.Output);
        var next = await scenario.Machine.InvokeAsync("charter");
        var stale = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)first.Result["revision"]!);
        Assert.Equal("Blocked", (string?)stale.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
        var second = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)next.Result["revision"]!);
        Assert.True(second.Exit == 0, second.Terminal.Output);
        Assert.Equal(6, scenario.GitHubWrites.Count);
        Assert.Equal(2, (int?)second.Result["publication"]?["sequence"]);
        Assert.Equal(published.Result["publication"]!["operation"]!.ToJsonString(), second.Result["publication"]!["previousOperation"]!.ToJsonString());
    }
    [Fact]
    public async Task Remote_mutations_target_immutable_repository_and_ref_IDs_not_reusable_repository_names()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var reviewed = await scenario.Machine.InvokeAsync("charter");
        var result = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)reviewed.Result["revision"]!);
        Assert.Equal(0, result.Exit);
        Assert.All(scenario.GitHubWrites, call => Assert.Equal("graphql", call[^1]));
    }
    [Fact]
    public async Task Material_code_drift_requires_complete_current_analysis_then_allows_the_same_human_merged_charter()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        scenario.HumanMerge();
        var source = scenario.Source.Source!;
        source.Head = new string('e', 40);
        source.RootTree = source.Tree((".dsf", "040000", source.Tree(("charter.md", "100644", source.Blob(scenario.CurrentCharter!)))),
            ("README.md", "100644", source.Blob(IntentModelFixture.SourceText + "\nThe implementation validates shift dates.")));
        Assert.Equal("Blocked", (string?)(await scenario.Machine.InvokeAsync("charter", "--adopt")).Result["state"]);
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("source", "--new-snapshot")).Exit);
        scenario.Source.WorkloadTransport = transport => new FactoryIntentCommandTests.IntentCliWorkloadTransport(transport, scenario.Models, input =>
        {
            if (input.Contains("CHECK_EVERY_CLAIM", StringComparison.Ordinal) && input.Contains("\"path\":\".dsf/charter.md\"", StringComparison.Ordinal))
                return """{"complete":true,"checks":[{"claim":0,"verdict":"Neutral","explanation":"Charter is explicit intent rather than independent implementation evidence.","excerpt":""},{"claim":1,"verdict":"Neutral","explanation":"Charter is explicit intent rather than independent implementation evidence.","excerpt":""},{"claim":2,"verdict":"Neutral","explanation":"Charter is explicit intent rather than independent implementation evidence.","excerpt":""}],"limitations":[]}""";
            return IntentModelFixture.Complete(input).Replace("\"pathIndex\":1", "\"pathIndex\":2", StringComparison.Ordinal);
        });
        var analysis = await scenario.Machine.InvokeAsync("infer", "--new-analysis");
        for (var i = 0; i < 5 && analysis.Exit != 0; i++) analysis = await scenario.Machine.InvokeAsync("infer");
        Assert.True(analysis.Exit == 0, analysis.Terminal.Output);
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt");
        Assert.True(review.Exit == 0, review.Terminal.Output);
        var adopted = await scenario.Machine.InvokeAsync("charter", "--adopt", "--approve", (string)review.Result["revision"]!);
        Assert.True(adopted.Exit == 0, adopted.Terminal.Output);
        Assert.Equal(analysis.Result["progress"]!["revision"]!.ToJsonString(), adopted.Result["adoption"]!["analysisRevision"]!.ToJsonString());
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }

    [Theory]
    [InlineData("--publish")]
    [InlineData("--adopt")]
    [InlineData("--status-only")]
    public async Task Redirected_inputs_do_not_prompt_or_contact_any_provider_when_incomplete(string action)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        Assert.Equal(1, await CliApplication.InvokeAsync(["onboard", "decide", "charter", action, "--json"], CancellationToken.None, terminal));
        Assert.Contains("Blocked", terminal.Output);
        Assert.Empty(terminal.Prompts);
    }

    [Fact]
    public async Task Interactive_charter_approval_is_current_and_prints_complete_explicit_replay()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var reviewed = await scenario.Machine.InvokeAsync("charter");
        var terminal = new ScriptedTerminal(new(true, false, false), ["https://owner.azconfig.io", "widget",
            BoundaryPlanFixture.Tenant, BoundaryPlanFixture.Subscription, (string)reviewed.Result["revision"]!]);
        var result = await scenario.Machine.Boundary.Repository.InvokeCommandAsync(terminal, ["onboard", "decide", "charter", "--publish"]);
        Assert.Equal(0, result);
        Assert.Equal(5, terminal.Prompts.Count);
        Assert.Contains("dsf onboard decide charter --publish", terminal.Output);
        Assert.Contains("--approve", terminal.Output);
        Assert.Contains("--subscription " + BoundaryPlanFixture.Subscription, terminal.Output);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }
    [Fact]
    public async Task Proposed_amendment_preserves_governing_intent_and_reports_the_pending_PR_separately()
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var existing = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        var adopted = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)existing.Result["revision"]!);
        var draft = await scenario.Machine.InvokeAsync("charter");
        var published = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)draft.Result["revision"]!);
        Assert.True(published.Exit == 0, published.Terminal.Output);
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Adopted", (string?)status.Result["state"]);
        Assert.Equal(adopted.Result["adoption"]!["revision"]!.ToJsonString(), status.Result["adoption"]!["revision"]!.ToJsonString());
        Assert.Equal("Published", (string?)status.Result["publication"]?["state"]);
        Assert.Equal(CharterScenario.ExistingCharter, scenario.CurrentCharter);
    }

    [Fact]
    public async Task Retained_adoption_cannot_report_an_uncertain_amendment_retry_as_successful()
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var existing = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)existing.Result["revision"]!);
        var draft = await scenario.Machine.InvokeAsync("charter");
        scenario.LoseEffect = 3;
        Assert.Equal("Unknown", (string?)(await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)draft.Result["revision"]!)).Result["state"]);
        var retry = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)draft.Result["revision"]!);
        Assert.Equal("Unknown", (string?)retry.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("human-branch")]
    [InlineData("boundary")]
    [InlineData("permissions")]
    [InlineData("wrong-commit")]
    public async Task Each_relevant_write_rechecks_native_scope_and_does_not_overwrite_human_work(string change)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        if (change == "permissions") scenario.CanPush = false;
        if (change == "wrong-commit") scenario.WrongCommitFiles = true;
        if (change == "rejected") scenario.RejectEffect = 2;
        scenario.AfterEffect = count =>
        {
            if (count == 2 && change == "human-branch") scenario.HumanBranchEdit = true;
            if (count == 2 && change == "boundary") scenario.Machine.Boundary.Repository.SetWorkflow("""
                on: [push, pull_request]
                jobs:
                  delivery:
                    runs-on: ubuntu-latest
                    steps:
                      - run: npm publish
                """);
        };
        var result = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.NotEqual(0, result.Exit);
        Assert.Equal(change == "rejected" ? "Failed" : "Blocked", (string?)result.Result["state"]);
        Assert.Equal(change == "permissions" ? 0 : 2, scenario.GitHubWrites.Count);
        Assert.Null(scenario.Pull);
        Assert.Null(scenario.CurrentCharter);
    }
    [Fact]
    public async Task Unchanged_existing_charter_with_unknown_optional_fields_adopts_without_an_empty_PR()
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var reviewed = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        Assert.True(reviewed.Exit == 0, reviewed.Terminal.Output);
        Assert.Equal(CharterScenario.ExistingCharter, (string?)reviewed.Result["review"]?["after"]);
        var result = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)reviewed.Result["revision"]!);
        Assert.True(result.Exit == 0, result.Terminal.Output);
        Assert.Equal("UnchangedExisting", (string?)result.Result["adoption"]?["route"]);
        Assert.Empty(scenario.GitHubWrites);
        scenario.Reconstruct();
        Assert.Equal("Adopted", (string?)(await scenario.Machine.InvokeAsync("charter", "--status-only")).Result["state"]);
        var retry = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)reviewed.Result["revision"]!);
        Assert.Equal(result.Result["adoption"]!["revision"]!.ToJsonString(), retry.Result["adoption"]!["revision"]!.ToJsonString());
    }

    [Fact]
    public async Task Missing_staged_adoption_evidence_is_unknown_not_live_authority()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!)).Exit);
        scenario.HumanMerge();
        var review = await scenario.Machine.InvokeAsync("charter", "--adopt");
        var adopted = await scenario.Machine.InvokeAsync("charter", "--adopt", "--approve", (string)review.Result["revision"]!);
        Assert.Equal(0, adopted.Exit);
        scenario.Reconstruct();
        var key = "dsf/factory/charter/adoptions/" + ((Guid)adopted.Result["adoption"]!["revision"]!).ToString("N") + "/payload/0000";
        scenario.Arm.Product.Remove(key);
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Unknown", (string?)status.Result["state"]);
        Assert.Null(status.Result["adoption"]);
    }

    [Fact]
    public async Task Uncertain_adoption_staging_keeps_one_intent_and_never_mints_unbounded_replacement_revisions()
    {
        using var scenario = await CharterScenario.ReadyAsync(CharterScenario.ExistingCharter);
        var reviewed = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing");
        scenario.Arm.Product.LoseResponseWhen = (key, _) => key.Contains("/adoptions/", StringComparison.Ordinal) && key.EndsWith("/payload/0000", StringComparison.Ordinal);
        var result = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)reviewed.Result["revision"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        var writes = scenario.Arm.Product.Writes.Count;
        var again = await scenario.Machine.InvokeAsync("charter", "--adopt", "--existing", "--approve", (string)reviewed.Result["revision"]!);
        Assert.Equal("Unknown", (string?)again.Result["state"]);
        Assert.Equal(writes, scenario.Arm.Product.Writes.Count);
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Unknown", (string?)status.Result["state"]);
    }

    [Fact]
    public async Task Independent_operators_racing_publication_have_one_conditional_winner()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        scenario.Arm.Product.BeforePut = async (key, create) =>
        {
            if (key == "dsf/factory/charter/publications/initialized" && create)
            {
                if (Interlocked.Increment(ref arrivals) == 2) reached.SetResult();
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        var results = await Task.WhenAll(
            scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!),
            scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!));
        Assert.Single(results, r => (string?)r.Result["state"] == "Published");
        Assert.Single(results, r => r.Exit == 1);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("unread")]
    [InlineData("human-edit")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("branch")]
    [InlineData("bot")]
    public async Task Current_source_and_native_human_merge_requirements_cannot_be_bypassed(string change)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        scenario.HumanMerge();
        var source = scenario.Source.Source!;
        if (change == "code") source.RootTree = source.Tree(("README.md", "100644", source.Blob("A materially different application.")));
        if (change == "unread") source.Progress.Remove(source.Progress.Keys.Single(k => k.Contains("/chunks/", StringComparison.Ordinal)));
        if (change == "missing") source.RootTree = source.Tree(("README.md", "100644", source.Blob(IntentModelFixture.SourceText)));
        if (change is "human-edit" or "invalid")
        {
            var text = change == "invalid" ? "not a charter" : scenario.CurrentCharter!.Replace("Coordinate", "Help organize", StringComparison.Ordinal);
            source.RootTree = source.Tree((".dsf", "040000", source.Tree(("charter.md", "100644", source.Blob(text)))),
                ("README.md", "100644", source.Blob(IntentModelFixture.SourceText)));
            scenario.CurrentCharter = text;
        }
        if (change == "branch") source.Branch = "new-default";
        if (change == "bot") scenario.Pull!["merged_by"]!["type"] = "Bot";
        var result = await scenario.Machine.InvokeAsync("charter", "--adopt", "--approve", (string)preview.Result["revision"]!);
        Assert.Equal(1, result.Exit);
        Assert.Null(result.Result["adoption"]);
        Assert.Equal(change == "unread" ? "Unknown" : "Blocked", (string?)result.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }
    [Fact]
    public async Task Publication_history_does_not_hide_a_current_revoked_source_blocker()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        Assert.Equal(0, (await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!)).Exit);
        scenario.Source.Source!.GitHubStatus = 403;
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Blocked", (string?)status.Result["state"]);
        Assert.NotNull(status.Result["publication"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Ambiguous_remote_effects_survive_serialized_worker_reconstruction_without_recreation(int effect)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        scenario.LoseEffect = effect;
        var result = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        var operation = result.Result["publication"]!["operation"]!.ToJsonString();
        scenario.Reconstruct();
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Unknown", (string?)status.Result["state"]);
        Assert.Equal(operation, status.Result["publication"]!["operation"]!.ToJsonString());
        var retry = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.Equal("Unknown", (string?)retry.Result["state"]);
        Assert.Equal(effect, scenario.GitHubWrites.Count);
    }

    [Fact]
    public async Task A_confirmed_intent_not_yet_dispatched_resumes_the_same_effect_instead_of_skipping_it()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.LoseResponseWhen = (key, value) => key == "dsf/factory/charter/publication"
            && (string?)JsonNode.Parse(value)!["effects"]?.AsArray().LastOrDefault()?["state"] == "Intended";
        var held = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.Equal("Unknown", (string?)held.Result["state"]);
        Assert.Empty(scenario.GitHubWrites);
        scenario.Reconstruct();
        var resumed = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.True(resumed.Exit == 0, resumed.Terminal.Output);
        Assert.Equal("Published", (string?)resumed.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
    }
    [Fact]
    public async Task Human_charter_only_merge_is_reviewed_and_adopted_without_an_endless_new_analysis_PR_loop()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        var analysis = (string)preview.Result["review"]!["analysisRevision"]!;
        var published = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.Equal(0, published.Exit);
        var pending = await scenario.Machine.InvokeAsync("charter", "--adopt");
        Assert.Equal("Blocked", (string?)pending.Result["state"]);
        scenario.HumanMerge();
        var merged = await scenario.Machine.InvokeAsync("charter", "--adopt");
        Assert.True(merged.Exit == 0, merged.Terminal.Output + merged.Terminal.Error);
        Assert.Equal("Reviewed", (string?)merged.Result["state"]);
        Assert.Equal(analysis, (string?)merged.Result["review"]?["analysisRevision"]);
        Assert.Equal(scenario.Source.Source!.Head, (string?)merged.Result["review"]?["commit"]);
        var adopted = await scenario.Machine.InvokeAsync("charter", "--adopt", "--approve", (string)merged.Result["revision"]!);
        Assert.True(adopted.Exit == 0, adopted.Terminal.Output + adopted.Terminal.Error);
        Assert.Equal("Adopted", (string?)adopted.Result["state"]);
        Assert.Equal(analysis, (string?)adopted.Result["adoption"]?["analysisRevision"]);
        Assert.Equal("HumanMerged", (string?)adopted.Result["adoption"]?["route"]);
        Assert.False((bool)adopted.Result["activated"]!);
        Assert.Equal(3, scenario.GitHubWrites.Count);
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Adopted", (string?)status.Result["state"]);
        Assert.NotNull(status.Result["adoption"]);
    }

    [Fact]
    public async Task Explicit_approval_creates_only_a_charter_branch_and_unmerged_PR_with_durable_native_receipts()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var preview = await scenario.Machine.InvokeAsync("charter");
        var result = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
        Assert.Equal("Published", (string?)result.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
        Assert.Equal("refs/heads/" + scenario.Branch, (string?)result.Result["publication"]?["branch"]);
        Assert.Equal(71, (int?)result.Result["publication"]?["pullRequest"]?["number"]);
        Assert.False((bool)result.Result["activated"]!);
        var status = await scenario.Machine.InvokeAsync("charter", "--status-only");
        Assert.Equal("Published", (string?)status.Result["state"]);
        var repeat = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)preview.Result["revision"]!);
        Assert.Equal("Published", (string?)repeat.Result["state"]);
        Assert.Equal(3, scenario.GitHubWrites.Count);
        Assert.All(scenario.GitHubWrites, args => Assert.DoesNotContain("PATCH", args));
        Assert.Equal("Inactive", (string?)scenario.Source.Record(ProductConfigurationKeys.FactoryManifest)["publication"]);
        Assert.All(scenario.Arm.Product.Writes.Where(write => write.Key.StartsWith("dsf/factory/charter/", StringComparison.Ordinal)), write =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(write.Key + write.Value + "application/vnd.dsf.reservation+json;version=1") + 1024, 1, 10 * 1024);
            Assert.True(write.IfNoneMatch == "*" || write.IfMatch is not null);
        });
    }

    [Fact]
    public async Task Exact_private_draft_preview_reaches_real_authenticated_hosts_without_writes_or_activation()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var owner = scenario.Machine.Store.Export();
        var product = scenario.Arm.Product.Export();
        var writes = scenario.Source.Source!.Writes.Count;
        var result = await scenario.Machine.InvokeAsync("charter");
        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
        Assert.Equal("Reviewed", (string?)result.Result["state"]);
        Assert.Equal(".dsf/charter.md", (string?)result.Result["review"]?["path"]);
        Assert.Contains("Coordinate volunteer shifts", (string?)result.Result["review"]?["after"]);
        Assert.Null(result.Result["review"]?["before"]);
        Assert.False((bool)result.Result["activated"]!);
        Assert.Equal(owner, scenario.Machine.Store.Export());
        Assert.Equal(product, scenario.Arm.Product.Export());
        Assert.Equal(writes, scenario.Source.Source.Writes.Count);
        Assert.Empty(scenario.GitHubWrites);
    }
}

internal sealed class CharterScenario : IDisposable
{
    internal const string ExistingCharter = """
        <!-- dsf:charter profile=grounded-onboarding-v1 -->

        # Product charter

        ## Purpose
        Help coordinators coordinate volunteer shifts.

        ## Intended Users
        Volunteer coordinators arranging staffed shifts.

        ## Goals
        - Ensure scheduling prevents double booking.

        ## Non-Goals
        Unknown: No authoritative boundaries have been recorded.

        ## Success Measures
        Unknown: No authoritative measured targets are recorded.

        """;
    internal ReservationMachine Machine = null!;
    internal FactoryFoundationTests.FoundationTransport Arm = null!;
    internal FactoryGitHubAuthorityWorkflowTests.AuthorityWorkflowTransport Previous = null!;
    internal SourceWorkflowTransport Source = null!;
    internal ModelsWorkflowTransport Models = null!;
    internal readonly List<IReadOnlyList<string>> GitHubWrites = [];
    internal string? Branch;
    internal string? BranchHead;
    internal string? ProposalTree;
    internal string ProposalCommit = new('b', 40);
    internal string? ProposalContent;
    internal JsonNode? Pull;
    internal int NextPullNumber = 71;
    internal string? CurrentCharter;
    internal int? LoseEffect;
    internal int? RejectEffect;
    internal Action<int>? AfterEffect;
    internal bool HumanBranchEdit;
    internal bool CanPush = true;
    internal bool WrongCommitFiles;

    internal void Reconstruct()
    {
        Arm.Product = ReservationTestStore.Restore(Arm.Product.Export());
        var previous = Source.Source!;
        var next = new SourceInventoryTransport(Source.Values(), previous.Clock.Now)
            { RootTree = previous.RootTree, Head = previous.Head, Branch = previous.Branch };
        next.ImportProgress(previous);
        foreach (var (key, value) in previous.Trees) next.Trees[key] = value.DeepClone();
        foreach (var (key, value) in previous.Blobs) next.Blobs[key] = value.ToArray();
        Source.Source = next;
        previous.Dispose();
    }

    internal void HumanMerge()
    {
        var source = Source.Source!;
        source.Head = new string('c', 40);
        source.RootTree = ProposalTree!;
        CurrentCharter = ProposalContent;
        Pull!["state"] = "closed";
        Pull["merged"] = true;
        Pull["merged_at"] = source.Clock.Now;
        Pull["merge_commit_sha"] = source.Head;
        Pull["merged_by"] = new JsonObject { ["id"] = 56, ["login"] = "human-maintainer", ["type"] = "User" };
    }

    internal static async Task<CharterScenario> ReadyAsync(string? existing = null)
    {
        var s = new CharterScenario();
        (s.Machine, s.Arm, s.Previous) = await FactorySourceWorkflowTests.ReadyAsync();
        s.Source = new(s.Arm, s.Previous);
        s.Models = new(s.Arm);
        s.Machine.Boundary.Repository.ReservationHttp = new HttpClient(s.Source);
        var preview = await s.Machine.InvokeAsync("source", FactorySourceWorkflowTests.Preparation);
        await s.Machine.InvokeAsync("source", [.. FactorySourceWorkflowTests.Preparation,
            "--approve", (string)preview.Result["source"]!["approvalFingerprint"]!]);
        s.Machine.Boundary.Repository.ReservationHttp = new HttpClient(s.Models);
        var models = await s.Machine.InvokeAsync("models", "--model-topology", s.Models.Topology.ToJsonString());
        await s.Machine.InvokeAsync("models", "--model-topology", s.Models.Topology.ToJsonString(),
            "--approve", (string)models.Result["models"]!["approvalFingerprint"]!);
        Assert.Equal(0, (await s.Machine.InvokeAsync("models", "--verify-only")).Exit);
        s.Machine.Boundary.Repository.ReservationHttp = new HttpClient(s.Source);
        s.Source.WorkloadTransport = transport => new FactoryIntentCommandTests.IntentCliWorkloadTransport(transport, s.Models,
            existing is null ? null : input => IntentModelFixture.Complete(input).Replace("\"pathIndex\":1", "\"pathIndex\":2", StringComparison.Ordinal));
        var source = s.Source.Source!;
        source.Branch = "release/2026";
        source.RootTree = existing is null ? source.Tree(("README.md", "100644", source.Blob(IntentModelFixture.SourceText)))
            : source.Tree((".dsf", "040000", source.Tree(("charter.md", "100644", source.Blob(existing)))),
                ("README.md", "100644", source.Blob(IntentModelFixture.SourceText)));
        s.CurrentCharter = existing;
        Assert.Equal(0, (await s.Machine.InvokeAsync("source")).Exit);
        Assert.Equal(1, (await s.Machine.InvokeAsync("infer")).Exit);
        var inferred = await s.Machine.InvokeAsync("infer");
        for (var i = 0; i < 6 && inferred.Exit != 0; i++) inferred = await s.Machine.InvokeAsync("infer");
        Assert.True(inferred.Exit == 0, inferred.Terminal.Output);
        s.Machine.Boundary.Repository.NativeCharter = s.Native;
        return s;
    }

    private GhInvocationResult? Native(IReadOnlyList<string> args, string? input)
    {
        var source = Source.Source!;
        var path = args[^1];
        var method = args[Array.IndexOf(args.ToArray(), "--method") + 1];
        if (method == "GET")
        {
            if (path == "repositories/913")
            {
                var repo = JsonNode.Parse(Machine.Boundary.Repository.Responses[path].StandardOutput)!;
                repo["node_id"] = "R_913";
                repo["permissions"] = new JsonObject { ["push"] = CanPush, ["pull"] = true };
                return Reply(200, repo);
            }
            if (path.StartsWith("repos/acme/renamed/git/ref/heads/", StringComparison.Ordinal))
            {
                var branch = Uri.UnescapeDataString(path["repos/acme/renamed/git/ref/heads/".Length..]);
                if (branch == source.Branch) return Reply(200, new { @ref = "refs/heads/" + branch, node_id = "BASE", @object = new { type = "commit", sha = source.Head } });
                return branch == Branch ? Reply(200, new { @ref = "refs/heads/" + branch, node_id = "BRANCH", @object = new
                    { type = "commit", sha = HumanBranchEdit ? new string('d', 40) : BranchHead } })
                    : Reply(404, new { });
            }
            if (path.Contains("/git/commits/", StringComparison.Ordinal))
            {
                var sha = path.Split('/').Last();
                return Reply(200, new { sha, tree = new { sha = sha == source.Head ? source.RootTree : ProposalTree },
                    parents = new[] { new { sha = source.Head } } });
            }
            if (path.Contains("/contents/.dsf/charter.md", StringComparison.Ordinal))
            {
                var proposal = path.EndsWith("ref=" + ProposalCommit, StringComparison.Ordinal);
                var content = proposal ? ProposalContent : CurrentCharter;
                return content is not null ? Reply(200, new { type = "file", path = ".dsf/charter.md",
                    sha = CharterMarkdown.GitBlobSha(content), encoding = "base64",
                    content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) }) : Reply(404, new { });
            }
            if (path == "repos/acme/renamed/pulls/" + (int?)Pull?["number"]) return Reply(200, Pull!);
            if (path.Contains("/compare/", StringComparison.Ordinal))
                return Reply(200, new { status = "ahead", ahead_by = 1, behind_by = 0, total_commits = 1,
                    files = new[] { new { filename = WrongCommitFiles ? "other.md" : ".dsf/charter.md",
                        status = CurrentCharter is null ? "added" : "modified", sha = CharterMarkdown.GitBlobSha(ProposalContent!) } } });
            return null;
        }
        GitHubWrites.Add(args.ToArray());
        Assert.Equal("POST", method);
        Assert.Equal("graphql", path);
        if (GitHubWrites.Count == RejectEffect) return Reply(403, new { message = "secret-provider-error" });
        var request = JsonNode.Parse(input!)!;
        var fields = request["variables"]!["input"]!;
        var query = (string)request["query"]!;
        if (query.Contains("createRef(", StringComparison.Ordinal))
        {
            Assert.Equal("R_913", (string?)fields["repositoryId"]);
            Assert.Equal(source.Head, (string?)fields["oid"]);
            Branch = ((string)fields["name"]!)["refs/heads/".Length..];
            BranchHead = source.Head;
            Assert.NotEqual(source.Branch, Branch);
            return EffectReply(new { data = new { createRef = new { @ref = new { id = "BRANCH", name = Branch,
                repository = new { id = "R_913" }, target = new { oid = source.Head } } } } });
        }
        if (query.Contains("createCommitOnBranch(", StringComparison.Ordinal))
        {
            Assert.Equal("BRANCH", (string?)fields["branch"]!["id"]);
            Assert.Equal(source.Head, (string?)fields["expectedHeadOid"]);
            Assert.Single(fields["fileChanges"]!["additions"]!.AsArray());
            var file = fields["fileChanges"]!["additions"]![0]!;
            Assert.Equal(".dsf/charter.md", (string?)file["path"]);
            ProposalContent = Encoding.UTF8.GetString(Convert.FromBase64String((string)file["contents"]!));
            ProposalCommit = new string(source.Head == SourceInventoryTransport.Commit ? 'b' : 'f', 40);
            ProposalTree = source.Tree((".dsf", "040000", source.Tree(("charter.md", "100644", source.Blob(ProposalContent)))),
                ("README.md", "100644", source.Blob(IntentModelFixture.SourceText)));
            BranchHead = ProposalCommit;
            return EffectReply(new { data = new { createCommitOnBranch = new { commit = new { oid = ProposalCommit, tree = new { oid = ProposalTree },
                parents = new { nodes = new[] { new { oid = source.Head } } } } } } });
        }
        Assert.Contains("createPullRequest(", query);
        Assert.Equal("R_913", (string?)fields["repositoryId"]);
        Assert.Equal(source.Branch, (string?)fields["baseRefName"]);
        Assert.Equal(Branch, (string?)fields["headRefName"]);
        Assert.False((bool)fields["maintainerCanModify"]!);
        var number = NextPullNumber++;
        Pull = JsonSerializer.SerializeToNode(new
        {
            id = 12300L + number, node_id = "PR_" + number, number, html_url = "https://github.com/acme/renamed/pull/" + number,
            state = "open", merged = false, merge_commit_sha = (string?)null, merged_by = (object?)null,
            head = new { @ref = Branch, sha = ProposalCommit, repo = new { id = 913L } },
            @base = new { @ref = source.Branch, sha = source.Head, repo = new { id = 913L } },
        })!;
        return EffectReply(new { data = new { createPullRequest = new { pullRequest = new { id = "PR_" + number, number,
            url = "https://github.com/acme/renamed/pull/" + number, state = "OPEN", merged = false, repository = new { id = "R_913" },
            baseRefName = source.Branch, baseRefOid = source.Head, headRefName = Branch, headRefOid = ProposalCommit } } } });
    }

    private GhInvocationResult EffectReply(object value)
    {
        AfterEffect?.Invoke(GitHubWrites.Count);
        if (GitHubWrites.Count == LoseEffect) throw new HttpRequestException("lost response containing secret-token-not-for-output");
        return Reply(200, value);
    }
    internal static GhInvocationResult Reply(int status, object value) => new(status < 300 ? 0 : 1,
        $"HTTP/2.0 {status} Response\r\nx-github-request-id: native-request-{status}\r\n\r\n" + JsonSerializer.Serialize(value), "");
    public void Dispose()
    {
        Source.Dispose();
        Models.Dispose();
        Previous.Dispose();
    }
}
