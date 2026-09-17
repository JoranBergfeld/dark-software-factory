using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryFoundationTests
{
    public static IEnumerable<object[]> InvalidProviderGuids()
    {
        foreach (var field in new[] { "correlationId", "trackingId", "principalId", "clientId" })
        foreach (var json in new[] { "null", "\"\"", "\"secret-provider-detail\"", "{\"secret-provider-detail\":true}",
                     "42", "\"00000000-0000-0000-0000-000000000000\"" })
            yield return [field, json];
    }

    [Theory]
    [MemberData(nameof(InvalidProviderGuids))]
    public async Task Required_provider_GUID_nulls_and_malformed_values_return_structured_unknown_after_dispatch(string field, string json)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, GuidField = field, GuidValue = JsonNode.Parse(json), EnforceCorrelation = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        Assert.False((bool)result["foundation"]!["isBaseProvisioned"]!);
        var operations = result["foundation"]!["operations"]!.AsArray();
        Assert.True(operations.Count > 3);
        Assert.Contains(operations, operation => (string?)operation!["state"] == "Completed");
        Assert.Contains(operations, operation => (string?)operation!["state"] is "Unknown" or "Intent");
        Assert.DoesNotContain("ArgumentNullException", terminal.Output + machine.Store.Export());
        Assert.DoesNotContain("secret-provider-detail", terminal.Output + machine.Store.Export());
        if (field == "correlationId")
        {
            var deployment = Assert.Single(operations, operation => ((string)operation!["target"]!).Contains("/deployments/", StringComparison.Ordinal));
            Assert.Equal(201, (int?)deployment!["httpStatus"]);
            Assert.False(string.IsNullOrEmpty((string?)deployment["providerRequestId"]));
            Assert.Equal("Unknown", (string?)deployment["state"]);
        }
        if (field is "principalId" or "clientId")
            Assert.Contains(operations, operation => ((string)operation!["target"]!).Contains("/components/", StringComparison.Ordinal)
                && (string?)operation["state"] == "Completed");
        var before = machine.Store.Export();
        var (resumeExit, resumed, _) = await machine.InvokeAsync("resume");
        Assert.Equal(1, resumeExit);
        Assert.Equal("Unknown", (string?)resumed["foundation"]?["state"]);
        Assert.Equal(1, (await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint)).Exit);
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(3, arm.Writes.Count);
    }

    [Theory]
    [InlineData("widget", "PD5DALigD-w=")]
    [InlineData("runs", "PD5DAA==")]
    [InlineData("runs", "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("runs", "PD5DALigD_w=")]
    [InlineData("runs", "PD5DALigD-x=")]
    [InlineData("runs", "PD5DALigD w=")]
    [InlineData("runs", "secret-provider-detail")]
    [InlineData("runs", "")]
    [InlineData("runs", null)]
    public async Task Invalid_Cosmos_native_RIDs_keep_creation_unknown_and_never_reissue(string target, string? rid)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        {
            CompleteCreates = true, CompleteDeployment = true, CosmosNativeReceipts = true,
            CosmosRidTarget = target, CosmosRidValue = rid,
        };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        var suffix = target == "widget" ? "/sqldatabases/widget" : "/sqldatabases/widget/containers/" + target;
        var receipt = Assert.Single(result["foundation"]!["operations"]!.AsArray(),
            operation => ((string)operation!["target"]!).EndsWith(suffix, StringComparison.Ordinal));
        Assert.NotEqual("Completed", (string?)receipt!["state"]);
        Assert.Null(receipt["incarnation"]);
        Assert.DoesNotContain("secret-provider-detail", terminal.Output + machine.Store.Export());
        var before = machine.Store.Export();
        Assert.Equal(1, (await machine.InvokeAsync("resume")).Exit);
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(3, arm.Writes.Count);
    }

    [Theory]
    [InlineData(false, "PD5DAA==", "PD5DALigD-w=")]
    [InlineData(true, "PD5D-w==", "PD5D-7igD-w=")]
    public async Task Cosmos_native_RID_alphabet_is_preserved_in_database_and_container_receipts(
        bool hyphenDatabase, string databaseRid, string containerRid)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, CosmosNativeReceipts = true, HyphenDatabaseRid = hyphenDatabase };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(0, exit);
        Assert.Empty(terminal.Error);
        Assert.Equal("Inactive", (string?)result["foundation"]?["state"]);
        foreach (var (suffix, rid) in new[] { ("/sqldatabases/widget", databaseRid), ("/sqldatabases/widget/containers/runs", containerRid) })
        {
            var receipt = Assert.Single(result["foundation"]!["operations"]!.AsArray(),
                operation => ((string)operation!["target"]!).EndsWith(suffix, StringComparison.Ordinal));
            Assert.Equal("Completed", (string?)receipt!["state"]);
            Assert.Equal("Created", (string?)receipt["disposition"]);
            Assert.Equal("cosmos-rid-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rid))),
                (string?)receipt["incarnation"]);
        }
        var before = machine.Store.Export();
        Assert.Equal(0, (await machine.InvokeAsync("resume")).Exit);
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(3, arm.Writes.Count);
    }

    [Theory]
    [InlineData("missing-app-id")]
    [InlineData("invalid-app-id")]
    [InlineData("null-app-id")]
    [InlineData("missing-date")]
    [InlineData("invalid-date")]
    [InlineData("old-date")]
    [InlineData("future-date")]
    [InlineData("contradictory-system-data")]
    [InlineData("wrong-workspace")]
    [InlineData("wrong-target")]
    [InlineData("wrong-type")]
    [InlineData("wrong-state")]
    [InlineData("public-ingestion")]
    public async Task Unproven_AppInsights_native_evidence_stays_unknown_without_reissue_or_secret_receipts(string damage)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, InsightsDamage = damage, EnforceCorrelation = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)result["foundation"]?["state"]);
        var operation = Assert.Single(result["foundation"]!["operations"]!.AsArray(),
            o => ((string)o!["target"]!).Contains("/microsoft.insights/components/", StringComparison.Ordinal));
        Assert.NotEqual("Completed", (string?)operation!["state"]);
        Assert.NotEqual("Created", (string?)operation["disposition"]);
        Assert.DoesNotContain("not-for-receipts", terminal.Output + machine.Store.Export());
        Assert.DoesNotContain("secret-provider-detail", terminal.Output + machine.Store.Export());
        var before = machine.Store.Export();
        await machine.InvokeAsync("resume");
        Assert.Equal(before, machine.Store.Export());
        Assert.Equal(3, arm.Writes.Count);
    }

    [Fact]
    public async Task AppInsights_native_200_creation_records_its_incarnation_without_claiming_telemetry_readiness()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        await machine.ReserveAsync(fingerprint);
        var arm = new FoundationTransport(machine.Store)
        { CompleteCreates = true, CompleteDeployment = true, EnforceCorrelation = true };
        machine.Boundary.Repository.ReservationHttp = new HttpClient(arm);

        var (exit, result, terminal) = await machine.InvokeAsync("provision", "--reviewed-fingerprint", fingerprint);

        Assert.Equal(0, exit);
        var operation = Assert.Single(result["foundation"]!["operations"]!.AsArray(),
            o => ((string)o!["target"]!).Contains("/microsoft.insights/components/", StringComparison.Ordinal));
        Assert.Equal(200, (int?)operation!["httpStatus"]);
        Assert.Equal("Created", (string?)operation["disposition"]);
        Assert.StartsWith("appinsights-app-id:887f4bfd-b5fd-40d7-9fc3-123456789abc;created:", (string?)operation["incarnation"]);
        Assert.False(string.IsNullOrEmpty((string?)operation["providerRequestId"]));
        Assert.False(string.IsNullOrEmpty((string?)operation["providerOperationId"]));
        Assert.False(string.IsNullOrEmpty((string?)operation["providerCorrelationId"]));
        Assert.Contains("factory telemetry connectivity", terminal.Output);
        Assert.DoesNotContain("InstrumentationKey", terminal.Output + machine.Store.Export());
    }

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

    internal sealed class FoundationTransport(ReservationTestStore owner, TimeProvider? creationClock = null) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker ownerClient = new(owner.Client());
        public ReservationTestStore Product { get; set; } = new();
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
        public bool HyphenDatabaseRid { get; init; }
        public string? CosmosRidTarget { get; init; }
        public string? CosmosRidValue { get; init; }
        public bool DeploymentFails { get; init; }
        public string? InsightsDamage { get; init; }
        public string? GuidField { get; init; }
        public JsonNode? GuidValue { get; init; }
        private readonly Dictionary<string, JsonNode> created = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, JsonNode> CreatedResources => created;
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
                                ["statusCode"] = ((string)record["target"]!).Contains("/microsoft.insights/components/", StringComparison.Ordinal)
                                    ? "OK" : ResourceStatusCode,
                                ["trackingId"] = ProviderGuidValue("trackingId",
                                    ResourceDamage == "invalid-tracking-id" ? "secret-provider-detail" : Guid.NewGuid().ToString()),
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
                        ["systemData"] = new JsonObject { ["createdAt"] = (creationClock ?? TimeProvider.System).GetUtcNow(), ["createdBy"] = BoundaryPlanFixture.Principal },
                        ["properties"] = new JsonObject
                        {
                            ["provisioningState"] = "Succeeded",
                            ["principalId"] = ProviderGuidValue("principalId", Guid.NewGuid().ToString()),
                            ["clientId"] = ProviderGuidValue("clientId", Guid.NewGuid().ToString()),
                            ["tenantId"] = BoundaryPlanFixture.Tenant,
                        },
                    };
                    if (ResourceDamage == "old-incarnation") created[target]["systemData"]!["createdAt"] = "2000-01-01T00:00:00Z";
                    if (ResourceDamage == "human-modified") created[target]["systemData"]!["lastModifiedAt"] = DateTimeOffset.UtcNow.AddSeconds(1);
                    if (ResourceDamage == "duplicate-principal") created[target]["properties"]!["principalId"] = BoundaryPlanFixture.Principal;
                    if (target.EndsWith("-cosmos", StringComparison.Ordinal))
                    {
                        var properties = created[target]["properties"]!;
                        properties["disableLocalAuth"] = true;
                        properties["publicNetworkAccess"] = "Disabled";
                        properties["documentEndpoint"] = "https://" + target.Split('/').Last() + ".documents.azure.com:443/";
                    }
                    if (target.Contains("/sqldatabases/", StringComparison.Ordinal))
                        created[target]["properties"]!["resource"] = new JsonObject { ["id"] = target.Split('/').Last() };
                    if (CosmosNativeReceipts && target.Contains("/sqldatabases/", StringComparison.Ordinal))
                    {
                        created[target].AsObject().Remove("systemData");
                        created[target].AsObject().Remove("etag");
                        var nativeRid = target.Split('/').Last() switch
                        {
                            "working" => "PD5DALigD-g=",
                            "records" => "PD5DALigD-k=",
                            "lessons" => "PD5DALigD-o=",
                            "charters" => "PD5DALigD-s=",
                            "runs" => "PD5DALigD-w=",
                            "learning" => "PD5DALigD-0=",
                            _ => "PD5DAA==",
                        };
                        if (HyphenDatabaseRid)
                            nativeRid = nativeRid == "PD5DAA==" ? "PD5D-w==" : nativeRid.Replace("PD5DAL", "PD5D-7", StringComparison.Ordinal);
                        created[target]["properties"]!["resource"] = new JsonObject
                        {
                            ["id"] = target.Split('/').Last(),
                            ["_rid"] = CosmosRidTarget == target.Split('/').Last() ? CosmosRidValue : nativeRid,
                            ["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        };
                    }
                    if (target.Contains("/containers/", StringComparison.Ordinal))
                        created[target]["properties"]!["resource"]!["partitionKey"] = new JsonObject
                            { ["kind"] = "Hash", ["version"] = 2, ["paths"] = new JsonArray("/product") };
                    if (target.Contains("/microsoft.insights/components/", StringComparison.Ordinal))
                    {
                        // Components_CreateOrUpdate 2020-02-02 returns 200 and native properties, not systemData.
                        created[target] = new JsonObject
                        {
                            ["id"] = target,
                            ["type"] = "Microsoft.Insights/components",
                            ["kind"] = "web",
                            ["properties"] = new JsonObject
                            {
                                ["AppId"] = "887f4bfd-b5fd-40d7-9fc3-123456789abc",
                                ["CreationDate"] = "2026-09-15T23:00:00Z",
                                ["provisioningState"] = "Succeeded",
                                ["WorkspaceResourceId"] = target.Replace("/microsoft.insights/components/", "/microsoft.operationalinsights/workspaces/", StringComparison.Ordinal)
                                    .Replace("-insights", "-logs", StringComparison.Ordinal),
                                ["InstrumentationKey"] = "not-for-receipts",
                                ["publicNetworkAccessForIngestion"] = "Disabled",
                                ["publicNetworkAccessForQuery"] = "Disabled",
                            },
                        };
                        var native = created[target]["properties"]!.AsObject();
                        if (InsightsDamage == "missing-app-id") native.Remove("AppId");
                        if (InsightsDamage == "invalid-app-id") native["AppId"] = "secret-provider-detail";
                        if (InsightsDamage == "null-app-id") native["AppId"] = null;
                        if (InsightsDamage == "missing-date") native.Remove("CreationDate");
                        if (InsightsDamage == "invalid-date") native["CreationDate"] = new JsonObject { ["secret-provider-detail"] = true };
                        if (InsightsDamage == "old-date") native["CreationDate"] = "2000-01-01T00:00:00Z";
                        if (InsightsDamage == "future-date") native["CreationDate"] = "2099-01-01T00:00:00Z";
                        if (InsightsDamage == "contradictory-system-data")
                            created[target]["systemData"] = new JsonObject { ["createdAt"] = "2000-01-01T00:00:00Z" };
                        if (InsightsDamage == "wrong-workspace") native["WorkspaceResourceId"] = "/subscriptions/other/resourcegroups/application";
                        if (InsightsDamage == "wrong-target") created[target]["id"] = "/subscriptions/other/resourcegroups/application";
                        if (InsightsDamage == "wrong-type") created[target]["type"] = "Microsoft.Other/components";
                        if (InsightsDamage == "wrong-state") native["provisioningState"] = "Updating";
                        if (InsightsDamage == "public-ingestion") native["publicNetworkAccessForIngestion"] = "Enabled";
                    }
                }
                return JsonReply(HttpStatusCode.Created, new JsonObject
                {
                    ["id"] = request.RequestUri.AbsolutePath,
                    ["properties"] = new JsonObject
                    {
                        ["provisioningState"] = DeploymentFails ? "Failed" : "Succeeded",
                        ["correlationId"] = ProviderGuidValue("correlationId", Guid.NewGuid().ToString()),
                    },
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

        private JsonNode? ProviderGuidValue(string field, string value) =>
            GuidField == field ? GuidValue?.DeepClone() : JsonValue.Create(value);

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
