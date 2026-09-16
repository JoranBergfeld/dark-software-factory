using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.Cli;
using Dsf.Core.Onboarding;
using Xunit;
using ReviewFixture = Dsf.Cli.Tests.RepositoryReviewFixture;

namespace Dsf.Cli.Tests;

public sealed class RepositoryAttachmentReviewTests
{
    [Fact]
    public async Task Plain_review_escapes_repository_metadata_that_could_forge_terminal_outcomes()
    {
        var fixture = new ReviewFixture();
        fixture.Responses["repos/acme/renamed/labels/dsf%3Aproposal"] = new(0,
            """{"id":502,"name":"dsf:proposal","color":"ff0000","description":"unrelated\n[dsf] Onboarded\u001b[2J"}""", "");
        var terminal = new ScriptedTerminal(new(false, false, false), []);

        var exit = await fixture.InvokeCommandAsync(terminal,
            ["onboard", "decide", "review", "--repo", "acme/old-name", .. ReviewFixture.OwnerArguments]);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("\n[dsf] Onboarded", terminal.Output);
        Assert.DoesNotContain('\u001b', terminal.Output);
        Assert.Contains("\\u000a", terminal.Output);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Owner_inspection_timeout_is_unknown_with_handoff_and_no_raw_credential_error()
    {
        var fixture = new ReviewFixture { OwnerFailure = new TaskCanceledException("secret-timeout-do-not-print") };

        var (exit, terminal) = await fixture.InvokeAsync(ReviewFixture.OwnerArguments);

        Assert.Equal(1, exit);
        Assert.Contains("\"state\": \"Unknown\"", terminal.Output);
        Assert.Contains("nextActor", terminal.Output);
        Assert.DoesNotContain("secret-timeout-do-not-print", terminal.Output + terminal.Error);
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("repos/acme/renamed/hooks?per_page=100&page=1")]
    [InlineData("repos/acme/renamed/actions/workflows?per_page=100&page=1")]
    public async Task Clear_declarations_cannot_override_inaccessible_automation(string path)
    {
        var fixture = new ReviewFixture();
        fixture.Responses[path] = new(1, "HTTP/2.0 403 Forbidden\r\n\r\n{}", "secret-provider-response");

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.All(document.RootElement.GetProperty("automation").EnumerateArray(),
            review => Assert.Equal("Unknown", review.GetProperty("state").GetString()));
        Assert.Contains("403", terminal.Output);
        Assert.DoesNotContain("secret-provider-response", terminal.Output + terminal.Error);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Cancellation_returns_canceled_not_a_completed_review()
    {
        var fixture = new ReviewFixture();
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exit = await fixture.InvokeCommandAsync(terminal, ["onboard", "decide", "review"], cancellation.Token);

        Assert.Equal(130, exit);
        Assert.Empty(terminal.Output);
        Assert.Empty(fixture.Invocations);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Credential_bearing_owner_endpoint_is_rejected_without_any_provider_contact()
    {
        var fixture = new ReviewFixture();

        var (exit, terminal) = await fixture.InvokeAsync(
            "--owner-appconfig-endpoint", "https://operator:secret-do-not-print@owner.azconfig.io",
            "--owner-keyvault-uri", "https://owner.vault.azure.net/");

        Assert.Equal(1, exit);
        Assert.Empty(fixture.Invocations);
        Assert.Empty(fixture.OwnerReads);
        Assert.Contains("--owner-appconfig-endpoint", terminal.Error);
        Assert.DoesNotContain("secret-do-not-print", terminal.Output + terminal.Error);
    }

    [Fact]
    public async Task Owner_adapter_uses_login_and_unlabelled_status_then_read_only_owner_secrets()
    {
        var fixture = new ReviewFixture();
        var credentials = await fixture.ReadAsync("https://owner.vault.azure.net/", true, CancellationToken.None);
        var runner = new RecordingAzureCliRunner(
            new(0, JsonSerializer.Serialize(new[]
            {
                new
                {
                    key = "dsf/owner/bootstrap/dsf-owner/status",
                    value = JsonSerializer.Serialize(new OwnerBootstrapStatus(
                        OwnerBootstrapStage.Completed, DateTimeOffset.UnixEpoch, [OwnerBootstrapStage.Completed],
                        "https://owner.vault.azure.net/", "https://owner.azconfig.io", "7", "42", "selected")),
                },
            }), ""),
            new(0, "7", ""), new(0, "42", ""), new(0, credentials.PrivateKey, ""));
        fixture.OwnerReader = new AzureCliOwnerBootstrapClient(runner);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(0, exit);
        Assert.Contains("\\0", runner.Invocations[0]);
        Assert.Contains("login", runner.Invocations[0]);
        Assert.All(runner.Invocations.Skip(1), invocation =>
        {
            Assert.Equal(["keyvault", "secret", "show"], invocation.Take(3));
            Assert.Contains("owner", invocation);
        });
        Assert.DoesNotContain("PRIVATE KEY", terminal.Output + terminal.Error);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        var typed = JsonSerializer.Deserialize<RepositoryAttachmentReview>(terminal.Output, options)!;
        var roundTrip = JsonSerializer.Deserialize<RepositoryAttachmentReview>(JsonSerializer.Serialize(typed, options), options)!;
        Assert.Equal(913, roundTrip.Repository!.Id);
        Assert.Equal(RepositoryReviewState.Confirmed, roundTrip.State);
        Assert.Equal(3, roundTrip.Automation.Count);
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("id", "914")]
    [InlineData("default_branch", "\"next-release\"")]
    public async Task Repository_drift_during_review_invalidates_the_mixed_assessment(string property, string value)
    {
        var fixture = new ReviewFixture();
        var changed = System.Text.Json.Nodes.JsonNode.Parse(fixture.Responses["repos/acme/old-name"].StandardOutput)!;
        changed[property] = System.Text.Json.Nodes.JsonNode.Parse(value);
        fixture.Responses["repos/acme/renamed"] = new(0, changed.ToJsonString(), "");

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        Assert.Contains("repository-stability", terminal.Output);
        Assert.Contains("changed during review", terminal.Output);
        Assert.Contains("\"id\": 913", terminal.Output);
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("label")]
    [InlineData("workflow")]
    [InlineData("workflow-file")]
    [InlineData("workflow-state")]
    [InlineData("hook")]
    public async Task Incomplete_provider_shapes_return_unknown_facts_not_raw_errors(string surface)
    {
        var fixture = new ReviewFixture();
        var (path, json) = surface switch
        {
            "repository" => ("repos/acme/old-name", """{"id":913,"private-source-do-not-print":"secret"}"""),
            "label" => ("repos/acme/renamed/labels/dsf%3Aproposal", """{"private-source-do-not-print":"secret"}"""),
            "workflow" => ("repos/acme/renamed/actions/workflows?per_page=100&page=1", """{"total_count":1,"workflows":[{}]}"""),
            "workflow-file" => ("repos/acme/renamed/contents/.github/workflows/check.yml?ref=release%2F2026",
                """{"type":"file","encoding":"base64","sha":"blob","content":null}"""),
            "workflow-state" => ("repos/acme/renamed/actions/workflows?per_page=100&page=1",
                """{"total_count":1,"workflows":[{"id":1,"state":"unrecognized","path":".github/workflows/check.yml"}]}"""),
            _ => ("repos/acme/renamed/hooks?per_page=100&page=1", """[{"active":true,"events":null}]"""),
        };
        fixture.Responses[path] = new(0, json, "");
        if (surface == "workflow-file")
        {
            fixture.Responses["repos/acme/renamed/actions/workflows?per_page=100&page=1"] = new(0,
                """{"total_count":1,"workflows":[{"id":1,"state":"active","path":".github/workflows/check.yml"}]}""", "");
        }

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        Assert.Contains("\"state\": \"Unknown\"", terminal.Output);
        Assert.Empty(terminal.Error);
        Assert.DoesNotContain("private-source-do-not-print", terminal.Output);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Interactive_review_collects_inputs_without_claiming_onboarding_or_permission_to_mutate()
    {
        var fixture = new ReviewFixture();
        var terminal = new ScriptedTerminal(new(true, false, false),
            ["acme/old-name", "clear", "clear", "clear", "acme/platform"]);

        var exit = await fixture.InvokeCommandAsync(terminal, ["onboard", "decide", "review", .. ReviewFixture.OwnerArguments]);

        Assert.Equal(0, exit);
        Assert.Equal(5, terminal.Prompts.Count);
        Assert.Contains("equivalent: dsf onboard decide review --repo acme/old-name", terminal.Output);
        Assert.Contains("Not onboarded; no remote changes", terminal.Output);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Owner_bootstrap_endpoint_mismatch_blocks_before_reading_any_signing_key()
    {
        var fixture = new ReviewFixture();
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, JsonSerializer.Serialize(new[]
        {
            new
            {
                key = "dsf/owner/bootstrap/dsf-owner/status",
                value = JsonSerializer.Serialize(new OwnerBootstrapStatus(
                    OwnerBootstrapStage.Completed, DateTimeOffset.UnixEpoch, [OwnerBootstrapStage.Completed],
                    "https://different.vault.azure.net/", "https://owner.azconfig.io", "7", "42", "selected")),
            },
        }), ""));
        fixture.OwnerReader = new AzureCliOwnerBootstrapClient(runner);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        Assert.Contains("owner-prerequisites", terminal.Output);
        Assert.Contains("Blocked", terminal.Output);
        Assert.Contains("endpoints", terminal.Output);
        Assert.DoesNotContain(runner.Invocations, args => args.Contains("keyvault"));
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("issues", "ProposalIssue")]
    [InlineData("pull_request", "CharterPullRequest")]
    [InlineData("label", "LabelCreation")]
    public async Task Known_coding_automation_blocks_only_affected_mutation_despite_clear_declaration(string trigger, string mutation)
    {
        var fixture = new ReviewFixture();
        fixture.Responses["repos/acme/renamed/actions/workflows?per_page=100&page=1"] = new(0,
            """{"total_count":1,"workflows":[{"id":90,"path":".github/workflows/dispatch.yml","state":"active"}]}""", "");
        var source = $"""
            name: Coding dispatch
            on: [{trigger}]
            jobs:
              dispatch:
                runs-on: ubuntu-latest
                steps:
                  - run: gh issue edit "$ISSUE" --add-assignee '@copilot'
                  - run: echo secret-source-do-not-persist
            """;
        fixture.Responses["repos/acme/renamed/contents/.github/workflows/dispatch.yml?ref=release%2F2026"] = new(0,
            JsonSerializer.Serialize(new { type = "file", sha = "workflow-sha", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(source)) }), "");

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.All(document.RootElement.GetProperty("automation").EnumerateArray(), review =>
            Assert.Equal(review.GetProperty("mutation").GetString() == mutation ? "Blocked" : "Confirmed", review.GetProperty("state").GetString()));
        Assert.Contains(".github/workflows/dispatch.yml", terminal.Output);
        Assert.Contains("workflow-sha", terminal.Output);
        Assert.DoesNotContain("secret-source-do-not-persist", terminal.Output + terminal.Error);
        fixture.AssertReadOnly();
    }

    public static TheoryData<string, string, string> WorkflowGuardCases
    {
        get
        {
            var cases = new TheoryData<string, string, string>();
            (string Condition, string IssueState, string PrState)[] conditions =
            [
                ("true", "Blocked", "Blocked"),
                ("false", "Unknown", "Unknown"),
                ("github.event_name == 'pull_request'", "Unknown", "Blocked"),
                ("github.event_name == 'issues'", "Blocked", "Unknown"),
                ("github.event_name != 'issues'", "Unknown", "Blocked"),
                ("${{ true }}", "Blocked", "Blocked"),
                ("${{ false }}", "Unknown", "Unknown"),
                ("${{ github.event_name == 'pull_request' }}", "Unknown", "Blocked"),
                ("${{ github.event_name != 'pull_request' }}", "Blocked", "Unknown"),
                ("${{ github.event_name == 'PULL_REQUEST' }}", "Unknown", "Blocked"),
                ("contains(github.event_name, 'pull')", "Unknown", "Unknown"),
                ("${{ env.DELIVERY_ENABLED }}", "Unknown", "Unknown"),
                ("${{ 'false' }}", "Unknown", "Unknown"),
                ("[]", "Unknown", "Unknown"),
            ];
            foreach (var (condition, issueState, prState) in conditions)
            {
                cases.Add($"if: {condition}\nsteps:\n  - run: npm publish", issueState, prState);
                cases.Add($"steps:\n  - if: {condition}\n    run: npm publish", issueState, prState);
            }

            cases.Add("if: github.event_name == 'pull_request'\nsteps:\n  - if: github.event_name == 'issues'\n    run: npm publish",
                "Unknown", "Unknown");
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(WorkflowGuardCases))]
    public async Task Delivery_guards_require_eligible_jobs_and_steps_for_each_workflow_event(
        string job, string issueState, string prState)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow("on: [issues, pull_request]\njobs:\n  publish:\n    runs-on: ubuntu-latest\n"
            + string.Join('\n', job.Split('\n').Select(line => "    " + line)));

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var automation = document.RootElement.GetProperty("automation").EnumerateArray().ToArray();
        Assert.Equal([issueState, prState, "Confirmed"], automation.Select(review => review.GetProperty("state").GetString()));
        Assert.All(automation, review =>
            Assert.Equal(review.GetProperty("state").GetString() == "Blocked",
                review.GetProperty("evidence").EnumerateArray().Any(value => value.GetString()!.Contains("instruction observed", StringComparison.Ordinal))));
        Assert.DoesNotContain("DELIVERY_ENABLED", terminal.Output + terminal.Error);
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("on: [issues, pull_request]", "Unknown", "Blocked")]
    [InlineData("on: {issues: {types: [closed]}, pull_request: {types: [opened]}}", "Confirmed", "Blocked")]
    [InlineData("on:\n  issues:\n    types: [opened]\n  pull_request:\n    types: [closed]", "Unknown", "Confirmed")]
    public async Task Eligible_delivery_in_another_job_still_blocks_only_its_event_and_activity(
        string trigger, string issueState, string prState)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow(trigger + """

            jobs:
              disabled:
                if: false
                runs-on: ubuntu-latest
                steps:
                  - run: npm publish
              uncertain:
                if: ${{ env.DELIVERY_ENABLED }}
                runs-on: ubuntu-latest
                steps:
                  - run: npm publish
              pull_request_only:
                if: ${{ github.event_name == 'pull_request' }}
                runs-on: ubuntu-latest
                steps:
                  - if: true
                    run: npm publish
            """);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.Equal([issueState, prState, "Confirmed"],
            document.RootElement.GetProperty("automation").EnumerateArray().Select(review => review.GetProperty("state").GetString()));
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("name: Deployment unit tests\nsteps:\n  - run: echo harmless")]
    [InlineData("steps:\n  - name: Publish deployment tests\n    run: echo harmless")]
    [InlineData("env:\n  DESCRIPTION: Assign @copilot and deploy\nsteps:\n  - run: echo harmless")]
    [InlineData("steps:\n  - run: echo '@copilot deployment unit tests'")]
    [InlineData("steps:\n  - run: echo 'kubectl apply -f deployment.yml'")]
    [InlineData("steps:\n  - run: printf '%s' 'npm publish'")]
    [InlineData("steps:\n  - shell: python\n    run: print('deployment')")]
    [InlineData("steps:\n  - shell: echo {0}\n    run: npm publish")]
    [InlineData("defaults:\n  run:\n    shell: echo {0}\nsteps:\n  - run: npm publish")]
    [InlineData("steps:\n  - run: |\n      cat <<'EXAMPLE'\n      kubectl apply -f deployment.yml\n      EXAMPLE")]
    public async Task Workflow_metadata_and_harmless_literals_are_not_known_delivery_instructions(string job)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow("name: Deployment unit tests\non: [pull_request]\njobs:\n  tests:\n    runs-on: ubuntu-latest\n"
            + string.Join('\n', job.Split('\n').Select(line => "    " + line)));

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.All(document.RootElement.GetProperty("automation").EnumerateArray(), review =>
            Assert.Equal(review.GetProperty("mutation").GetString() == "CharterPullRequest" ? "Unknown" : "Confirmed",
                review.GetProperty("state").GetString()));
        Assert.DoesNotContain("instruction observed", terminal.Output);
        var finding = Assert.Single(document.RootElement.GetProperty("findings").EnumerateArray(),
            item => item.GetProperty("check").GetString() == "automation:CharterPullRequest");
        Assert.Contains("Repeating a declaration cannot resolve", finding.GetProperty("nextAction").GetString());
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("gh issue edit \"$ISSUE\" --add-assignee '@copilot'", "Blocked")]
    [InlineData("gh issue edit \"$ISSUE\" --add-assignee=@copilot", "Blocked")]
    [InlineData("gh issue edit \"$ISSUE\" --body \"--add-assignee @copilot\"", "Unknown")]
    [InlineData("gh issue edit \"$ISSUE\" --body='--add-assignee' '@copilot'", "Unknown")]
    [InlineData("gh issue edit \"$ISSUE\" --body '--add-assignee' '@copilot'", "Unknown")]
    [InlineData("gh issue edit \"$ISSUE\" --body '--add-assignee=@copilot'", "Unknown")]
    [InlineData("gh issue edit \"$ISSUE\" --body 'Human review' --add-assignee '@copilot'", "Blocked")]
    [InlineData("gh agent-task create", "Blocked")]
    [InlineData("gh agent-task list", "Unknown")]
    [InlineData("gh pr merge \"$PR\"", "Blocked")]
    [InlineData("gh pr merge --help", "Unknown")]
    [InlineData("npm publish", "Blocked")]
    [InlineData("npm publish --dry-run", "Unknown")]
    [InlineData("npm publish --dry-run=true", "Unknown")]
    [InlineData("kubectl apply -f manifest.yml", "Blocked")]
    [InlineData("terraform apply -auto-approve", "Blocked")]
    [InlineData("gh workflow run tests.yml", "Unknown")]
    [InlineData("bash scripts/deploy.sh", "Unknown")]
    public async Task Delivery_commands_are_distinguished_from_arguments_and_opaque_dispatch(string command, string state)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow($"""
            on: [pull_request]
            jobs:
              tests:
                runs-on: ubuntu-latest
                steps:
                  - run: {command}
            """);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.All(document.RootElement.GetProperty("automation").EnumerateArray(), review =>
            Assert.Equal(review.GetProperty("mutation").GetString() == "CharterPullRequest" ? state : "Confirmed",
                review.GetProperty("state").GetString()));
        Assert.Equal(state == "Blocked", terminal.Output.Contains("instruction observed", StringComparison.Ordinal));
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("", "Blocked")]
    [InlineData(" --dry-run", "Unknown")]
    [InlineData(" --dry-run=true", "Unknown")]
    [InlineData(" --dry-run=false", "Blocked")]
    [InlineData(" --dry-run false", "Blocked")]
    [InlineData(" --dry-run true", "Unknown")]
    [InlineData(" '--dry-run=false'", "Blocked")]
    [InlineData(" --dry-run=true --dry-run=false", "Blocked")]
    [InlineData(" --dry-run --dry-run=false", "Blocked")]
    [InlineData(" --dry-run=false --dry-run=true", "Unknown")]
    [InlineData(" --dry-run=$PREVIEW", "Unknown")]
    [InlineData(" --dry-run=maybe", "Unknown")]
    [InlineData(" --dry-run=$PREVIEW --dry-run=false", "Unknown")]
    [InlineData(" --dry-run ''", "Unknown")]
    [InlineData(" --dry-run FALSE", "Unknown")]
    [InlineData(" --no-dry-run", "Blocked")]
    [InlineData(" --dry-run --no-dry-run", "Blocked")]
    [InlineData(" --no-dry-run --dry-run", "Unknown")]
    [InlineData(" --dry-run=false --help", "Unknown")]
    public async Task Npm_publish_dry_run_flags_preserve_known_delivery_when_explicitly_disabled(string arguments, string state)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow($"""
            on: [pull_request]
            jobs:
              publish:
                runs-on: ubuntu-latest
                steps:
                  - run: npm publish{arguments}
            """);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.All(document.RootElement.GetProperty("automation").EnumerateArray(), review =>
            Assert.Equal(review.GetProperty("mutation").GetString() == "CharterPullRequest" ? state : "Confirmed",
                review.GetProperty("state").GetString()));
        Assert.Equal(state == "Blocked", terminal.Output.Contains("instruction observed", StringComparison.Ordinal));
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("\"$PR\"", "Blocked")]
    [InlineData("\"$PR\" --disable-auto", "Unknown")]
    [InlineData("\"$PR\" --disable-auto=true", "Unknown")]
    [InlineData("\"$PR\" --disable-auto=false", "Blocked")]
    [InlineData("\"$PR\" --disable-auto=0", "Blocked")]
    [InlineData("\"$PR\" --disable-auto=1", "Unknown")]
    [InlineData("\"$PR\" --disable-auto=False", "Blocked")]
    [InlineData("\"$PR\" --disable-auto=TRUE", "Unknown")]
    [InlineData("\"$PR\" --disable-auto=FaLsE", "Unknown")]
    [InlineData("\"$PR\" --disable-auto=$DISABLE_AUTO", "Unknown")]
    [InlineData("\"$PR\" --disable-auto=maybe", "Unknown")]
    [InlineData("\"$PR\" --disable-auto=true --disable-auto=false", "Blocked")]
    [InlineData("\"$PR\" --disable-auto=false --disable-auto", "Unknown")]
    [InlineData("\"$PR\" --disable-auto false", "Unknown")]
    [InlineData("--disable-auto false", "Unknown")]
    [InlineData("--disable-auto false --disable-auto=false", "Blocked")]
    [InlineData("\"$PR\" --disable-auto false --disable-auto=false", "Unknown")]
    [InlineData("\"$PR\" --body '--disable-auto'", "Blocked")]
    [InlineData("\"$PR\" --body '--disable-auto=true'", "Blocked")]
    [InlineData("\"$PR\" --squash --disable-auto=false", "Blocked")]
    [InlineData("\"$PR\" --squash --disable-auto=true", "Unknown")]
    [InlineData("\"$PR\" --opaque-option", "Unknown")]
    [InlineData("\"$PR\" --disable-auto= --squash", "Unknown")]
    public async Task Disabling_auto_merge_is_not_delivery_but_explicit_false_preserves_known_merges(
        string arguments, string state)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow($"""
            on: [pull_request]
            jobs:
              merge:
                runs-on: ubuntu-latest
                steps:
                  - run: gh pr merge {arguments}
            """);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.All(document.RootElement.GetProperty("automation").EnumerateArray(), review =>
            Assert.Equal(review.GetProperty("mutation").GetString() == "CharterPullRequest" ? state : "Confirmed",
                review.GetProperty("state").GetString()));
        Assert.Equal(state == "Blocked", terminal.Output.Contains("instruction observed", StringComparison.Ordinal));
        Assert.DoesNotContain("DISABLE_AUTO", terminal.Output + terminal.Error);
        fixture.AssertReadOnly();
    }

    public static TheoryData<string, string, string, string> WorkflowActivityFilterCases
    {
        get
        {
            var cases = new TheoryData<string, string, string, string>();
            (string Event, string Activity, string Mutation, string State)[] activities =
            [
                ("issues", "opened", "ProposalIssue", "Blocked"),
                ("issues", "labeled", "ProposalIssue", "Blocked"),
                ("issues", "closed", "ProposalIssue", "Confirmed"),
                ("issues", "assigned", "ProposalIssue", "Confirmed"),
                ("issues", "edited", "ProposalIssue", "Confirmed"),
                ("issues", "unlabeled", "ProposalIssue", "Confirmed"),
                ("pull_request", "opened", "CharterPullRequest", "Blocked"),
                ("pull_request", "closed", "CharterPullRequest", "Confirmed"),
                ("pull_request", "labeled", "CharterPullRequest", "Confirmed"),
                ("pull_request_target", "opened", "CharterPullRequest", "Blocked"),
                ("pull_request_target", "closed", "CharterPullRequest", "Confirmed"),
                ("label", "created", "LabelCreation", "Blocked"),
                ("label", "edited", "LabelCreation", "Confirmed"),
                ("label", "deleted", "LabelCreation", "Confirmed"),
            ];
            foreach (var (eventName, activity, mutation, state) in activities)
            {
                cases.Add($"on: {{{eventName}: {{types: [{activity}]}}}}", activity, mutation, state);
                cases.Add($"on:\n  {eventName}:\n    types:\n      - {activity}", activity, mutation, state);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(WorkflowActivityFilterCases))]
    public async Task Workflow_activity_filters_block_only_events_caused_by_the_reviewed_mutation(
        string trigger, string activity, string mutation, string state)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow(trigger + """

            jobs:
              dispatch:
                runs-on: ubuntu-latest
                steps:
                  - run: gh issue edit "$ISSUE" --add-assignee '@copilot'
            """);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(state == "Blocked" ? 1 : 0, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.All(document.RootElement.GetProperty("automation").EnumerateArray(), review =>
            Assert.Equal(review.GetProperty("mutation").GetString() == mutation ? state : "Confirmed",
                review.GetProperty("state").GetString()));
        Assert.Contains($"types: {activity}", terminal.Output);
        Assert.Equal(state == "Blocked", terminal.Output.Contains("instruction observed", StringComparison.Ordinal));
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("on: issues", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on: [issues]", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on:\n  issues:", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {}}", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: null}", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: ~}", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on: pull_request", "Confirmed", "Blocked", "Confirmed")]
    [InlineData("on: {pull_request_target: {}}", "Confirmed", "Blocked", "Confirmed")]
    [InlineData("on: label", "Confirmed", "Confirmed", "Blocked")]
    [InlineData("on: [push, create]", "Confirmed", "Blocked", "Confirmed")]
    [InlineData("on: {issues: {types: [closed, opened]}}", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on:\n  issues:\n    types:\n      - closed\n      - labeled", "Blocked", "Confirmed", "Confirmed")]
    [InlineData("on: issue_comment", "Confirmed", "Confirmed", "Confirmed")]
    [InlineData("on: {issue_comment: {types: [created]}}", "Confirmed", "Confirmed", "Confirmed")]
    [InlineData("on: {pull_request_review: {types: [submitted]}}", "Confirmed", "Confirmed", "Confirmed")]
    [InlineData("on: {pull_request_review_comment: {types: [created]}}", "Confirmed", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {types: []}}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {types: null}}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {types: opened}}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on:\n  issues:\n    types:\n      closed: true", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {types: [closed, unknown_activity]}}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on:\n  issues:\n    types:\n      - closed\n      - unknown_activity", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {types: [closed, {}]}}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: [closed]}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {type: [closed]}}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {issues: {types: [closed], unknown_filter: true}}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: {pull_request: {types: [opend]}}", "Confirmed", "Unknown", "Confirmed")]
    [InlineData("on:\n  label:\n    types: created", "Confirmed", "Confirmed", "Unknown")]
    [InlineData("on: {label: {types: [opened]}}", "Confirmed", "Confirmed", "Unknown")]
    [InlineData("on: {issues: 'null'}", "Unknown", "Confirmed", "Confirmed")]
    [InlineData("on: future_event", "Unknown", "Unknown", "Unknown")]
    [InlineData("on: {future_event: {types: [opened]}}", "Unknown", "Unknown", "Unknown")]
    [InlineData("on: [{issues: {types: [closed]}}]", "Unknown", "Unknown", "Unknown")]
    [InlineData("on: []", "Unknown", "Unknown", "Unknown")]
    [InlineData("on: {}", "Unknown", "Unknown", "Unknown")]
    public async Task Workflow_event_defaults_and_uncertain_filters_never_silently_clear_or_block_mutations(
        string trigger, string issueState, string prState, string labelState)
    {
        var fixture = new ReviewFixture();
        fixture.SetWorkflow(trigger + """

            jobs:
              dispatch:
                runs-on: ubuntu-latest
                steps:
                  - run: gh issue edit "$ISSUE" --add-assignee '@copilot'
            """);

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        string[] states = [issueState, prState, labelState];
        Assert.Equal(states.All(state => state == "Confirmed") ? 0 : 1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var automation = document.RootElement.GetProperty("automation");
        Assert.Equal(states, automation.EnumerateArray().Select(review => review.GetProperty("state").GetString()));
        Assert.Equal(states.Select(state => state != "Unknown"),
            automation.EnumerateArray().Select(review => review.GetProperty("visibleInventoryComplete").GetBoolean()));
        Assert.Empty(terminal.Error);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Missing_external_automation_review_remains_pending_even_when_visible_inventory_is_empty()
    {
        var fixture = new ReviewFixture();

        var (exit, terminal) = await fixture.InvokeAsync(ReviewFixture.OwnerArguments);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var mutations = document.RootElement.GetProperty("automation");
        Assert.Equal(3, mutations.GetArrayLength());
        Assert.All(mutations.EnumerateArray(), mutation =>
        {
            Assert.Equal("Pending", mutation.GetProperty("state").GetString());
            Assert.Equal("Unknown", mutation.GetProperty("declaration").GetString());
            Assert.False(mutation.GetProperty("exhaustiveTechnicalProof").GetBoolean());
        });
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Conflicting_label_meaning_is_recorded_without_altering_existing_labels()
    {
        var fixture = new ReviewFixture();
        fixture.Responses["repos/acme/renamed/labels/dsf%3Aproposal"] = new(0,
            """{"id":502,"name":"dsf:proposal","color":"ff0000","description":"Assign a coding agent and deploy this issue"}""", "");

        var (exit, terminal) = await fixture.InvokeAsync(ReviewFixture.OwnerArguments);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var labels = document.RootElement.GetProperty("labels");
        Assert.Equal(4, labels.GetArrayLength());
        var proposal = labels[0];
        Assert.Equal(502, proposal.GetProperty("id").GetInt64());
        Assert.Equal("ff0000", proposal.GetProperty("color").GetString());
        Assert.Equal("Blocked", proposal.GetProperty("semanticState").GetString());
        Assert.Contains("coding agent", proposal.GetProperty("description").GetString());
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("dsf:proposal", "DSF proposal for human consideration; not a delivery assignment")]
    [InlineData("dsf:proposal", "  dsf PROPOSAL for human consideration; not a delivery assignment.  ")]
    [InlineData("dsf:proposal", "DSF proposal for human consideration ;  not a delivery assignment.")]
    [InlineData("dsf:proposal", "DSF proposal for human consideration. Not a delivery assignment.")]
    [InlineData("dsf-outcome:approved", "Human-approved DSF proposal; not a delivery assignment")]
    [InlineData("dsf-outcome:approved", "Human approved DSF proposal; not a delivery assignment.")]
    [InlineData("dsf-outcome:rejected", "Human-rejected DSF proposal")]
    [InlineData("dsf-outcome:changes-requested", "DSF proposal requiring human-requested changes")]
    public async Task Presentation_only_label_variations_preserve_compatible_existing_definitions(string name, string description)
    {
        var fixture = new ReviewFixture();
        fixture.Responses[$"repos/acme/renamed/labels/{Uri.EscapeDataString(name)}"] = new(0,
            JsonSerializer.Serialize(new { id = 502, name, color = "ff0000", description }), "");

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var label = Assert.Single(document.RootElement.GetProperty("labels").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);
        Assert.Equal("Confirmed", label.GetProperty("semanticState").GetString());
        Assert.Equal(502, label.GetProperty("id").GetInt64());
        Assert.Equal("ff0000", label.GetProperty("color").GetString());
        Assert.Equal(description, label.GetProperty("description").GetString());
        fixture.AssertReadOnly();
    }

    public static TheoryData<string, string, string> CanonicalLabelMeaningCases
    {
        get
        {
            var cases = new TheoryData<string, string, string>();
            (string Name, string Description)[] labels =
            [
                ("dsf:proposal", "DSF proposal for human consideration; not a delivery assignment."),
                ("dsf-outcome:approved", "Human-approved DSF proposal; not a delivery assignment."),
                ("dsf-outcome:rejected", "Human-rejected DSF proposal."),
                ("dsf-outcome:changes-requested", "DSF proposal requiring human-requested changes."),
            ];
            foreach (var target in labels)
            {
                foreach (var observed in labels)
                {
                    cases.Add(target.Name, observed.Description, target.Name == observed.Name ? "Confirmed" : "Blocked");
                }

                cases.Add(target.Name, "Human-approved or human-rejected DSF proposal; decision pending.", "Unknown");
            }

            cases.Add("dsf-outcome:approved", "  HUMAN rejected DSF proposal  ", "Blocked");
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalLabelMeaningCases))]
    public async Task Canonical_label_meanings_conflict_when_used_for_a_different_allowed_label(
        string name, string description, string state)
    {
        var fixture = new ReviewFixture();
        fixture.Responses[$"repos/acme/renamed/labels/{Uri.EscapeDataString(name)}"] = new(0,
            JsonSerializer.Serialize(new { id = 502, name, color = "ff0000", description }), "");

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(state == "Confirmed" ? 0 : 1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.Equal(state, document.RootElement.GetProperty("state").GetString());
        var label = Assert.Single(document.RootElement.GetProperty("labels").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);
        Assert.Equal(state, label.GetProperty("semanticState").GetString());
        Assert.Equal(502, label.GetProperty("id").GetInt64());
        Assert.Equal("ff0000", label.GetProperty("color").GetString());
        Assert.Equal(description, label.GetProperty("description").GetString());
        var finding = Assert.Single(document.RootElement.GetProperty("findings").EnumerateArray(),
            item => item.GetProperty("check").GetString() == $"label:{name}");
        Assert.DoesNotContain("Explicit delivery/assignment", finding.GetProperty("detail").GetString());
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("Product council queue", "Unknown")]
    [InlineData("DSF suggestion for people to consider", "Unknown")]
    [InlineData("Do not assign a coding agent or deploy this issue", "Unknown")]
    [InlineData("Assignment and deployment policy needs owner review", "Unknown")]
    [InlineData("DSF proposal for human consideration; a delivery assignment.", "Blocked")]
    [InlineData("Automatically assign this issue to @copilot.", "Blocked")]
    [InlineData("Assign a coding agent and deploy this issue", "Blocked")]
    [InlineData("Deploy this issue to production.", "Blocked")]
    [InlineData("Accepted for delivery.", "Blocked")]
    public async Task Label_meaning_distinguishes_uncertainty_from_delivery_assignment_conflicts(string? description, string state)
    {
        var fixture = new ReviewFixture();
        fixture.Responses["repos/acme/renamed/labels/dsf%3Aproposal"] = new(0,
            JsonSerializer.Serialize(new { id = 502, name = "dsf:proposal", color = "ff0000", description }), "");

        var (exit, terminal) = await fixture.InvokeAsync([.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        Assert.Equal(state, document.RootElement.GetProperty("state").GetString());
        var label = document.RootElement.GetProperty("labels")[0];
        Assert.Equal(state, label.GetProperty("semanticState").GetString());
        Assert.Equal(description, label.GetProperty("description").GetString());
        var finding = Assert.Single(document.RootElement.GetProperty("findings").EnumerateArray(),
            item => item.GetProperty("check").GetString() == "label:dsf:proposal");
        Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("nextActor").GetString()));
        Assert.Contains("outside DSF", finding.GetProperty("nextAction").GetString());
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("suspended", "installation-suspension", "Blocked")]
    [InlineData("all-repositories", "installation-selection", "Blocked")]
    [InlineData("insufficient-permissions", "installation-permissions", "Pending")]
    [InlineData("approval-required", "administrator-approval", "Pending")]
    [InlineData("revoked", "installation", "Denied")]
    [InlineData("not-selected", "repository-membership", "Pending")]
    [InlineData("organization-restricted", "installation", "Denied")]
    [InlineData("owner-incomplete", "owner-prerequisites", "Pending")]
    public async Task Unapproved_or_incompatible_installation_never_passes_attachment(string scenario, string check, string state)
    {
        var fixture = new ReviewFixture();
        var installation = System.Text.Json.Nodes.JsonNode.Parse(fixture.AppResponses["/app/installations/42"].Json)!;
        switch (scenario)
        {
            case "suspended": installation["suspended_at"] = "2026-09-01T00:00:00Z"; break;
            case "all-repositories": installation["repository_selection"] = "all"; break;
            case "insufficient-permissions": installation["permissions"]!["issues"] = "read"; break;
            case "approval-required":
                fixture.AppResponses["/app"] = (HttpStatusCode.OK,
                    """{"id":7,"slug":"dsf-owner","permissions":{"issues":"write","contents":"read","metadata":"read","administration":"write"}}""");
                break;
            case "not-selected": fixture.AppResponses["/repos/acme/renamed/installation"] = (HttpStatusCode.NotFound, "{}"); break;
            case "owner-incomplete": fixture.OwnerIdentity = null; break;
        }
        fixture.AppResponses["/app/installations/42"] = (
            scenario == "revoked" ? HttpStatusCode.NotFound
            : scenario == "organization-restricted" ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
            installation.ToJsonString());

        var (exit, terminal) = await fixture.InvokeAsync(ReviewFixture.OwnerArguments);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var finding = Assert.Single(document.RootElement.GetProperty("findings").EnumerateArray(),
            value => value.GetProperty("check").GetString() == check);
        Assert.Equal(state, finding.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("nextActor").GetString()));
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData("archived", "true")]
    [InlineData("disabled", "true")]
    [InlineData("has_issues", "false")]
    public async Task Incompatible_repository_is_blocked_without_repair(string property, string value)
    {
        var fixture = new ReviewFixture();
        var response = fixture.Responses["repos/acme/old-name"];
        var json = System.Text.Json.Nodes.JsonNode.Parse(response.StandardOutput)!;
        json[property] = bool.Parse(value);
        fixture.Responses["repos/acme/old-name"] = response with { StandardOutput = json.ToJsonString() };

        var (exit, terminal) = await fixture.InvokeAsync();

        Assert.Equal(1, exit);
        Assert.Contains("\"check\": \"repository-settings\"", terminal.Output);
        Assert.Contains("\"state\": \"Blocked\"", terminal.Output);
        Assert.Contains("No repository settings", terminal.Output);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Prepared_owner_verifies_app_installation_and_repository_membership_without_factory_credentials()
    {
        var fixture = new ReviewFixture();

        var (exit, terminal) = await fixture.InvokeAsync(
            [.. ReviewFixture.OwnerArguments, .. ReviewFixture.ClearAutomationArguments]);

        Assert.Contains("\"check\": \"app-registration\"", terminal.Output);
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var installation = document.RootElement.GetProperty("installation");
        Assert.Equal(7, installation.GetProperty("appId").GetInt64());
        Assert.Equal(42, installation.GetProperty("id").GetInt64());
        Assert.Equal(81, installation.GetProperty("accountId").GetInt64());
        Assert.Equal("selected", installation.GetProperty("repositorySelection").GetString());
        Assert.Equal("write", installation.GetProperty("grantedPermissions").GetProperty("issues").GetString());
        fixture.AssertReadOnly();
    }

    [Theory]
    [InlineData(401, "Denied")]
    [InlineData(403, "Denied")]
    [InlineData(404, "Unknown")]
    public async Task Unreadable_repository_reports_authority_state_without_leaking_provider_errors(int status, string state)
    {
        var fixture = new ReviewFixture();
        fixture.Responses["repos/acme/old-name"] = new(1,
            $"HTTP/2.0 {status} Forbidden\r\nX-GitHub-SSO: required; url=https://github.com/orgs/acme/sso\r\n\r\n{{}}",
            "sensitive provider text: token-do-not-print");

        var (exit, terminal) = await fixture.InvokeAsync();

        Assert.Equal(1, exit);
        Assert.Contains($"\"state\": \"{state}\"", terminal.Output);
        Assert.Contains("organization", terminal.Output);
        Assert.Contains("nextActor", terminal.Output);
        Assert.DoesNotContain("token-do-not-print", terminal.Output + terminal.Error);
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Review_uses_effective_operator_identity_and_observed_renamed_repository()
    {
        var fixture = new ReviewFixture();

        var (exit, terminal) = await fixture.InvokeAsync();

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(terminal.Output);
        var review = document.RootElement;
        Assert.True(review.TryGetProperty("operator", out var actor), "The review must attribute discovery to the effective gh identity.");
        Assert.Equal("reviewer", actor.GetProperty("login").GetString());
        var repository = review.GetProperty("repository");
        Assert.Equal(913, repository.GetProperty("id").GetInt64());
        Assert.Equal("renamed", repository.GetProperty("name").GetString());
        Assert.Equal("release/2026", repository.GetProperty("defaultBranch").GetString());
        Assert.Equal("Unknown", review.GetProperty("state").GetString());
        fixture.AssertReadOnly();
    }

    [Fact]
    public async Task Redirected_review_requires_explicit_repository_without_starting_onboarding()
    {
        var terminal = new ScriptedTerminal(new(false, false, false), []);

        var exit = await CliApplication.InvokeAsync(
            ["onboard", "decide", "review"], CancellationToken.None, terminal);

        Assert.Equal(1, exit);
        Assert.Contains("--repo is required when prompts are unavailable", terminal.Error);
        Assert.Empty(terminal.Prompts);
        Assert.Empty(terminal.Output);
    }

}

internal sealed class RepositoryReviewFixture : HttpMessageHandler, IGhCliRunner, IOwnerCredentialReader
{
    public static readonly string[] OwnerArguments =
        ["--owner-appconfig-endpoint", "https://owner.azconfig.io", "--owner-keyvault-uri", "https://owner.vault.azure.net/"];
    public static readonly string[] ClearAutomationArguments =
        ["--issue-automation", "clear", "--pr-automation", "clear", "--label-automation", "clear", "--automation-owner", "acme/platform"];
    private static readonly string PrivateKey = CreatePrivateKey();
    public OwnerGitHubIdentity? OwnerIdentity { get; set; } = new(
        "7", "42", "selected", "https://owner.vault.azure.net/", "https://owner.azconfig.io");
    public Dictionary<string, (HttpStatusCode Status, string Json)> AppResponses { get; } = new()
    {
        ["/app"] = (HttpStatusCode.OK, """{"id":7,"slug":"dsf-owner","permissions":{"issues":"write","contents":"read","metadata":"read"}}"""),
        ["/app/installations/42"] = (HttpStatusCode.OK, """
                {"id":42,"app_id":7,"account":{"id":81,"login":"acme","type":"Organization"},
                 "repository_selection":"selected","suspended_at":null,
                 "permissions":{"issues":"write","contents":"read","metadata":"read"}}
                """),
        ["/repos/acme/renamed/installation"] = (HttpStatusCode.OK, """{"id":42}"""),
    };
    public List<HttpMethod> AppMethods { get; } = [];
    public List<string> OwnerReads { get; } = [];
    public IOwnerCredentialReader? OwnerReader { get; set; }
    public IAzureCliRunner? BoundaryAzureRunner { get; set; }
    public HttpClient? ReservationHttp { get; set; }
    public TimeProvider? ReservationClock { get; set; }
    public Exception? OwnerFailure { get; set; }
    public Dictionary<string, GhInvocationResult> Responses { get; } = new()
    {
        ["user"] = new(0, """{"id":12,"login":"reviewer"}""", ""),
        ["repositories/913"] = new(0, """
                {"id":913,"owner":{"id":81,"login":"acme"},"name":"renamed","default_branch":"release/2026",
                 "visibility":"private","archived":false,"disabled":false,"has_issues":true}
                """, ""),
        ["repos/acme/old-name"] = new(0, """
                {"id":913,"owner":{"id":81,"login":"acme"},"name":"renamed","default_branch":"release/2026",
                 "visibility":"private","archived":false,"disabled":false,"has_issues":true}
                """, ""),
        ["repos/acme/renamed"] = new(0, """
                {"id":913,"owner":{"id":81,"login":"acme"},"name":"renamed","default_branch":"release/2026",
                 "visibility":"private","archived":false,"disabled":false,"has_issues":true}
                """, ""),
        ["repos/acme/renamed/labels/dsf%3Aproposal"] = new(1, "HTTP/2.0 404 Not Found\r\n\r\n{}", ""),
        ["repos/acme/renamed/labels/dsf-outcome%3Aapproved"] = new(1, "HTTP/2.0 404 Not Found\r\n\r\n{}", ""),
        ["repos/acme/renamed/labels/dsf-outcome%3Arejected"] = new(1, "HTTP/2.0 404 Not Found\r\n\r\n{}", ""),
        ["repos/acme/renamed/labels/dsf-outcome%3Achanges-requested"] = new(1, "HTTP/2.0 404 Not Found\r\n\r\n{}", ""),
        ["repos/acme/renamed/actions/workflows?per_page=100&page=1"] = new(0, """{"total_count":0,"workflows":[]}""", ""),
        ["repos/acme/renamed/hooks?per_page=100&page=1"] = new(0, "[]", ""),
    };
    public List<IReadOnlyList<string>> Invocations { get; } = [];
    private readonly RecordingGitHubProvisioningClient github = new();
    private readonly RecordingAzureProvisioningClient azure = new();
    private readonly RecordingCharterRepositoryClient charter = new(null);
    private readonly RecordingAppConfigurationClient configuration = new()
    {
        OnWrite = _ => Assert.Fail("Read-only review wrote App Configuration."),
    };

    public void SetWorkflow(string source)
    {
        Responses["repos/acme/renamed/actions/workflows?per_page=100&page=1"] = new(0,
            """{"total_count":1,"workflows":[{"id":90,"path":".github/workflows/dispatch.yml","state":"active"}]}""", "");
        Responses["repos/acme/renamed/contents/.github/workflows/dispatch.yml?ref=release%2F2026"] = new(0,
            JsonSerializer.Serialize(new
            {
                type = "file",
                sha = "workflow-sha",
                encoding = "base64",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(source)),
            }), "");
    }

    public GhInvocationResult Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invocations.Add(arguments.ToArray());
        var response = Responses[arguments[^1]];
        return arguments.Contains("--include") && response.ExitCode == 0
            ? response with { StandardOutput = "HTTP/2.0 200 OK\r\n\r\n" + response.StandardOutput }
            : response;
    }

    public Task<OwnerGitHubIdentity?> ReadIdentityFromStatusAsync(string endpoint, CancellationToken cancellationToken)
    {
        OwnerReads.Add(endpoint);
        if (OwnerFailure is not null)
        {
            return Task.FromException<OwnerGitHubIdentity?>(OwnerFailure);
        }

        return Task.FromResult(OwnerIdentity);
    }

    public Task<OwnerGitHubCredentials> ReadAsync(string vault, bool includePrivateKey, CancellationToken cancellationToken)
    {
        OwnerReads.Add(vault);
        return Task.FromResult(new OwnerGitHubCredentials("7", "42", includePrivateKey ? PrivateKey : ""));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AppMethods.Add(request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        var response = AppResponses[request.RequestUri!.AbsolutePath];
        return Task.FromResult(new HttpResponseMessage(response.Status) { Content = new StringContent(response.Json) });
    }

    public async Task<(int Exit, ScriptedTerminal Terminal)> InvokeAsync(params string[] extra)
    {
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        string[] ownerArguments = extra.Contains("--owner-appconfig-endpoint") ? []
            : ["--owner-appconfig-endpoint", "", "--owner-keyvault-uri", ""];
        var exit = await InvokeCommandAsync(terminal,
            ["onboard", "decide", "review", "--repo", "acme/old-name", "--json", .. ownerArguments, .. extra],
            CancellationToken.None);
        return (exit, terminal);
    }

    public async Task<int> InvokeCommandAsync(ScriptedTerminal terminal, string[] arguments, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
        return await CliApplication.InvokeAsync(arguments,
            cancellationToken, terminal, github, azure, configuration, charter, new RecordingCharterStore(),
            new RepositoryAttachmentReviewer(this, OwnerReader ?? this, client), BoundaryAzureRunner, ReservationHttp, ReservationClock);
    }

    public void AssertReadOnly()
    {
        Assert.Empty(github.Requests);
        Assert.Empty(azure.Requests);
        Assert.Empty(charter.InitialPullRequests);
        Assert.Empty(charter.FilePullRequests);
        Assert.Empty(charter.Issues);
        Assert.All(AppMethods, method => Assert.Equal(HttpMethod.Get, method));
        Assert.All(OwnerReads, endpoint => Assert.Contains("owner.", endpoint));
        Assert.All(Invocations, invocation =>
        {
            Assert.Equal("api", invocation[0]);
            Assert.Contains("GET", invocation);
            Assert.Contains("github.com", invocation);
        });
    }

    private static string CreatePrivateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }
}
