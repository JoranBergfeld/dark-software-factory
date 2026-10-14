using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Text;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryGitHubAuthorityWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_reverification_hold_never_starts_provider_checks_or_claims_revocation(bool responseLost)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!])).Exit);
        var before = arm.Product.Writes.Count;
        if (responseLost) arm.Product.LoseResponseForKey = "dsf/factory/github-authority";
        else arm.Product.RejectKey = "dsf/factory/github-authority";

        var result = await machine.InvokeAsync("github", "--verify-only");
        var current = await machine.InvokeAsync("status");

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Contains("Hold not confirmed", result.Terminal.Output);
        Assert.Contains("no revocation asserted", result.Terminal.Output);
        Assert.Equal(responseLost ? "Partial" : "Verified", (string?)current.Result["gitHubAuthority"]!["state"]);
        Assert.Equal(before + 1, arm.Product.Writes.Count);
        Assert.Empty(transport.GrantReads);
        Assert.Equal(1, transport.Verifications);
    }

    [Fact]
    public async Task Canceled_reverification_keeps_the_confirmed_pending_hold_and_historical_proof()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        using var http = new HttpClient(transport);
        machine.Boundary.Repository.ReservationHttp = http;
        var initial = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(0, initial.Exit);
        using var cancellation = new CancellationTokenSource();
        var caller = new ReservationMachine(machine.Store) { Token = cancellation.Token };
        caller.Boundary.Repository.ReservationHttp = http;
        transport.OnGrantRead = cancellation.Cancel;

        var result = await caller.InvokeAsync("github", "--verify-only");
        var current = await machine.InvokeAsync("status");

        Assert.Equal(130, result.Exit);
        Assert.Equal("Canceled", (string?)result.Result["state"]);
        Assert.Equal("Partial", (string?)current.Result["gitHubAuthority"]!["state"]);
        Assert.True(JsonNode.DeepEquals(initial.Result["gitHubAuthority"]!["proof"], current.Result["gitHubAuthority"]!["proof"]));
        Assert.Equal(2, transport.Grants.Count);
    }

    [Theory]
    [InlineData("unchanged", 1, "Verified")]
    [InlineData("unchanged-time-format", 1, "Verified")]
    [InlineData("unchanged-version", 2, "Verified")]
    [InlineData("missing", 1, "Blocked")]
    [InlineData("missing", 2, "Blocked")]
    [InlineData("recreated", 1, "Blocked")]
    [InlineData("principal", 1, "Blocked")]
    [InlineData("principal-type", 2, "Blocked")]
    [InlineData("role", 2, "Blocked")]
    [InlineData("scope", 2, "Blocked")]
    [InlineData("condition", 1, "Blocked")]
    [InlineData("condition-version", 2, "Blocked")]
    [InlineData("creator", 1, "Blocked")]
    [InlineData("missing-incarnation", 2, "Unknown")]
    [InlineData("malformed-incarnation", 1, "Unknown")]
    [InlineData("denied", 2, "Unknown")]
    [InlineData("malformed", 1, "Unknown")]
    public async Task Reverification_requires_current_exact_grant_bindings_and_original_incarnations(string damage, int index, string state)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm)
        { Complete = true, GrantConditionVersion = damage == "unchanged-version" ? "2.0" : null };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var initial = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(0, initial.Exit);
        var grant = initial.Result["gitHubOperations"]![index]!;
        var id = (string)grant["target"]!;
        if (damage == "unchanged-version") Assert.Equal("2.0", (string?)grant["conditionVersion"]);
        if (damage == "missing") transport.RemoveResource(id);
        else if (damage is "denied" or "malformed")
            transport.GrantReplies[id] = (damage == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, "{secret-provider-detail");
        else transport.ChangeResource(id, value =>
        {
            var properties = value["properties"]!;
            if (damage == "unchanged-time-format") properties["createdOn"] = "2026-09-16T01:00:00+02:00";
            if (damage == "recreated") properties["createdOn"] = "2026-09-15T23:00:01Z";
            if (damage == "principal") properties["principalId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
            if (damage == "principal-type") properties["principalType"] = "Group";
            if (damage == "role") properties["roleDefinitionId"] = "/subscriptions/" + BoundaryPlanFixture.Subscription
                + "/providers/microsoft.authorization/roledefinitions/ffffffff-ffff-ffff-ffff-ffffffffffff";
            if (damage == "scope") properties["scope"] = "/subscriptions/" + BoundaryPlanFixture.Subscription;
            if (damage == "condition") properties["condition"] = "unapproved condition";
            if (damage == "condition-version") properties["conditionVersion"] = "2.0";
            if (damage == "creator") properties["createdBy"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
            if (damage == "missing-incarnation") properties.AsObject().Remove("createdOn");
            if (damage == "malformed-incarnation") properties["createdOn"] = "unverifiable";
        });
        var before = machine.Store.Export();

        var result = await machine.InvokeAsync("github", "--verify-only");
        var inspected = await machine.InvokeAsync("status");

        Assert.Equal(state, (string?)result.Result["gitHubAuthority"]!["state"]);
        Assert.Equal(state, (string?)inspected.Result["gitHubAuthority"]!["state"]);
        Assert.Equal(2, transport.GrantReads.Count);
        if (state != "Verified")
        {
            Assert.Contains("GET " + id, result.Terminal.Output);
            Assert.Contains("factory Azure RBAC grantor", result.Terminal.Output);
            Assert.True(JsonNode.DeepEquals(initial.Result["gitHubAuthority"]!["proof"], inspected.Result["gitHubAuthority"]!["proof"]));
            Assert.Equal(1, transport.Verifications);
        }
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + arm.Product.Export());
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(2, transport.Grants.Count);
        Assert.Equal(2, transport.Deployments.Count);
        Assert.Single(transport.Copies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reverification_holds_old_authority_when_the_final_outcome_cannot_be_persisted(bool missingGrants)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var initial = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(0, initial.Exit);
        if (missingGrants)
            foreach (var grant in initial.Result["gitHubOperations"]!.AsArray().Where(o => o!["roleScope"] is not null))
                transport.RemoveResource((string)grant!["target"]!);
        transport.OnGrantRead = () => arm.Product.RejectKey = "dsf/factory/github-authority";

        var result = await machine.InvokeAsync("github", "--verify-only");
        var current = await machine.InvokeAsync("status");

        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal("Partial", (string?)current.Result["gitHubAuthority"]!["state"]);
        Assert.Equal("host-verification", (string?)current.Result["gitHubAuthority"]!["stage"]);
        Assert.True(JsonNode.DeepEquals(initial.Result["gitHubAuthority"]!["proof"], current.Result["gitHubAuthority"]!["proof"]));
        Assert.Contains("GitHub authentication", current.Result["foundation"]!["pendingStages"]!.ToJsonString());
        Assert.Equal(2, transport.Grants.Count);
        Assert.Single(transport.Copies);
    }

    [Fact]
    public async Task Missing_current_authentication_grants_block_reverification_despite_working_deployed_access()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var initial = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(0, initial.Exit);
        var grants = initial.Result["gitHubOperations"]!.AsArray().Where(o => o!["roleScope"] is not null)
            .Select(o => (string)o!["target"]!).ToArray();
        Assert.Equal(2, grants.Length);
        foreach (var id in grants) transport.RemoveResource(id);
        var ownerBefore = machine.Store.Export();

        var result = await machine.InvokeAsync("github", "--verify-only");

        Assert.Equal("Blocked", (string?)result.Result["gitHubAuthority"]!["state"]);
        Assert.Equal(1, result.Exit);
        Assert.All(grants, id => Assert.Contains(id, transport.GrantReads));
        Assert.All(grants, id => Assert.Contains(id, result.Terminal.Output));
        Assert.Equal("Verified", (string?)result.Result["gitHubAuthority"]!["proof"]!["state"]);
        Assert.Equal("Blocked", (string?)(await machine.InvokeAsync("status")).Result["gitHubAuthority"]!["state"]);
        Assert.Single(transport.Copies);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Equal(2, transport.Deployments.Count);
        Assert.Equal(ownerBefore, machine.Store.Export());
    }

    [Fact]
    public async Task Lost_receipt_visibility_during_observation_does_not_promote_authority()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        transport.OnVerification = () => transport.HideCopyReceipt = true;
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal("Unknown", (string?)result.Result["gitHubAuthority"]!["state"]);
        Assert.Contains("GitHub authentication", result.Result["foundation"]!["pendingStages"]!.ToJsonString());
        Assert.Single(transport.Copies);
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("branch-changed")]
    [InlineData("metadata-race")]
    [InlineData("label-conflict")]
    [InlineData("wrong-id")]
    [InlineData("revoked")]
    public async Task Reverification_resolves_deployed_durable_identity_before_current_repository_review(string scenario)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!])).Exit);
        var ownerBefore = machine.Store.Export();
        transport.RepositoryName = scenario == "branch-changed" ? "acme/renamed" : "acme/renamed-again";
        transport.DefaultBranch = "release/2027";
        MoveRepository(machine.Boundary.Repository, scenario == "metadata-race" ? "acme/racing-name" : transport.RepositoryName,
            transport.DefaultBranch);
        if (scenario == "wrong-id") transport.RepositoryId = 999;
        if (scenario == "label-conflict")
            machine.Boundary.Repository.Responses["repos/" + transport.RepositoryName + "/labels/dsf%3Aproposal"] =
                new(0, """{"id":91,"name":"dsf:proposal","color":"123456","description":"Delivery assignment."}""", "");
        if (scenario == "revoked") { transport.HostDenied = true; transport.Fault = "host-repository-denied"; }
        machine.Boundary.Repository.Invocations.Clear();
        transport.OnVerification = () => Assert.Empty(machine.Boundary.Repository.Invocations);
        var refreshed = await machine.InvokeAsync("github", "--verify-only");
        Assert.Equal(scenario is "metadata-race" or "wrong-id" ? "Unknown" : scenario is "revoked" or "label-conflict" ? "Blocked" : "Verified",
            (string?)refreshed.Result["gitHubAuthority"]!["state"]);
        if (scenario is "renamed" or "branch-changed")
        {
            Assert.Equal(0, refreshed.Exit);
            Assert.Equal(transport.RepositoryName, (string?)refreshed.Result["gitHubAuthority"]!["proof"]!["repository"]);
            Assert.Equal(transport.DefaultBranch, (string?)refreshed.Result["gitHubAuthority"]!["proof"]!["defaultBranch"]);
            Assert.Contains(machine.Boundary.Repository.Invocations, i => i[^1] == "repositories/913");
        }
        if (scenario == "revoked")
        {
            Assert.Contains("GET /repositories/913", refreshed.Terminal.Output);
            Assert.Contains("GitHub installation administrator", refreshed.Terminal.Output);
        }
        Assert.DoesNotContain(machine.Boundary.Repository.Invocations, i => i[^1] == "repos/acme/renamed");
        Assert.Single(transport.Copies);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Equal(2, transport.Deployments.Count);
        Assert.Equal(ownerBefore, machine.Store.Export());
    }

    private static void MoveRepository(RepositoryReviewFixture repository, string name, string branch)
    {
        var metadata = JsonNode.Parse(repository.Responses["repos/acme/renamed"].StandardOutput)!;
        metadata["name"] = name.Split('/')[1];
        metadata["default_branch"] = branch;
        foreach (var key in repository.Responses.Keys.Where(k => k.StartsWith("repos/acme/renamed/", StringComparison.Ordinal)).ToArray())
            repository.Responses[key.Replace("repos/acme/renamed/", "repos/" + name + "/", StringComparison.Ordinal)] = repository.Responses[key];
        repository.Responses["repos/" + name] = new(0, metadata.ToJsonString(), "");
        repository.Responses["repositories/913"] = new(0, metadata.ToJsonString(), "");
        if (name != "acme/renamed")
        {
            metadata["id"] = 999;
            repository.Responses["repos/acme/renamed"] = new(0, metadata.ToJsonString(), "");
        }
        repository.AppResponses["/repos/" + name + "/installation"] = (HttpStatusCode.OK, """{"id":42}""");
    }

    [Theory]
    [InlineData("source-group", "Blocked")]
    [InlineData("metadata-group", "Verified")]
    [InlineData("metadata-direct", "Verified")]
    [InlineData("membership-unreadable", "Unknown")]
    [InlineData("role-unreadable", "Unknown")]
    [InlineData("scope-invalid", "Unknown")]
    [InlineData("scope-incomplete", "Unknown")]
    [InlineData("partial-membership", "Unknown")]
    [InlineData("unrelated-group", "Verified")]
    public async Task Effective_key_access_evidence_distinguishes_secret_metadata_and_unknown(string scenario, string state)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        var approved = preview.Result["gitHubAuthority"]!;
        var principal = scenario == "source-group" ? (string)approved["otherWorkloadPrincipalIds"]![1]!
            : scenario == "unrelated-group" ? "ffffffff-ffff-ffff-ffff-ffffffffffff" : (string)approved["codePrincipalId"]!;
        using var transport = new AuthorityWorkflowTransport(arm)
        {
            Complete = true, GroupKeyWorkload = scenario == "metadata-direct" ? null : principal,
            InheritedKeyPrincipal = scenario == "metadata-direct" ? principal : null,
            GroupPermission = scenario.StartsWith("metadata", StringComparison.Ordinal) || scenario == "scope-incomplete" ? "metadata" : "secret", Fault = scenario,
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve", (string)approved["approvalFingerprint"]!]);
        Assert.Equal(state, (string?)(result.Result["gitHubAuthority"]?["state"] ?? result.Result["state"]));
        Assert.Equal(state == "Verified" ? 0 : 1, result.Exit);
        if (state != "Verified") Assert.Empty(transport.Copies);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output);
    }

    [Fact]
    public async Task Transitive_group_secret_access_at_subscription_blocks_before_copying()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm)
        {
            Complete = true, GroupKeyWorkload = (string)preview.Result["gitHubAuthority"]!["codePrincipalId"]!,
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var before = arm.Product.Export();
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("group", (string?)result.Result["detail"], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Copies);
        Assert.Equal(before, arm.Product.Export());
    }

    [Theory]
    [InlineData("--approve", "not-the-approved-fingerprint")]
    [InlineData("--source-key-version", "ffffffffffffffffffffffffffffffff")]
    [InlineData("--runtime-image", "ghcr.io/other/image@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("--auth-audience", "ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public async Task New_preparation_inputs_cannot_masquerade_as_a_successful_retry_of_existing_authority(string option, string value)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!])).Exit);
        var before = arm.Product.Export();
        var rejected = await machine.InvokeAsync("github", option, value);
        Assert.Equal(1, rejected.Exit);
        Assert.Equal("Blocked", (string?)rejected.Result["state"]);
        Assert.Equal(before, arm.Product.Export());
        Assert.Single(transport.Copies);
        Assert.Equal(1, transport.Verifications);
    }

    [Fact]
    public async Task Deployed_governance_denial_remains_a_blocker_without_fabricating_factory_proof()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, Fault = "host-governance-denied" };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("product-governance", (string?)result.Result["detail"]);
        Assert.Contains("factory operator", (string?)result.Result["detail"]);
        Assert.Null(result.Result["gitHubAuthority"]!["proof"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_reverification_refreshes_deployed_evidence_without_reissuing_creation_effects(bool initiallyDenied)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, HostDenied = initiallyDenied };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var initial = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(initiallyDenied ? 1 : 0, initial.Exit);
        transport.HostDenied = false;
        transport.RepositoryName = "acme/renamed-again";
        MoveRepository(machine.Boundary.Repository, transport.RepositoryName, transport.DefaultBranch);
        var inspected = await machine.InvokeAsync("status");
        Assert.Equal(initiallyDenied ? "Blocked" : "Verified", (string?)inspected.Result["gitHubAuthority"]!["state"]);
        Assert.Equal(1, transport.Verifications);
        var ownerBefore = machine.Store.Export();
        var fresh = await machine.InvokeAsync("github", "--verify-only");
        Assert.Equal(0, fresh.Exit);
        Assert.Equal("acme/renamed-again", (string?)fresh.Result["gitHubAuthority"]!["proof"]!["repository"]);
        Assert.Equal(initial.Result["gitHubAuthority"]!["operationId"]!.ToJsonString(),
            fresh.Result["gitHubAuthority"]!["operationId"]!.ToJsonString());
        Assert.Single(transport.Copies);
        Assert.Equal(2, transport.Grants.Count);
        Assert.Equal(2, transport.Deployments.Count);
        Assert.Equal(2, transport.Verifications);
        Assert.Equal(ownerBefore, machine.Store.Export());
        transport.HostDenied = true;
        var denied = await machine.InvokeAsync("github", "--verify-only");
        Assert.Equal("Blocked", (string?)denied.Result["gitHubAuthority"]!["state"]);
        Assert.Contains("GitHub authentication", denied.Result["foundation"]!["pendingStages"]!.ToJsonString());
        Assert.Single(transport.Copies);
    }

    [Fact]
    public async Task Missing_Entra_API_consent_blocks_at_the_exact_deployed_operation_with_its_administrator()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        machine.Boundary.Repository.BoundaryAzureRunner = new AudienceDeniedAzure(machine.Boundary);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("Entra API administrator", (string?)result.Result["detail"]);
        Assert.Contains("/verify", (string?)result.Result["detail"]);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output);
        Assert.Equal(0, transport.Verifications);
    }

    private sealed class AudienceDeniedAzure(IAzureCliRunner inner) : IAzureCliRunner
    {
        public Task<AzureCliInvocationResult> RunAsync(IReadOnlyList<string> args, CancellationToken token) =>
            args.Any(a => a.StartsWith("api://", StringComparison.Ordinal))
                ? Task.FromResult(new AzureCliInvocationResult(1, "", "secret-provider-detail"))
                : inner.RunAsync(args, token);
    }

    [Fact]
    public async Task Cancellation_after_key_dispatch_holds_intent_without_new_grants_rollback_or_reissue()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var cancellation = new CancellationTokenSource();
        var caller = new ReservationMachine(machine.Store) { Token = cancellation.Token };
        using var transport = new AuthorityWorkflowTransport(arm) { CancelAfterCopy = cancellation };
        using var http = new HttpClient(transport);
        caller.Boundary.Repository.ReservationHttp = http;
        machine.Boundary.Repository.ReservationHttp = http;
        var ownerBefore = machine.Store.Export();
        var canceled = await caller.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(130, canceled.Exit);
        Assert.Equal("Canceled", (string?)canceled.Result["state"]);
        var observed = await machine.InvokeAsync("status");
        Assert.Equal("Unknown", (string?)observed.Result["state"]);
        Assert.Single(transport.Copies);
        Assert.Empty(transport.Grants);
        Assert.Equal(ownerBefore, machine.Store.Export());
    }

    [Fact]
    public async Task Interactive_authority_approval_discloses_risk_and_prints_the_equivalent_explicit_command()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        var fingerprint = (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!;
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var terminal = new ScriptedTerminal(new(true, true, false),
            ["https://owner.azconfig.io", "widget", BoundaryPlanFixture.Tenant, BoundaryPlanFixture.Subscription,
                SourceVault, Preparation[3], Preparation[5], Preparation[7], fingerprint]);
        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal, ["onboard", "decide", "github"]);
        Assert.Equal(0, exit);
        Assert.Equal(9, terminal.Prompts.Count);
        Assert.Contains("NOT signing-key containment", terminal.Output);
        Assert.Contains("--approve " + fingerprint, terminal.Output);
        Assert.Contains("--source-key-version " + Preparation[3], terminal.Output);
        Assert.Single(transport.Copies);
    }

    [Fact]
    public async Task Structured_authority_input_never_prompts_even_on_an_interactive_terminal()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var terminal = new ScriptedTerminal(new(true, true, false), []);
        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal, ["onboard", "decide", "github", "--json"]);
        Assert.Equal(1, exit);
        Assert.Empty(terminal.Prompts);
        Assert.Empty(machine.Boundary.Calls);
        Assert.Empty(machine.Store.Writes);
    }

    [Theory]
    [InlineData("host-missing-expiry")]
    [InlineData("host-app-permission")]
    [InlineData("host-installation-permission")]
    [InlineData("host-selection-missing")]
    [InlineData("role-wrong-scope")]
    [InlineData("key-created-invalid")]
    public async Task Malformed_deployed_scope_or_native_grant_receipt_is_never_verified(string fault)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, Fault = fault };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["isActivated"]!);
    }

    [Theory]
    [InlineData("missing-proof")]
    [InlineData("missing-expiry")]
    [InlineData("empty-operations")]
    [InlineData("unproven-key")]
    [InlineData("unaccepted-risk")]
    [InlineData("missing-copy-provenance")]
    [InlineData("wrong-role-definition")]
    [InlineData("binding-identity")]
    [InlineData("approval")]
    public async Task Status_does_not_infer_verified_authority_from_an_invalid_capability_record(string damage)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!])).Exit);
        var serialized = JsonNode.Parse(arm.Product.Export())!.AsObject();
        var key = damage == "binding-identity"
            ? serialized.First(p => p.Key.Contains("/github-bindings/", StringComparison.Ordinal) && p.Key.EndsWith("/auth", StringComparison.Ordinal)).Key
            : damage is "unproven-key" or "missing-copy-provenance"
            ? serialized.First(p => p.Key.StartsWith("dsf/factory/github-operations/", StringComparison.Ordinal) && p.Key.EndsWith("/0000", StringComparison.Ordinal)).Key
            : damage == "wrong-role-definition" ? serialized.First(p => p.Key.StartsWith("dsf/factory/github-operations/", StringComparison.Ordinal)
                && p.Key.EndsWith("/0001", StringComparison.Ordinal)).Key
            : "dsf/factory/github-authority";
        var record = JsonNode.Parse((string)serialized[key]!)!;
        var value = JsonNode.Parse((string)record["value"]!)!;
        if (damage == "missing-proof") value["proof"] = null;
        if (damage == "missing-expiry") value["proof"]!["tokenExpiresAt"] = null;
        if (damage == "empty-operations") value["operationCount"] = 0;
        if (damage == "unproven-key") value["disposition"] = "Unproven";
        if (damage == "unaccepted-risk") value["acceptedSigningRisk"] = false;
        if (damage == "missing-copy-provenance") value["providerOperationId"] = null;
        if (damage == "wrong-role-definition") value["roleDefinitionId"] = "/unapproved/4633458b-17de-408a-b874-0445c86b69e6";
        if (damage == "binding-identity") value["principalId"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        if (damage == "approval") value["approvalFingerprint"] = new string('0', 64);
        record["value"] = value.ToJsonString();
        serialized[key] = record.ToJsonString();
        var remote = new FactoryFoundationTests.FoundationTransport(machine.Store)
        { Product = ReservationTestStore.Restore(serialized.ToJsonString()) };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(remote);
        var result = await machine.InvokeAsync("status");
        Assert.Equal(1, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Empty(remote.Writes);
        Assert.Empty(remote.Product.Writes);
    }

    [Fact]
    public async Task Concurrent_approval_dispatches_once_without_claiming_the_losing_local_operation()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        var other = new ReservationMachine(machine.Store);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        using var http = new HttpClient(transport);
        machine.Boundary.Repository.ReservationHttp = http;
        other.Boundary.Repository.ReservationHttp = http;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        arm.Product.BeforePut = async (key, create) =>
        {
            if (key == "dsf/factory/manifest" && !create && Interlocked.Increment(ref arrivals) <= 2)
            {
                if (Volatile.Read(ref arrivals) == 2) barrier.TrySetResult();
                await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        string[] args = [.. Preparation, "--approve", (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!];
        var results = await Task.WhenAll(machine.InvokeAsync("github", args), other.InvokeAsync("github", args));
        var failed = Assert.Single(results, r => r.Exit != 0);
        Assert.Null(failed.Result["gitHubAuthority"]);
        Assert.Single(results, r => r.Exit == 0);
        Assert.Single(transport.Copies);
        Assert.Equal(2, transport.Grants.Count);
    }

    [Fact]
    public async Task Inherited_code_identity_key_access_blocks_before_copying_the_signing_key()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm)
        { Complete = true, InheritedKeyPrincipal = (string)preview.Result["gitHubAuthority"]!["codePrincipalId"]! };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Empty(transport.Copies);
        Assert.Empty(transport.Grants);
        Assert.Contains("inherited", (string?)result.Result["detail"]);
    }

    [Theory]
    [InlineData("copy-denied", "Failed")]
    [InlineData("grant-denied", "Blocked")]
    [InlineData("grant-lost", "Unknown")]
    [InlineData("host-lost", "Unknown")]
    public async Task Known_rejections_and_lost_effects_remain_distinct_and_never_force_retry(string fault, string state)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, Fault = fault };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        string[] args = [.. Preparation, "--approve", (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!];
        var result = await machine.InvokeAsync("github", args);
        Assert.Equal(1, result.Exit);
        Assert.Equal(state, (string?)result.Result["gitHubAuthority"]?["state"]);
        var writes = transport.Copies.Count + transport.Grants.Count + transport.Deployments.Count;
        var persisted = arm.Product.Export();
        foreach (var action in new[] { "status", "resume", "github" })
        {
            var inspected = await machine.InvokeAsync(action, action == "github" ? args : []);
            Assert.Equal(state, (string?)inspected.Result["gitHubAuthority"]?["state"]);
            Assert.Equal(persisted, arm.Product.Export());
        }
        Assert.Equal(writes, transport.Copies.Count + transport.Grants.Count + transport.Deployments.Count);
        Assert.DoesNotContain("secret-provider-detail", result.Terminal.Output + persisted);
    }

    [Fact]
    public async Task Newly_known_coding_automation_blocks_authority_preparation_without_repairing_repository()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        machine.Boundary.Repository.SetWorkflow("""
            on: [issues]
            jobs:
              dispatch:
                runs-on: ubuntu-latest
                steps:
                  - run: gh issue edit "$ISSUE" --add-assignee '@copilot'
            """);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Contains("automation", (string?)result.Result["detail"]);
        Assert.Contains("acme/platform", (string?)result.Result["detail"]);
        Assert.Empty(transport.Copies);
        machine.Boundary.Repository.AssertReadOnly();
    }

    [Fact]
    public async Task Recreated_foundation_identity_cannot_receive_an_approved_old_signing_key_grant()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, RecreatedIdentity = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["state"]);
        Assert.Empty(transport.Copies);
        Assert.Empty(transport.Grants);
    }

    [Fact]
    public async Task Deployed_GitHub_denial_reports_actual_operation_and_administrator_without_claiming_capability()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, HostDenied = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        var authority = result.Result["gitHubAuthority"]!;
        Assert.Equal("Blocked", (string?)authority["state"]);
        Assert.Equal("GET /app/installations/42", (string?)authority["proof"]?["operation"]);
        Assert.Equal("GitHub installation administrator", (string?)authority["proof"]?["responsibleAdmin"]);
        Assert.Contains("GitHub authentication", result.Result["foundation"]!["pendingStages"]!.ToJsonString());
        Assert.Equal("Blocked", (string?)(await machine.InvokeAsync("status")).Result["gitHubAuthority"]?["state"]);
    }

    [Fact]
    public async Task Known_inflight_deployments_are_polled_without_reissuing_the_key_copy_or_host()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, AsyncDeployments = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.True(result.Exit == 0, (string?)result.Result["detail"]);
        Assert.Equal(2, transport.Deployments.Count);
        Assert.Single(transport.Copies);
    }

    [Fact]
    public async Task Native_authentication_policy_is_recorded_before_dispatch_and_has_separate_creation_provenance()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.True(result.Exit == 0, (string?)result.Result["detail"]);
        var policy = Assert.Single(result.Result["gitHubOperations"]!.AsArray(),
            r => ((string)r!["target"]!).EndsWith("/authconfigs/current", StringComparison.Ordinal));
        Assert.Equal("Completed", (string?)policy!["state"]);
        Assert.Equal("Created", (string?)policy["disposition"]);
        Assert.NotNull(policy["providerOperationId"]);
        Assert.NotNull(policy["incarnation"]);
        Assert.Equal(5, result.Result["gitHubOperations"]!.AsArray().Count);
    }

    [Fact]
    public async Task Disabled_native_caller_authentication_blocks_before_any_deployed_verification()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, DisableHostAuthentication = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["gitHubAuthority"]?["state"]);
        Assert.Contains("authConfig", (string?)result.Result["detail"]);
        Assert.Equal(0, transport.Verifications);
        Assert.Equal("Blocked", (string?)(await machine.InvokeAsync("status")).Result["gitHubAuthority"]?["state"]);
    }

    [Fact]
    public async Task Native_key_version_receipt_is_creation_proof_without_invented_systemData()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true, NativeSecretMetadata = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var result = await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!]);
        Assert.True(result.Exit == 0, (string?)result.Result["detail"]);
        Assert.Contains("key-version:", result.Result["gitHubOperations"]!.ToJsonString());
    }

    [Fact]
    public async Task Approved_workflow_records_copied_version_exact_grants_and_real_host_proof_without_activation()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        var approval = (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!;
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var ownerBefore = machine.Store.Export();

        var (exit, result, terminal) = await machine.InvokeAsync("github", [.. Preparation, "--approve", approval]);

        Assert.True(exit == 0, (string?)result["detail"]);
        var authority = result["gitHubAuthority"]!;
        Assert.Equal("Verified", (string?)authority["state"]);
        Assert.True((bool)authority["acceptedSigningRisk"]!);
        Assert.EndsWith("/0123456789abcdef0123456789abcdef", (string?)authority["destinationSecretUri"]);
        Assert.Equal("acme/renamed", (string?)authority["proof"]?["repository"]);
        Assert.False((bool)result["isActivated"]!);
        Assert.Equal("Inactive", (string?)result["foundation"]?["state"]);
        Assert.DoesNotContain("GitHub authentication", result["foundation"]!["pendingStages"]!.ToJsonString());
        Assert.Equal(ownerBefore, machine.Store.Export());
        Assert.Equal(2, transport.Deployments.Count);
        Assert.Single(transport.Copies);
        var host = transport.Deployments.Last()["properties"]!["template"]!["resources"]!.AsArray();
        Assert.Single(host, r => (string?)r!["type"] == "Microsoft.App/containerApps");
        var authenticationHost = host.Single(r => (string?)r!["type"] == "Microsoft.App/containerApps")!;
        var mountedIdentity = Assert.Single(authenticationHost["identity"]!["userAssignedIdentities"]!.AsObject());
        Assert.Contains("-github", mountedIdentity.Key);
        Assert.DoesNotContain("-code", mountedIdentity.Key);
        var container = Assert.Single(authenticationHost["properties"]!["template"]!["containers"]!.AsArray())!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["dotnet","dsf-runtime.dll"]"""), container["command"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["serve-github-auth"]"""), container["args"]));
        var hostRequest = transport.Deployments.Last().ToJsonString();
        Assert.Contains("serve-github-auth", hostRequest);
        Assert.DoesNotContain("GITHUB_TOKEN", hostRequest);
        Assert.DoesNotContain("PRIVATE KEY", hostRequest);
        Assert.All(transport.Grants, grant => Assert.Equal((string?)authority["authPrincipalId"], (string?)grant["properties"]?["principalId"]));
        Assert.Equal(2, transport.Grants.Count);
        Assert.Contains(transport.Grants, g => ((string)g["properties"]!["roleDefinitionId"]!).EndsWith("4633458b-17de-408a-b874-0445c86b69e6"));
        Assert.Empty(terminal.Error);
        var current = await machine.InvokeAsync("status");
        Assert.Equal("Verified", (string?)current.Result["gitHubAuthority"]?["state"]);
        Assert.Equal(2, transport.Deployments.Count);
    }

    [Fact]
    public async Task Lost_key_copy_is_held_with_versioned_cross_subscription_provenance_and_never_reissued()
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        var approval = (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!;
        using var transport = new AuthorityWorkflowTransport(arm);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        var ownerBefore = machine.Store.Export();

        var (exit, result, terminal) = await machine.InvokeAsync("github", [.. Preparation, "--approve", approval]);

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)result["gitHubAuthority"]?["state"]);
        Assert.Single(transport.Copies);
        var parameters = transport.Copies[0]["properties"]!["parameters"]!;
        var reference = parameters["githubAppPrivateKey"]!["reference"]!;
        Assert.Equal(SourceVault, (string?)reference["keyVault"]?["id"]);
        Assert.Equal("abcdef0123456789abcdef0123456789", (string?)reference["secretVersion"]);
        Assert.Equal("github-app-private-key", (string?)reference["secretName"]);
        Assert.Null(parameters["githubAppPrivateKey"]!["value"]);
        Assert.DoesNotContain("secret-provider-detail", terminal.Output + arm.Product.Export());
        Assert.Equal(ownerBefore, machine.Store.Export());
        var persisted = arm.Product.Export();
        foreach (var action in new[] { "status", "resume", "github" })
        {
            var inspected = await machine.InvokeAsync(action, action == "github" ? [.. Preparation, "--approve", approval] : []);
            Assert.Equal("Unknown", (string?)inspected.Result["gitHubAuthority"]?["state"]);
            Assert.Equal(persisted, arm.Product.Export());
        }
        var held = await machine.InvokeAsync("github", "--verify-only");
        Assert.Equal("Unknown", (string?)held.Result["gitHubAuthority"]?["state"]);
        Assert.Equal(persisted, arm.Product.Export());
        Assert.Single(transport.Copies);
    }

    internal const string SourceVault = "/subscriptions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/resourcegroups/owner/providers/microsoft.keyvault/vaults/owner";
    internal static readonly string[] Preparation =
    [
        "--source-vault-id", SourceVault, "--source-key-version", "abcdef0123456789abcdef0123456789",
        "--runtime-image", "ghcr.io/example/dsf-runtime@sha256:" + new string('a', 64),
        "--auth-audience", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    ];

    [Fact]
    public async Task Preview_binds_exact_proven_identity_key_version_destination_and_signing_risk_without_effects()
    {
        var (machine, arm) = await FoundationAsync();
        var before = machine.Store.Export();
        var productBefore = arm.Product.Export();

        var (exit, result, terminal) = await machine.InvokeAsync("github", Preparation);

        Assert.Equal(1, exit);
        Assert.True((string?)result["state"] == "Partial", (string?)result["detail"]);
        var authority = result["gitHubAuthority"]!;
        Assert.Equal(64, ((string)authority["approvalFingerprint"]!).Length);
        Assert.False((bool)authority["acceptedSigningRisk"]!);
        Assert.Equal(SourceVault, (string?)authority["sourceVaultId"]);
        Assert.Equal("abcdef0123456789abcdef0123456789", (string?)authority["sourceSecretVersion"]);
        Assert.Contains("/vaults/df-", (string?)authority["destinationVaultId"]);
        Assert.NotEqual((string?)authority["authPrincipalId"], (string?)authority["codePrincipalId"]);
        Assert.Contains("broader", (string?)authority["detail"]);
        Assert.Contains("issue-write", (string?)authority["detail"]);
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(productBefore, arm.Product.Export());
        Assert.Equal(3, arm.Writes.Count);
        Assert.Empty(terminal.Error);
    }

    internal sealed class AuthorityWorkflowTransport(FactoryFoundationTests.FoundationTransport foundation) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker fallback = new(foundation, disposeHandler: false);
        public bool Complete { get; init; }
        public bool NativeSecretMetadata { get; init; }
        public bool DisableHostAuthentication { get; init; }
        public bool AsyncDeployments { get; init; }
        public bool HostDenied { get; set; }
        public string RepositoryName { get; set; } = "acme/renamed";
        public string DefaultBranch { get; set; } = "release/2026";
        public long RepositoryId { get; set; } = 913;
        public Action? OnVerification { get; set; }
        public bool HideCopyReceipt { get; set; }
        public bool RecreatedIdentity { get; init; }
        public string? Fault { get; set; }
        public string? InheritedKeyPrincipal { get; init; }
        public string? GroupKeyWorkload { get; init; }
        public string GroupPermission { get; init; } = "secret";
        private const string GroupId = "12345678-1234-1234-1234-123456789abc";
        private const string KeyRole = "/subscriptions/" + BoundaryPlanFixture.Subscription
            + "/providers/microsoft.authorization/roledefinitions/12345678-1234-1234-1234-123456789def";
        public CancellationTokenSource? CancelAfterCopy { get; init; }
        public int Verifications { get; private set; }
        public List<JsonNode> Copies { get; } = [];
        public List<JsonNode> Deployments { get; } = [];
        public List<JsonNode> Grants { get; } = [];
        public IReadOnlyCollection<string> ResourceIds => resources.Keys;
        public List<string> DeletedGrants { get; } = [];
        public List<string> DeletedKeyVersions { get; } = [];
        public bool CleanupDenied { get; set; }
        public List<string> GrantReads { get; } = [];
        public Action? OnGrantRead { get; set; }
        public string? GrantConditionVersion { get; init; }
        public Dictionary<string, (HttpStatusCode Status, string Body)> GrantReplies { get; } = [];
        private readonly Dictionary<string, JsonNode> resources = [];
        public void RemoveResource(string id) => Assert.True(resources.Remove(id));
        public void ChangeResource(string id, Action<JsonNode> change) => change(resources[id]);
        public void SeedResource(string id, JsonNode resource) => resources.Add(id, resource);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete
                && path.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.OrdinalIgnoreCase))
            {
                if (CleanupDenied) return new(HttpStatusCode.Forbidden);
                if (!resources.Remove(path)) return new(HttpStatusCode.NotFound);
                DeletedGrants.Add(path);
                return new(HttpStatusCode.NoContent);
            }
            if (request.RequestUri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal)
                && path == "/deletedsecrets/github-app-private-key/recover")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                DeletedKeyVersions.Clear();
                return Reply(new JsonObject { ["recovered"] = true });
            }
            if (request.RequestUri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal)
                && path.StartsWith("/secrets/github-app-private-key/", StringComparison.Ordinal))
            {
                var version = request.RequestUri.GetLeftPart(UriPartial.Path);
                if (request.Method == HttpMethod.Get)
                    return DeletedKeyVersions.Contains(version)
                        ? new(HttpStatusCode.NotFound)
                        : Reply(new JsonObject { ["id"] = Fault == "key-custody-conflict" ? version + "-changed" : version,
                            ["attributes"] = new JsonObject
                        { ["enabled"] = true, ["created"] = DateTimeOffset.Parse("2026-09-15T23:00:00Z").ToUnixTimeSeconds() } });
                Assert.Equal(HttpMethod.Delete, request.Method);
                if (CleanupDenied) return new(HttpStatusCode.Forbidden);
                DeletedKeyVersions.Add(version);
                return Reply(new JsonObject { ["recoveryId"] = version + "/deleted", ["deletedDate"] = 1789513200 });
            }
            if (request.Method == HttpMethod.Get && path.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal))
            {
                GrantReads.Add(path);
                OnGrantRead?.Invoke();
                token.ThrowIfCancellationRequested();
                if (GrantReplies.TryGetValue(path, out var reply))
                    return new(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
            }
            if (HideCopyReceipt && request.Method == HttpMethod.Get && Uri.UnescapeDataString(path).Contains("/github-operations/", StringComparison.Ordinal)
                && Uri.UnescapeDataString(path).EndsWith("/0000", StringComparison.Ordinal))
                return new(HttpStatusCode.NotFound);
            if (RecreatedIdentity && path.Contains("/userassignedidentities/", StringComparison.Ordinal))
            {
                using var original = await fallback.SendAsync(request, token);
                var changed = JsonNode.Parse(await original.Content.ReadAsStringAsync(token))!;
                changed["properties"]!["principalId"] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
                return Reply(changed);
            }
            if (request.RequestUri.Host == "github.internal.azurecontainerapps.io")
            {
                Verifications++;
                OnVerification?.Invoke();
                Assert.Equal("/verify", path);
                Assert.Equal(HttpMethod.Post, request.Method);
                if (Fault == "host-governance-denied")
                    return Reply(JsonNode.Parse("""
                        {"state":"Blocked","operation":"product-governance","responsibleAdmin":"factory operator",
                        "detail":"Current pinned inactive governance is unavailable or invalid; no GitHub access."}
                        """)!, HttpStatusCode.Forbidden);
                var authority = JsonNode.Parse((string)JsonNode.Parse(foundation.Product.ListJson())!.AsArray()
                    .Single(r => (string?)r!["key"] == "dsf/factory/github-authority")!["value"]!)!;
                var proof = new JsonObject
                {
                    ["state"] = HostDenied ? "Blocked" : "Verified",
                    ["operation"] = HostDenied ? Fault == "host-repository-denied" ? "GET /repositories/913" : "GET /app/installations/42" : "deployed-github-authentication",
                    ["responsibleAdmin"] = HostDenied ? "GitHub installation administrator" : "none",
                    ["detail"] = "Synthetic transport evidence, not live staging proof.",
                    ["factoryId"] = authority["factoryId"]!.DeepClone(), ["operationId"] = authority["operationId"]!.DeepClone(),
                    ["observedAt"] = "2026-09-15T23:00:00Z", ["appId"] = 7, ["installationId"] = 42, ["accountId"] = 81,
                    ["repositoryId"] = RepositoryId, ["repository"] = RepositoryName, ["defaultBranch"] = DefaultBranch,
                    ["installationRepositorySelection"] = "selected",
                    ["grantedPermissions"] = authority["expectedPermissions"]!.DeepClone(),
                    ["appPermissions"] = authority["expectedAppPermissions"]!.DeepClone(),
                    ["effectivePermissions"] = new JsonObject { ["metadata"] = "read", ["contents"] = "read" },
                    ["tokenExpiresAt"] = "2026-09-16T00:00:00Z",
                    ["keyVersionUri"] = authority["destinationSecretUri"]!.DeepClone(),
                    ["authPrincipalId"] = authority["authPrincipalId"]!.DeepClone(),
                };
                if (Fault == "host-missing-expiry") proof["tokenExpiresAt"] = null;
                if (Fault == "host-app-permission") proof["appPermissions"]!["administration"] = "write";
                if (Fault == "host-installation-permission") proof["grantedPermissions"]!["issues"] = "read";
                if (Fault == "host-selection-missing") proof["installationRepositorySelection"] = null;
                return Reply(proof, HostDenied ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            }
            if (resources.TryGetValue(path, out var resource) && request.Method == HttpMethod.Get) return Reply(resource);
            if (path.EndsWith("/providers/microsoft.authorization/permissions", StringComparison.OrdinalIgnoreCase))
                return Reply(JsonNode.Parse("""{"value":[{"actions":["*"],"notActions":[],"dataActions":[],"notDataActions":[]}]}""")!);
            if (path == KeyRole)
            {
                if (Fault == "role-unreadable") return new(HttpStatusCode.Forbidden);
                return Reply(new JsonObject { ["id"] = KeyRole, ["properties"] = new JsonObject
                { ["permissions"] = new JsonArray(new JsonObject
                { ["actions"] = new JsonArray(), ["notActions"] = new JsonArray(), ["notDataActions"] = new JsonArray(),
                    ["dataActions"] = new JsonArray(GroupPermission == "metadata"
                        ? "Microsoft.KeyVault/vaults/secrets/readMetadata/action" : "Microsoft.KeyVault/vaults/secrets/getSecret/action") }) } });
            }
            if ((InheritedKeyPrincipal is not null || GroupKeyWorkload is not null) && path.Contains("/vaults/", StringComparison.Ordinal)
                && path.EndsWith("/providers/microsoft.authorization/roleassignments", StringComparison.OrdinalIgnoreCase)
                && request.RequestUri.Query.Contains("atScope", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (Fault == "membership-unreadable" && query.Contains("assignedTo", StringComparison.Ordinal))
                    return new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-provider-detail") };
                if (Fault == "partial-membership" && query.Contains("assignedTo", StringComparison.Ordinal))
                    return Reply(JsonNode.Parse("""{"value":[],"nextLink":"https://management.azure.com/next"}""")!);
                // ARM resolves code MI -> security group -> containing security group, without Graph calls.
                if (query.Contains("assignedTo", StringComparison.Ordinal)
                    && !query.Contains("assignedTo('" + (GroupKeyWorkload ?? InheritedKeyPrincipal) + "')", StringComparison.Ordinal))
                    return Reply(JsonNode.Parse("""{"value":[]}""")!);
                var scope = Fault == "scope-incomplete" ? "/subscriptions"
                    : "/subscriptions/" + (Fault == "scope-invalid" ? "ffffffff-ffff-ffff-ffff-ffffffffffff" : BoundaryPlanFixture.Subscription);
                return Reply(new JsonObject { ["value"] = new JsonArray(new JsonObject
                {
                    ["id"] = scope + "/providers/microsoft.authorization/roleassignments/12345678-1234-1234-1234-123456789aaa",
                    ["properties"] = new JsonObject { ["principalId"] = GroupKeyWorkload is not null ? GroupId : InheritedKeyPrincipal,
                        ["principalType"] = GroupKeyWorkload is not null ? "Group" : "ServicePrincipal", ["scope"] = scope,
                        ["roleDefinitionId"] = KeyRole, ["condition"] = null },
                }) });
            }
            if (path.EndsWith("/providers/microsoft.authorization/roleassignments", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/providers/microsoft.authorization/denyassignments", StringComparison.OrdinalIgnoreCase))
                return Reply(JsonNode.Parse("""{"value":[]}""")!);
            if (path.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
                var grant = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                Grants.Add(grant.DeepClone());
                if (Fault == "grant-lost") throw new HttpRequestException("secret-provider-detail");
                if (Fault == "grant-denied") return new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-provider-detail") };
                grant["id"] = path;
                grant["properties"]!["createdOn"] = "2026-09-15T23:00:00Z";
                grant["properties"]!["createdBy"] = BoundaryPlanFixture.Principal;
                grant["properties"]!["conditionVersion"] = GrantConditionVersion;
                grant["properties"]!["scope"] = Fault == "role-wrong-scope" ? "/subscriptions/unapproved"
                    : path.Split("/providers/microsoft.authorization/roleassignments/")[0];
                resources[path] = grant;
                return Reply(grant, HttpStatusCode.Created);
            }
            if (request.RequestUri.Host == "management.azure.com" && path == FactoryGitHubAuthorityWorkflowTests.SourceVault)
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject
                    {
                        ["id"] = path, ["properties"] = new JsonObject
                        {
                            ["vaultUri"] = "https://owner.vault.azure.net/",
                            ["tenantId"] = BoundaryPlanFixture.Tenant, ["enabledForTemplateDeployment"] = true,
                        },
                    }.ToJsonString(), Encoding.UTF8, "application/json"),
                };
            if (path.Contains("/deployments/gh-", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                Deployments.Add(body);
                if (body["properties"]?["parameters"]?["githubAppPrivateKey"] is not null)
                {
                    Copies.Add(body);
                    CancelAfterCopy?.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                if (Fault == "copy-denied" && path.EndsWith("-key", StringComparison.Ordinal))
                    return new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-provider-detail") };
                if (Fault == "host-lost" && path.EndsWith("-host", StringComparison.Ordinal)) throw new HttpRequestException("secret-provider-detail");
                var records = JsonNode.Parse(foundation.Product.ListJson())!.AsArray()
                    .Select(v => JsonNode.Parse((string)v!["value"]!)!).ToArray();
                Assert.Contains(records, r => (string?)r["kind"] == "decide-github-operation"
                    && (string?)r["target"] == path && (string?)r["state"] == "Intent");
                if (!Complete) throw new HttpRequestException("secret-provider-detail");
                var target = records.Single(r => (string?)r["kind"] == "decide-github-operation" && (string?)r["target"] == path);
                var resourceId = (string)target["resourceId"]!;
                var isKey = resourceId.EndsWith("/secrets/github-app-private-key", StringComparison.Ordinal);
                resources[resourceId] = new JsonObject
                {
                    ["id"] = resourceId, ["systemData"] = new JsonObject
                    { ["createdAt"] = "2026-09-15T23:00:00Z", ["lastModifiedAt"] = "2026-09-15T23:00:00Z" },
                    ["properties"] = isKey ? new JsonObject
                    {
                        ["secretUriWithVersion"] = "https://" + resourceId.Split("/vaults/")[1].Split('/')[0]
                            + ".vault.azure.net/secrets/github-app-private-key/0123456789abcdef0123456789abcdef",
                    } : new JsonObject
                    {
                        ["provisioningState"] = "Succeeded",
                        ["configuration"] = new JsonObject { ["ingress"] = new JsonObject { ["fqdn"] = "github.internal.azurecontainerapps.io" } },
                    },
                };
                if (!isKey)
                {
                    var authority = records.Single(r => r["authPrincipalId"] is not null);
                    resources[resourceId + "/authconfigs/current"] = new JsonObject
                    {
                        ["id"] = resourceId + "/authconfigs/current",
                        ["systemData"] = new JsonObject { ["createdAt"] = "2026-09-15T23:00:00Z", ["lastModifiedAt"] = "2026-09-15T23:00:00Z" },
                        ["properties"] = new JsonObject
                        {
                            ["platform"] = new JsonObject { ["enabled"] = !DisableHostAuthentication },
                            ["globalValidation"] = new JsonObject { ["unauthenticatedClientAction"] = "Return401", ["excludedPaths"] = new JsonArray() },
                            ["httpSettings"] = new JsonObject { ["requireHttps"] = true },
                            ["login"] = new JsonObject { ["tokenStore"] = new JsonObject { ["enabled"] = false } },
                            ["identityProviders"] = new JsonObject { ["azureActiveDirectory"] = new JsonObject
                            {
                                ["enabled"] = true,
                                ["registration"] = new JsonObject
                                {
                                    ["clientId"] = authority["audienceClientId"]!.DeepClone(),
                                    ["openIdIssuer"] = "https://login.microsoftonline.com/" + BoundaryPlanFixture.Tenant + "/v2.0",
                                },
                                ["validation"] = new JsonObject
                                {
                                    ["allowedAudiences"] = new JsonArray((string)authority["audienceClientId"]!, "api://" + (string)authority["audienceClientId"]!),
                                    ["defaultAuthorizationPolicy"] = new JsonObject { ["allowedPrincipals"] = new JsonObject
                                    { ["identities"] = new JsonArray(BoundaryPlanFixture.Principal, (string)authority["codePrincipalId"]!) } },
                                },
                            } },
                        },
                    };
                }
                if (isKey)
                {
                    if (NativeSecretMetadata) resources[resourceId].AsObject().Remove("systemData");
                    resources[resourceId]["properties"]!["attributes"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["created"] = DateTimeOffset.Parse("2026-09-15T23:00:00Z").ToUnixTimeSeconds(),
                        ["updated"] = DateTimeOffset.Parse("2026-09-15T23:00:00Z").ToUnixTimeSeconds(),
                    };
                    if (Fault == "key-created-invalid") resources[resourceId]["properties"]!["attributes"]!["created"] = long.MaxValue;
                }
                resources[path + "/operations"] = new JsonObject { ["value"] = new JsonArray(new JsonObject
                {
                    ["operationId"] = "ABC1", ["properties"] = new JsonObject
                    {
                        ["targetResource"] = new JsonObject { ["id"] = resourceId },
                        ["provisioningOperation"] = "Create", ["provisioningState"] = "Succeeded", ["statusCode"] = "OK",
                        ["trackingId"] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                    },
                }) };
                if (!isKey)
                {
                    var policyReceipt = resources[path + "/operations"]["value"]![0]!.DeepClone();
                    policyReceipt["operationId"] = "ABC2";
                    policyReceipt["properties"]!["targetResource"]!["id"] = resourceId + "/authconfigs/current";
                    resources[path + "/operations"]["value"]!.AsArray().Add(policyReceipt);
                }
                var receipt = new JsonObject { ["id"] = path, ["properties"] = new JsonObject
                { ["provisioningState"] = "Succeeded", ["correlationId"] = "dddddddd-dddd-dddd-dddd-dddddddddddd" } };
                resources[path] = receipt;
                if (AsyncDeployments)
                {
                    var pending = receipt.DeepClone();
                    pending["properties"]!["provisioningState"] = "Running";
                    return Reply(pending, HttpStatusCode.Created);
                }
                return Reply(receipt, HttpStatusCode.Created);
            }
            return await fallback.SendAsync(request, token);
        }

        private static HttpResponseMessage Reply(JsonNode body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            response.Headers.Add("x-ms-request-id", "cccccccc-cccc-cccc-cccc-cccccccccccc");
            response.Headers.Date = DateTimeOffset.Parse("2026-09-15T23:00:00Z");
            return response;
        }
    }

    internal static async Task<(ReservationMachine Machine, FactoryFoundationTests.FoundationTransport Arm)> FoundationAsync()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FactoryFoundationTests.FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        var result = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);
        Assert.True(result.Exit == 0, result.Terminal.Output);
        return (machine, arm);
    }

    [Fact]
    public async Task GitHub_authority_requires_completed_foundation_without_adding_owner_or_target_effects()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var review = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)review.Plan["fingerprint"]!);
        var before = machine.Store.Export();

        var (exit, result, terminal) = await machine.InvokeAsync("github");

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)result["state"]);
        Assert.Contains("completed", (string?)result["detail"]);
        Assert.Empty(terminal.Error);
        Assert.Equal(before, machine.Store.Export());
        machine.Boundary.AssertReadOnly();
    }
}
