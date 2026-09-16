using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryGitHubAuthorityWorkflowTests
{
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
    public async Task Status_does_not_infer_verified_authority_from_an_invalid_capability_record(string damage)
    {
        var (machine, arm) = await FoundationAsync();
        var preview = await machine.InvokeAsync("github", Preparation);
        using var transport = new AuthorityWorkflowTransport(arm) { Complete = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(transport);
        Assert.Equal(0, (await machine.InvokeAsync("github", [.. Preparation, "--approve",
            (string)preview.Result["gitHubAuthority"]!["approvalFingerprint"]!])).Exit);
        var serialized = JsonNode.Parse(arm.Product.Export())!.AsObject();
        var key = damage is "unproven-key" or "missing-copy-provenance"
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
        public bool RecreatedIdentity { get; init; }
        public string? Fault { get; init; }
        public string? InheritedKeyPrincipal { get; init; }
        public CancellationTokenSource? CancelAfterCopy { get; init; }
        public int Verifications { get; private set; }
        public List<JsonNode> Copies { get; } = [];
        public List<JsonNode> Deployments { get; } = [];
        public List<JsonNode> Grants { get; } = [];
        private readonly Dictionary<string, JsonNode> resources = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
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
                    ["operation"] = HostDenied ? "GET /app/installations/42" : "deployed-github-authentication",
                    ["responsibleAdmin"] = HostDenied ? "GitHub installation administrator" : "none",
                    ["detail"] = "Synthetic transport evidence, not live staging proof.",
                    ["factoryId"] = authority["factoryId"]!.DeepClone(), ["operationId"] = authority["operationId"]!.DeepClone(),
                    ["observedAt"] = "2026-09-15T23:00:00Z", ["appId"] = 7, ["installationId"] = 42, ["accountId"] = 81,
                    ["repositoryId"] = 913, ["repository"] = RepositoryName, ["defaultBranch"] = "trunk",
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
            if (InheritedKeyPrincipal is not null && path.Contains("/vaults/", StringComparison.Ordinal)
                && path.EndsWith("/providers/microsoft.authorization/roleassignments", StringComparison.OrdinalIgnoreCase)
                && request.RequestUri.Query.Contains("atScope", StringComparison.Ordinal))
                return Reply(new JsonObject { ["value"] = new JsonArray(new JsonObject
                { ["properties"] = new JsonObject { ["principalId"] = InheritedKeyPrincipal } }) });
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
