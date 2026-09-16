using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryFoundationTests
{
    [Fact]
    public async Task Concurrent_provisioners_dispatch_once_and_do_not_claim_rejected_local_operation_ids()
    {
        var owner = new ReservationTestStore();
        var machine = new ReservationMachine(owner);
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        owner.BeforePut = async (key, create) =>
        {
            if (key == "dsf/onboarding/products/widget" && !create && Interlocked.Increment(ref arrivals) <= 2)
            {
                if (Volatile.Read(ref arrivals) == 2) barrier.TrySetResult();
                await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        };
        var first = new ReservationMachine(owner);
        var second = new ReservationMachine(owner);
        var firstArm = new FoundationTransport(owner);
        var secondArm = new FoundationTransport(owner);
        first.Boundary.Repository.ReservationHttp = new HttpClient(firstArm);
        second.Boundary.Repository.ReservationHttp = new HttpClient(secondArm);

        var outcomes = await Task.WhenAll(first.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint),
            second.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint));

        Assert.Equal(1, firstArm.Writes.Count + secondArm.Writes.Count);
        var loser = Assert.Single(outcomes, o => (string?)o.Result["state"] == "Blocked");
        Assert.Null(loser.Result["foundation"]);
        Assert.All(outcomes, o => Assert.Equal(1, o.Exit));
    }

    [Fact]
    public async Task Failed_deployment_keeps_confirmed_resource_receipts_instead_of_erasing_partial_progress()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, DeploymentFails = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Equal("Failed", (string?)result["foundation"]?["state"]);
        var operations = result["foundation"]!["operations"]!.AsArray();
        Assert.Contains(operations, o => ((string)o!["target"]!).Contains("/vaults/", StringComparison.Ordinal) && (string?)o["state"] == "Completed");
        Assert.Contains(operations, o => ((string)o!["target"]!).Contains("/components/", StringComparison.Ordinal) && (string?)o["state"] == "Failed");
        Assert.Equal(3, arm.Writes.Count);
        Assert.Empty(terminal.Error);
        Assert.False((bool)result["foundation"]!["isBaseProvisioned"]!);
    }

    [Fact]
    public async Task Another_authorized_reader_can_inspect_without_impersonating_the_original_operator()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        Assert.Equal(0, (await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        observer.Boundary.AppConfigActor = "99999999-9999-9999-9999-999999999999";
        observer.Boundary.Repository.ReservationHttp = new HttpClient(new FoundationTransport(observer.Store) { Product = arm.Product });

        var (exit, result, _) = await observer.InvokeAsync("status");

        Assert.Equal(0, exit);
        Assert.Equal(BoundaryPlanFixture.Principal, (string?)result["identity"]!["azureActorId"]);
        Assert.False((bool)result["hasRegistryEffects"]!);
        Assert.Empty(observer.Store.Writes);
    }

    [Fact]
    public async Task Cosmos_foundation_contains_owned_runtime_state_containers_without_account_wide_grants()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true, CosmosNativeReceipts = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(0, exit);
        var deployment = JsonNode.Parse(arm.Writes.Last())!;
        var resources = deployment["properties"]!["template"]!["resources"]!.AsArray();
        Assert.Contains(resources, r => (string?)r!["type"] == "Microsoft.DocumentDB/databaseAccounts/sqlDatabases");
        var containers = resources.Where(r => (string?)r!["type"] == "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers").ToArray();
        Assert.Contains(containers, c => (string?)c!["properties"]?["resource"]?["partitionKey"]?["paths"]?[0] == "/product");
        Assert.Contains(containers, c => (string?)c!["properties"]?["resource"]?["partitionKey"]?["paths"]?[0] == "/id");
        Assert.Contains(result["foundation"]!["operations"]!.AsArray(),
            o => ((string)o!["target"]!).EndsWith("/sqldatabases/widget/containers/runs", StringComparison.Ordinal));
        Assert.DoesNotContain("sqlRoleAssignments", deployment.ToJsonString());
    }

    [Fact]
    public async Task Interactive_approval_prints_the_equivalent_explicit_command_without_activating()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        var terminal = new ScriptedTerminal(new(true, false, false), [fingerprint]);

        var exit = await machine.Boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", "provision", "--owner-appconfig-endpoint", "https://owner.azconfig.io",
                "--product", "widget", "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription]);

        Assert.Equal(0, exit);
        Assert.Contains("dsf onboard decide provision", terminal.Output);
        Assert.Contains("--reviewed-fingerprint " + fingerprint, terminal.Output);
        Assert.Contains("\"isActivated\": false", terminal.Output);
        Assert.Empty(terminal.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Product_publication_failure_preserves_owned_resources_and_never_dispatches_the_base(bool lost)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var product = new ReservationTestStore
        {
            RejectKey = lost ? null : "dsf/factory/profile",
            LoseResponseForKey = lost ? "dsf/factory/profile" : null,
        };
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, Product = product };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Equal(lost ? "Unknown" : "Failed", (string?)result["foundation"]?["state"]);
        Assert.Equal(lost ? "Unknown" : "Failed", (string?)result["foundation"]!["operations"]!.AsArray().Last()!["state"]);
        Assert.Equal(2, arm.Writes.Count);
        Assert.Empty(terminal.Error);
        Assert.DoesNotContain("secret-provider-detail", terminal.Output);
        Assert.Equal(lost ? 1 : 0, JsonNode.Parse(product.ListJson())!.AsArray().Count);
        var before = machine.Store.Export();
        await machine.InvokeAsync("resume");
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(2, arm.Writes.Count);
    }

    [Fact]
    public async Task Resource_providers_returning_OK_require_fresh_creation_incarnations_not_just_names()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, ResourceStatusCode = "OK" };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(0, exit);
        Assert.Equal("Inactive", (string?)result["foundation"]?["state"]);
    }

    [Theory]
    [InlineData("malformed-json")]
    [InlineData("invalid-date")]
    [InlineData("invalid-request-id")]
    [InlineData("updated-not-created")]
    [InlineData("read-timeout")]
    public async Task Ambiguous_provider_results_are_redacted_unknown_not_retryable_success(string damage)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true, CreateDamage = damage };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        Assert.DoesNotContain("secret-provider-detail", terminal.Output + machine.Store.Export());
        Assert.True(arm.Writes.Count <= 2);
        if (damage is "malformed-json" or "invalid-date")
            Assert.Equal(201, (int?)result["foundation"]!["operations"]!.AsArray().Last()!["httpStatus"]);
    }

    [Fact]
    public async Task Every_dispatch_uses_the_client_correlation_recorded_before_the_request()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, EnforceCorrelation = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, _, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(0, exit);
        Assert.True(arm.CorrelationVerified > 3);
    }

    [Fact]
    public async Task A_completed_foundation_does_not_turn_wrong_provision_approval_into_success()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        Assert.Equal(0, (await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var before = machine.Store.Export();

        var (exit, result, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", new string('0', 64));

        Assert.Equal(1, exit);
        Assert.Equal("Blocked", (string?)result["state"]);
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(3, arm.Writes.Count);
    }

    [Fact]
    public async Task Provision_does_not_resume_incomplete_admission_as_a_hidden_side_effect()
    {
        var machine = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" });
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        Assert.Equal(1, (await machine.ReserveAsync(fingerprint)).Exit);
        var before = machine.Store.Export();
        var arm = new FoundationTransport(machine.Store);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, _, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Empty(arm.Writes);
        Assert.Equal(before, machine.Store.Export());
    }

    [Fact]
    public async Task Hosting_platform_group_is_declared_before_deployment_and_bound_to_the_created_environment()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(0, exit);
        var managed = Assert.Single(result["foundation"]!["operations"]!.AsArray(),
            operation => ((string)operation!["target"]!).EndsWith("-hosting-infra", StringComparison.Ordinal));
        Assert.Contains("/managedenvironments/", (string?)managed!["managedBy"]);
        Assert.Equal("Completed", (string?)managed["state"]);
        Assert.NotNull(managed["providerCorrelationId"]);
        Assert.NotNull(managed["incarnation"]);
        Assert.Equal(3, arm.Writes.Count);
    }

    [Theory]
    [InlineData("old-incarnation")]
    [InlineData("human-modified")]
    [InlineData("duplicate-principal")]
    [InlineData("invalid-tracking-id")]
    public async Task Unexpected_resource_incarnations_and_identity_bindings_never_become_completed_foundation(string damage)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, ResourceDamage = damage };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        Assert.Equal(3, arm.Writes.Count);
        Assert.Empty(terminal.Error);
        Assert.False((bool)result["foundation"]!["isBaseProvisioned"]!);
        Assert.False((bool)result["isActivated"]!);
        Assert.DoesNotContain("secret-provider-detail", terminal.Output + machine.Store.Export());
    }

    [Theory]
    [InlineData("missing-operations")]
    [InlineData("invalid-stage")]
    [InlineData("invalid-request-id")]
    [InlineData("lost-failed-progress")]
    public async Task Corrupt_foundation_progress_never_reports_base_completion(string damage)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true, DeploymentFails = damage == "lost-failed-progress" };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        Assert.Equal(damage == "lost-failed-progress" ? 1 : 0, (await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var records = JsonNode.Parse(machine.Store.Export())!.AsObject();
        var key = damage == "invalid-request-id"
            ? records.First(p => p.Key.StartsWith("dsf/foundation/", StringComparison.Ordinal)).Key
            : "dsf/onboarding/products/widget";
        var record = JsonNode.Parse((string)records[key]!)!;
        var value = JsonNode.Parse((string)record["value"]!)!;
        if (damage == "missing-operations") value["foundation"]!["operationCount"] = 0;
        if (damage == "invalid-stage") value["foundation"]!["stage"] = "made-up";
        if (damage == "invalid-request-id") value["clientRequestId"] = Guid.Empty.ToString();
        record["value"] = value.ToJsonString();
        records[key] = record.ToJsonString();
        if (damage == "lost-failed-progress")
            records.Remove(records.Select(p => p.Key).Where(k => k.StartsWith("dsf/foundation/", StringComparison.Ordinal)).OrderDescending().First());
        var observer = new ReservationMachine(ReservationTestStore.Restore(records.ToJsonString()));
        observer.Boundary.Repository.ReservationHttp = new HttpClient(new FoundationTransport(observer.Store) { Product = arm.Product });

        var (exit, result, _) = await observer.InvokeAsync("status");

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        Assert.False((bool)result["foundation"]!["isBaseProvisioned"]!);
        Assert.Empty(observer.Store.Writes);
    }

    [Fact]
    public async Task Product_manifest_cannot_rehash_unreviewed_plan_bytes_into_authority()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        Assert.Equal(0, (await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        var records = JsonNode.Parse(arm.Product.Export())!.AsObject();
        var chunks = records.Where(r => r.Key.StartsWith("dsf/factory/reviewed-plan/", StringComparison.Ordinal)).OrderBy(r => r.Key).ToArray();
        var bytes = chunks.SelectMany(r => Convert.FromBase64String((string)JsonNode.Parse((string)JsonNode.Parse((string)r.Value!)!["value"]!)!["data"]!)).ToArray();
        bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("swedencentral", "southcentralus", StringComparison.Ordinal));
        for (var i = 0; i < chunks.Length; i++)
        {
            var record = JsonNode.Parse((string)chunks[i].Value!)!;
            var chunk = JsonNode.Parse((string)record["value"]!)!;
            chunk["data"] = Convert.ToBase64String(bytes.AsSpan(i * 4000, Math.Min(4000, bytes.Length - i * 4000)));
            record["value"] = chunk.ToJsonString();
            records[chunks[i].Key] = record.ToJsonString();
        }
        var manifestRecord = JsonNode.Parse((string)records["dsf/factory/manifest"]!)!;
        var manifest = JsonNode.Parse((string)manifestRecord["value"]!)!;
        manifest["planBytes"] = bytes.Length;
        manifest["reviewedPlanSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        manifestRecord["value"] = manifest.ToJsonString();
        records["dsf/factory/manifest"] = manifestRecord.ToJsonString();
        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        observer.Boundary.Repository.ReservationHttp = new HttpClient(new FoundationTransport(observer.Store)
        { Product = ReservationTestStore.Restore(records.ToJsonString()) });

        var (exit, result, _) = await observer.InvokeAsync("status");

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        Assert.Null(result["plan"]);
        Assert.Empty(observer.Store.Writes);
    }

    [Fact]
    public async Task Accepted_asynchronous_creation_is_observed_to_completion_without_reissuing_the_write()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true, AsyncConfiguration = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(0, exit);
        Assert.Equal("Inactive", (string?)result["foundation"]?["state"]);
        Assert.Equal(3, arm.Writes.Count);
        Assert.Equal(1, arm.ConfigurationPolls);
        var configuration = result["foundation"]!["operations"]!.AsArray()
            .Single(o => ((string)o!["target"]!).Contains("/configurationstores/", StringComparison.Ordinal));
        Assert.Equal("Completed", (string?)configuration!["state"]);
        Assert.NotNull(configuration["incarnation"]);
    }

    [Theory]
    [InlineData("cancel", "Canceled", 130)]
    [InlineData("existing", "Blocked", 1)]
    [InlineData("forbidden", "Failed", 1)]
    public async Task Foundation_cancellation_and_provider_rejections_preserve_identity_and_close_dispatch(string failure, string expected, int expectedExit)
    {
        using var cancellation = new CancellationTokenSource();
        var machine = new ReservationMachine(new ReservationTestStore()) { Token = cancellation.Token };
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        var (_, reserved, _) = await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        {
            CancelOnPut = failure == "cancel" ? cancellation : null,
            ExistingTarget = failure == "existing",
            RejectPut = failure == "forbidden",
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(expectedExit, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal(expected, (string?)result["foundation"]?["state"]);
        Assert.True(JsonNode.DeepEquals(reserved["identity"], result["identity"]));
        Assert.Equal(failure == "existing" ? 0 : 1, arm.Writes.Count);
        if (failure == "cancel")
            Assert.Equal("Unknown", (string?)Assert.Single(result["foundation"]!["operations"]!.AsArray())!["state"]);
        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        var remote = new FoundationTransport(observer.Store);
        observer.Boundary.Repository.ReservationHttp = new HttpClient(remote);
        var (statusExit, status, _) = await observer.InvokeAsync("resume");
        Assert.Equal(1, statusExit);
        Assert.Equal(failure == "cancel" ? "Unknown" : expected, (string?)status["foundation"]?["state"]);
        Assert.Empty(remote.Writes);
        Assert.Empty(observer.Store.Writes);
        machine.Boundary.AssertReadOnly();
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("profile")]
    [InlineData("reviewed-plan/0000")]
    public async Task Status_and_resume_never_substitute_owner_archive_for_missing_product_authority(string missing)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);
        var (exit, _, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);
        Assert.Equal(0, exit);
        var damaged = JsonNode.Parse(arm.Product.Export())!.AsObject();
        Assert.True(damaged.Remove("dsf/factory/" + missing));
        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        var remote = new FoundationTransport(observer.Store) { Product = ReservationTestStore.Restore(damaged.ToJsonString()) };
        observer.Boundary.Repository.ReservationHttp = new HttpClient(remote);

        foreach (var action in new[] { "status", "resume" })
        {
            var (statusExit, status, terminal) = await observer.InvokeAsync(action);
            Assert.Equal(1, statusExit);
            Assert.Equal("Unknown", (string?)status["foundation"]?["state"]);
            Assert.Empty(terminal.Error);
            Assert.False((bool)status["foundation"]!["isBaseProvisioned"]!);
        }
        Assert.Empty(observer.Store.Writes);
        Assert.Empty(remote.Product.Writes);
        Assert.Empty(remote.Writes);
    }

    [Fact]
    public async Task Inactive_foundation_deploys_only_reviewed_factory_base_with_distinct_ungranted_identities()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true, CompleteDeployment = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.True(exit == 0, terminal.Output + terminal.Error);
        Assert.Equal("Inactive", (string?)result["foundation"]?["state"]);
        Assert.True((bool)result["foundation"]!["isBaseProvisioned"]!);
        Assert.True((bool)result["hasRegistryEffects"]!);
        Assert.False((bool)result["isActivated"]!);
        Assert.Contains("activation", result["foundation"]!["pendingStages"]!.ToJsonString());
        var deployment = JsonNode.Parse(arm.Writes.Last())!;
        var template = deployment["properties"]!["template"]!;
        var resources = template["resources"]!.AsArray();
        Assert.Contains(resources, r => (string?)r!["type"] == "Microsoft.KeyVault/vaults");
        Assert.Contains(resources, r => (string?)r!["type"] == "Microsoft.DocumentDB/databaseAccounts");
        Assert.Contains(resources, r => (string?)r!["type"] == "Microsoft.OperationalInsights/workspaces");
        Assert.Contains(resources, r => (string?)r!["type"] == "Microsoft.Network/virtualNetworks");
        Assert.Contains(resources, r => (string?)r!["type"] == "Microsoft.App/managedEnvironments");
        Assert.DoesNotContain(resources, r => ((string)r!["type"]!).Contains("roleAssignments", StringComparison.OrdinalIgnoreCase)
            || ((string)r!["type"]!).Contains("containerApps", StringComparison.OrdinalIgnoreCase));
        var operations = result["foundation"]!["operations"]!.AsArray();
        var identities = operations.Where(o => (string?)o!["principalId"] is not null).ToArray();
        Assert.Equal(4, identities.Length);
        Assert.Equal(4, identities.Select(i => (string)i!["principalId"]!).Distinct().Count());
        Assert.All(operations, operation => Assert.Equal("Completed", (string?)operation!["state"]));
        Assert.All(operations, operation => Assert.NotNull(operation!["requestSha256"]));
        machine.Boundary.AssertReadOnly();
    }

    [Fact]
    public async Task Created_product_configuration_becomes_authority_before_any_base_deployment()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        var (_, reserved, _) = await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store) { CompleteCreates = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (_, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Empty(terminal.Error);
        Assert.Equal("https://dsfo-" + Guid.Parse((string)reserved["factoryId"]!).ToString("N") + ".azconfig.io",
            (string?)result["foundation"]?["productEndpoint"]);
        var records = JsonNode.Parse(arm.Product.ListJson())!.AsArray();
        var manifest = JsonNode.Parse((string)records.Single(r => (string?)r!["key"] == "dsf/factory/manifest")!["value"]!)!;
        Assert.Equal("decide-factory-manifest", (string?)manifest["kind"]);
        Assert.Equal("Inactive", (string?)manifest["publication"]);
        Assert.Equal(fingerprint, (string?)manifest["identity"]?["planFingerprint"]);
        Assert.Equal("product-app-configuration", (string?)manifest["authority"]);
        var profile = JsonNode.Parse((string)records.Single(r => (string?)r!["key"] == "dsf/factory/profile")!["value"]!)!;
        Assert.Equal("onboard-decide-inactive-v1", (string?)profile["profile"]);
        Assert.Empty(profile["allowedRepositoryMutations"]!.AsArray());
        Assert.DoesNotContain("GITHUB_APP_PRIVATE_KEY", arm.Product.Export());
        Assert.All(arm.Product.Writes, write => Assert.Equal("*", write.IfNoneMatch));
        Assert.False((bool)result["isActivated"]!);
        machine.Boundary.AssertReadOnly();
    }

    [Fact]
    public async Task Approved_foundation_records_creation_intent_before_ARM_and_holds_lost_response_without_reissue()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        var (_, reserved, _) = await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store);
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        Assert.Single(arm.Writes);
        var operation = result["foundation"]!["operations"]!.AsArray().Single()!;
        Assert.Equal(BoundaryPlanFixture.Root.ToLowerInvariant() + "/resourcegroups/widget-decide", (string?)operation["target"]);
        Assert.Equal("Unknown", (string?)operation["state"]);
        Assert.Equal("CreateRequested", (string?)operation["disposition"]);
        Assert.NotNull(operation["requestSha256"]);
        Assert.NotNull(operation["clientRequestId"]);
        Assert.True(JsonNode.DeepEquals(reserved["identity"], result["identity"]));
        Assert.DoesNotContain("secret-provider-detail", terminal.Output);
        var before = machine.Store.Export();

        var (_, resumed, _) = await machine.InvokeAsync("resume");
        var (_, repeated, _) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);
        Assert.Equal("Unknown", (string?)resumed["foundation"]?["state"]);
        Assert.Equal("Unknown", (string?)repeated["foundation"]?["state"]);
        Assert.Single(arm.Writes);
        Assert.Equal(before, machine.Store.Export());
        machine.Boundary.AssertReadOnly();
    }

    [Fact]
    public async Task Provision_requires_separate_exact_approval_not_reservation_consent()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var (reservedExit, _, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal(0, reservedExit);
        var before = machine.Store.Export();

        var (exit, result, terminal) = await machine.InvokeAsync("provision");

        Assert.Equal(1, exit);
        Assert.Equal("Failed", (string?)result["state"]);
        Assert.Contains("--reviewed-fingerprint", (string?)result["detail"]);
        Assert.Empty(terminal.Error);
        Assert.Equal(before, machine.Store.Export());
        machine.Boundary.AssertReadOnly();
    }

    internal sealed class FoundationTransport(ReservationTestStore owner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker ownerClient = new(owner.Client());
        public ReservationTestStore Product { get; init; } = new();
        public bool CompleteCreates { get; init; }
        public bool CompleteDeployment { get; init; }
        public CancellationTokenSource? CancelOnPut { get; init; }
        public bool ExistingTarget { get; init; }
        public bool RejectPut { get; init; }
        public bool AsyncConfiguration { get; init; }
        public int ConfigurationPolls { get; private set; }
        public string? ResourceDamage { get; init; }
        public bool EnforceCorrelation { get; init; }
        public int CorrelationVerified { get; private set; }
        public string? CreateDamage { get; init; }
        public string ResourceStatusCode { get; init; } = "Created";
        public bool CosmosNativeReceipts { get; init; }
        public bool DeploymentFails { get; init; }
        private readonly Dictionary<string, JsonNode> created = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Writes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (EnforceCorrelation && request.Method == HttpMethod.Put && request.RequestUri!.Host != "owner.azconfig.io")
            {
                var target = request.RequestUri.Host == "management.azure.com"
                    ? request.RequestUri.AbsolutePath.ToLowerInvariant()
                    : request.RequestUri.GetLeftPart(UriPartial.Authority) + "/kv/"
                        + Uri.EscapeDataString(Uri.UnescapeDataString(request.RequestUri.AbsolutePath["/kv/".Length..]));
                var intents = JsonNode.Parse(owner.ListJson())!.AsArray().Select(r => JsonNode.Parse((string)r!["value"]!)!)
                    .Where(r => (string?)r["kind"] == "decide-foundation-operation"
                        && ((string?)r["target"] == target || (string?)r["deploymentTarget"] == target)).ToArray();
                Assert.NotEmpty(intents);
                Assert.All(intents, intent => Assert.Equal((string?)intent["clientRequestId"],
                    request.Headers.GetValues("x-ms-client-request-id").Single()));
                CorrelationVerified++;
            }
            if (request.RequestUri!.Host == "owner.azconfig.io")
                return await ownerClient.SendAsync(request, token);
            if (request.RequestUri.Host.StartsWith("dsfo-", StringComparison.Ordinal) && request.RequestUri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal))
            {
                using var client = new HttpMessageInvoker(Product.Client());
                return await client.SendAsync(request, token);
            }
            Assert.Equal("management.azure.com", request.RequestUri.Host);
            if (request.Method == HttpMethod.Get)
            {
                if (CreateDamage == "read-timeout") throw new TaskCanceledException("secret-provider-detail");
                if (created.ContainsKey(request.RequestUri.AbsolutePath)
                    && request.RequestUri.AbsolutePath.Contains("/configurationstores/", StringComparison.Ordinal))
                    ConfigurationPolls++;
                if (ExistingTarget) return JsonReply(HttpStatusCode.OK, new JsonObject { ["tags"] = new JsonObject { ["dsf"] = "matching-does-not-prove-ownership" } });
                if (request.RequestUri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal))
                {
                    var operationRecords = JsonNode.Parse(owner.ListJson())!.AsArray()
                        .Select(r => JsonNode.Parse((string)r!["value"]!)!)
                        .Where(r => (string?)r["kind"] == "decide-foundation-operation"
                            && (string?)r["deploymentTarget"] is not null && r["managedBy"] is null).ToArray();
                    return JsonReply(HttpStatusCode.OK, new JsonObject
                    {
                        ["value"] = new JsonArray(operationRecords.Select((record, index) => (JsonNode)new JsonObject
                        {
                            ["operationId"] = index.ToString(),
                            ["properties"] = new JsonObject
                            {
                                ["provisioningOperation"] = "Create",
                                ["provisioningState"] = DeploymentFails && ((string)record["target"]!).Contains("/components/", StringComparison.Ordinal) ? "Failed" : "Succeeded",
                                ["statusCode"] = ResourceStatusCode,
                                ["trackingId"] = ResourceDamage == "invalid-tracking-id" ? "secret-provider-detail" : Guid.NewGuid().ToString(),
                                ["targetResource"] = new JsonObject { ["id"] = (string)record["target"]! },
                            },
                        }).ToArray()),
                    });
                }
                return created.TryGetValue(request.RequestUri.AbsolutePath, out var current)
                    ? JsonReply(HttpStatusCode.OK, current)
                    : JsonReply(HttpStatusCode.NotFound, new JsonObject());
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            var records = JsonNode.Parse(owner.ListJson())!.AsArray();
            Assert.Contains(records, entry =>
            {
                var value = JsonNode.Parse((string)entry!["value"]!)!;
                return (string?)value["kind"] == "decide-foundation-operation"
                    && (string?)value["target"] == request.RequestUri.AbsolutePath.ToLowerInvariant()
                    && (string?)value["state"] == "Intent";
            });
            Writes.Add(await request.Content!.ReadAsStringAsync(token));
            if (CancelOnPut is not null)
            {
                CancelOnPut.Cancel();
                throw new OperationCanceledException(token);
            }
            if (RejectPut) return JsonReply(HttpStatusCode.Forbidden, new JsonObject { ["error"] = "secret-provider-detail" });
            if (request.RequestUri.AbsolutePath.Contains("/deployments/", StringComparison.Ordinal))
            {
                if (!CompleteDeployment) throw new HttpRequestException("secret-provider-detail");
                var pending = JsonNode.Parse(owner.ListJson())!.AsArray().Select(r => JsonNode.Parse((string)r!["value"]!)!)
                    .Where(r => (string?)r["deploymentTarget"] == request.RequestUri.AbsolutePath.ToLowerInvariant()).ToArray();
                foreach (var intent in pending)
                {
                    var target = (string)intent["target"]!;
                    created[target] = new JsonObject
                    {
                        ["id"] = target,
                        ["etag"] = Guid.NewGuid().ToString(),
                        ["managedBy"] = (string?)intent["managedBy"],
                        ["systemData"] = new JsonObject { ["createdAt"] = DateTimeOffset.UtcNow, ["createdBy"] = BoundaryPlanFixture.Principal },
                        ["properties"] = new JsonObject
                        {
                            ["provisioningState"] = "Succeeded",
                            ["principalId"] = Guid.NewGuid().ToString(),
                            ["clientId"] = Guid.NewGuid().ToString(),
                            ["tenantId"] = BoundaryPlanFixture.Tenant,
                        },
                    };
                    if (ResourceDamage == "old-incarnation") created[target]["systemData"]!["createdAt"] = "2000-01-01T00:00:00Z";
                    if (ResourceDamage == "human-modified") created[target]["systemData"]!["lastModifiedAt"] = DateTimeOffset.UtcNow.AddSeconds(1);
                    if (ResourceDamage == "duplicate-principal") created[target]["properties"]!["principalId"] = BoundaryPlanFixture.Principal;
                    if (CosmosNativeReceipts && target.Contains("/sqldatabases/", StringComparison.Ordinal))
                    {
                        created[target].AsObject().Remove("systemData");
                        created[target].AsObject().Remove("etag");
                        created[target]["properties"]!["resource"] = new JsonObject
                        {
                            ["id"] = target.Split('/').Last(),
                            ["_rid"] = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                            ["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        };
                    }
                }
                return JsonReply(HttpStatusCode.Created, new JsonObject
                {
                    ["id"] = request.RequestUri.AbsolutePath,
                    ["properties"] = new JsonObject { ["provisioningState"] = DeploymentFails ? "Failed" : "Succeeded", ["correlationId"] = Guid.NewGuid().ToString() },
                });
            }
            if (CompleteCreates)
            {
                if (AsyncConfiguration && request.RequestUri.AbsolutePath.Contains("/configurationstores/", StringComparison.Ordinal))
                {
                    var complete = new JsonObject
                    {
                        ["id"] = request.RequestUri.AbsolutePath,
                        ["properties"] = new JsonObject
                        {
                            ["provisioningState"] = "Succeeded",
                            ["creationDate"] = DateTimeOffset.UtcNow,
                            ["endpoint"] = "https://" + request.RequestUri.Segments.Last() + ".azconfig.io",
                        },
                    };
                    created[request.RequestUri.AbsolutePath] = complete.DeepClone();
                    complete["properties"]!["provisioningState"] = "Creating";
                    return JsonReply(HttpStatusCode.Created, complete);
                }
                var response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(new JsonObject
                    {
                        ["id"] = request.RequestUri.AbsolutePath,
                        ["properties"] = new JsonObject
                        {
                            ["provisioningState"] = "Succeeded",
                            ["creationDate"] = DateTimeOffset.UtcNow,
                            ["endpoint"] = "https://" + request.RequestUri.Segments.Last() + ".azconfig.io",
                        },
                    }.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
                response.Headers.Date = DateTimeOffset.UtcNow;
                if (CreateDamage == "malformed-json") response.Content = new StringContent("{secret-provider-detail");
                if (CreateDamage == "invalid-date" && request.RequestUri.AbsolutePath.Contains("/configurationstores/", StringComparison.Ordinal))
                {
                    var invalid = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                    invalid["properties"]!["creationDate"] = new JsonObject { ["secret-provider-detail"] = true };
                    response.Content = new StringContent(invalid.ToJsonString(), Encoding.UTF8, "application/json");
                }
                if (CreateDamage == "invalid-request-id")
                {
                    response.Headers.Remove("x-ms-request-id");
                    response.Headers.Add("x-ms-request-id", "secret-provider-detail");
                }
                if (CreateDamage == "updated-not-created") response.StatusCode = HttpStatusCode.OK;
                return response;
            }

            throw new HttpRequestException("secret-provider-detail");
        }

        private static HttpResponseMessage JsonReply(HttpStatusCode status, JsonNode body)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
            response.Headers.Date = DateTimeOffset.UtcNow;
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ownerClient.Dispose();
            base.Dispose(disposing);
        }
    }
}
