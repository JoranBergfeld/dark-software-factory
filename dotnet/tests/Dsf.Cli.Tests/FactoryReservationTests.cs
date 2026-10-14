using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryReservationTests
{
    private const string MalformedReceiptMarker = "malformed-receipt-marker-do-not-print";

    [Theory]
    [InlineData("last_modified")]
    [InlineData("locked")]
    [InlineData("value")]
    public async Task Committed_initial_admission_with_malformed_receipt_is_redacted_unknown_and_recovers_the_same_identity(string field)
    {
        var store = new ReservationTestStore
        {
            ModifyResponse = (method, record) =>
            {
                if (method == HttpMethod.Put) MalformReceipt(record, field);
            },
        };
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();
        Assert.Equal(0, reviewed.Exit);

        var (exit, uncertain, terminal) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);

        Assert.Equal(1, exit);
        Assert.True((string?)uncertain["state"] == "Unknown", terminal.Output + terminal.Error);
        Assert.Empty(terminal.Error);
        Assert.True((bool)uncertain["hasRegistryEffects"]!);
        Assert.False((bool)uncertain["isProvisioned"]!);
        Assert.Null(uncertain["pendingIntent"]);
        Assert.Contains("no earlier durable intent", (string?)uncertain["detail"]);
        Assert.DoesNotContain(MalformedReceiptMarker, terminal.Output + terminal.Error + store.Export());
        var admission = Assert.Single(store.Writes);
        Assert.Equal("dsf/onboarding/products/widget", admission.Key);
        Assert.Equal("*", admission.IfNoneMatch);
        Assert.Single(JsonNode.Parse(store.ListJson())!.AsArray());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(admission.Value)!["identity"], uncertain["identity"]));

        var persisted = store.Export();
        var observer = new ReservationMachine(ReservationTestStore.Restore(persisted));
        var (statusExit, status, _) = await observer.InvokeAsync("status");
        Assert.Equal(1, statusExit);
        Assert.Equal("Partial", (string?)status["state"]);
        Assert.True(JsonNode.DeepEquals(uncertain["identity"], status["identity"]));
        Assert.Empty(observer.Store.Writes);
        Assert.Equal(persisted, observer.Store.Export());

        var replacement = new ReservationMachine(ReservationTestStore.Restore(observer.Store.Export()));
        var input = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(input, machine.Boundary.Input.ToJsonString());
            var (resumedExit, resumed, resumedTerminal) = await replacement.InvokeAsync("resume", "--input", input);
            Assert.True(resumedExit == 0, resumedTerminal.Output + resumedTerminal.Error);
            Assert.Equal("Reserved", (string?)resumed["state"]);
            Assert.False((bool)resumed["isProvisioned"]!);
            Assert.True(JsonNode.DeepEquals(uncertain["identity"], resumed["identity"]));
            Assert.DoesNotContain(replacement.Store.Writes, w => w.Key == admission.Key && w.IfNoneMatch == "*");
        }
        finally { File.Delete(input); }
        machine.Boundary.AssertReadOnly();
        observer.Boundary.AssertReadOnly();
        replacement.Boundary.AssertReadOnly();
    }

    [Theory]
    [InlineData("checkpoint", "last_modified")]
    [InlineData("checkpoint", "locked")]
    [InlineData("checkpoint", "value")]
    [InlineData("claim", "last_modified")]
    [InlineData("claim", "locked")]
    [InlineData("claim", "value")]
    public async Task Committed_resume_write_with_malformed_receipt_preserves_intent_and_recovers_without_duplicate_claims(string point, string field)
    {
        const string rootKey = "dsf/onboarding/products/widget";
        var initial = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" });
        var reviewed = await initial.Boundary.ReviewAsync();
        var (initialExit, admitted, _) = await initial.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal(1, initialExit);
        Assert.Equal("Unknown", (string?)admitted["state"]);
        var machine = new ReservationMachine(ReservationTestStore.Restore(initial.Store.Export()));
        machine.Store.ModifyResponse = (method, record) =>
        {
            var key = (string)record["key"]!;
            if (method == HttpMethod.Put && (point == "checkpoint" ? key == rootKey : key.StartsWith("dsf/onboarding/resources/", StringComparison.Ordinal)))
                MalformReceipt(record, field);
        };

        var (exit, uncertain, terminal) = await machine.InvokeAsync("resume");

        Assert.Equal(1, exit);
        Assert.True((string?)uncertain["state"] == "Unknown", terminal.Output + terminal.Error);
        Assert.Empty(terminal.Error);
        Assert.True((bool)uncertain["hasRegistryEffects"]!);
        Assert.False((bool)uncertain["isProvisioned"]!);
        Assert.True(JsonNode.DeepEquals(admitted["identity"], uncertain["identity"]));
        Assert.NotNull(uncertain["pendingIntent"]);
        Assert.DoesNotContain(MalformedReceiptMarker, terminal.Output + terminal.Error + machine.Store.Export());
        var committed = machine.Store.Writes.Last();
        var records = JsonNode.Parse(machine.Store.ListJson())!.AsArray();
        var root = JsonNode.Parse((string)records.Single(r => (string?)r!["key"] == rootKey)!["value"]!)!;
        Assert.Equal("Partial", (string?)root["state"]);
        if (point == "checkpoint")
        {
            Assert.Single(machine.Store.Writes);
            Assert.Equal(rootKey, committed.Key);
            Assert.NotNull(committed.IfMatch);
            Assert.True(JsonNode.DeepEquals(admitted["pendingIntent"], uncertain["pendingIntent"]));
            Assert.Null(root["intent"]);
        }
        else
        {
            Assert.Single(machine.Store.Writes, w => w.Key.StartsWith("dsf/onboarding/resources/", StringComparison.Ordinal));
            Assert.Equal("*", committed.IfNoneMatch);
            Assert.Equal(committed.Key, (string?)uncertain["pendingIntent"]!["key"]);
            Assert.True(JsonNode.DeepEquals(root["intent"], uncertain["pendingIntent"]));
        }
        Assert.Equal(committed.Value, (string?)records.Single(r => (string?)r!["key"] == committed.Key)!["value"]);

        var persisted = machine.Store.Export();
        var observer = new ReservationMachine(ReservationTestStore.Restore(persisted));
        var (statusExit, status, _) = await observer.InvokeAsync("status");
        Assert.Equal(1, statusExit);
        Assert.Equal(point == "checkpoint" ? "Partial" : "Unknown", (string?)status["state"]);
        Assert.True(JsonNode.DeepEquals(admitted["identity"], status["identity"]));
        Assert.True(JsonNode.DeepEquals(root["intent"], status["pendingIntent"]));
        Assert.Empty(observer.Store.Writes);
        Assert.Equal(persisted, observer.Store.Export());

        var replacement = new ReservationMachine(ReservationTestStore.Restore(observer.Store.Export()));
        var (resumedExit, resumed, resumedTerminal) = await replacement.InvokeAsync("resume");
        Assert.True(resumedExit == 0, resumedTerminal.Output + resumedTerminal.Error);
        Assert.Equal("Reserved", (string?)resumed["state"]);
        Assert.False((bool)resumed["isProvisioned"]!);
        Assert.True(JsonNode.DeepEquals(admitted["identity"], resumed["identity"]));
        Assert.DoesNotContain(replacement.Store.Writes, w => w.Key == rootKey && w.IfNoneMatch == "*");
        var before = JsonNode.Parse(persisted)!.AsObject();
        var after = JsonNode.Parse(replacement.Store.Export())!.AsObject();
        foreach (var record in before.Where(r => r.Key != rootKey))
        {
            Assert.Equal((string?)record.Value, (string?)after[record.Key]);
            Assert.DoesNotContain(replacement.Store.Writes, w => w.Key == record.Key);
        }
        initial.Boundary.AssertReadOnly();
        machine.Boundary.AssertReadOnly();
        observer.Boundary.AssertReadOnly();
        replacement.Boundary.AssertReadOnly();
    }

    [Theory]
    [InlineData("status", "anchor", "last_modified")]
    [InlineData("status", "anchor", "locked")]
    [InlineData("status", "anchor", "value")]
    [InlineData("status", "claim", "last_modified")]
    [InlineData("status", "claim", "locked")]
    [InlineData("status", "claim", "value")]
    [InlineData("resume", "anchor", "last_modified")]
    [InlineData("resume", "anchor", "locked")]
    [InlineData("resume", "anchor", "value")]
    [InlineData("resume", "claim", "last_modified")]
    [InlineData("resume", "claim", "locked")]
    [InlineData("resume", "claim", "value")]
    public async Task Malformed_registry_reads_report_redacted_unavailability_without_writes_and_allow_subsequent_recovery(string action, string point, string field)
    {
        var initial = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" });
        var reviewed = await initial.Boundary.ReviewAsync();
        var (initialExit, admitted, _) = await initial.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal(1, initialExit);
        Assert.Equal("Unknown", (string?)admitted["state"]);
        var persisted = initial.Store.Export();
        var machine = new ReservationMachine(ReservationTestStore.Restore(persisted));
        var key = point == "anchor" ? "dsf/onboarding/products/widget" : "dsf/onboarding/repositories/913";
        machine.Store.ModifyResponse = (method, record) =>
        {
            if (method == HttpMethod.Get && (string?)record["key"] == key) MalformReceipt(record, field);
        };

        var (exit, unknown, terminal) = await machine.InvokeAsync(action);

        Assert.Equal(1, exit);
        Assert.True((string?)unknown["state"] == "Unknown", terminal.Output + terminal.Error);
        Assert.Contains("read unavailable", (string?)unknown["detail"]);
        Assert.Empty(terminal.Error);
        Assert.False((bool)unknown["hasRegistryEffects"]!);
        Assert.False((bool)unknown["isProvisioned"]!);
        Assert.DoesNotContain(MalformedReceiptMarker, terminal.Output + terminal.Error);
        if (point == "anchor") Assert.Null(unknown["identity"]);
        else
        {
            Assert.True(JsonNode.DeepEquals(admitted["identity"], unknown["identity"]));
            Assert.True(JsonNode.DeepEquals(admitted["pendingIntent"], unknown["pendingIntent"]));
        }
        Assert.Empty(machine.Store.Writes);
        Assert.Equal(persisted, machine.Store.Export());

        var observer = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        var (statusExit, status, _) = await observer.InvokeAsync("status");
        Assert.Equal(1, statusExit);
        Assert.Equal("Unknown", (string?)status["state"]);
        Assert.True(JsonNode.DeepEquals(admitted["identity"], status["identity"]));
        Assert.True(JsonNode.DeepEquals(admitted["pendingIntent"], status["pendingIntent"]));
        Assert.Empty(observer.Store.Writes);
        Assert.Equal(persisted, observer.Store.Export());

        var replacement = new ReservationMachine(ReservationTestStore.Restore(observer.Store.Export()));
        var (resumedExit, resumed, resumedTerminal) = await replacement.InvokeAsync("resume");
        Assert.True(resumedExit == 0, resumedTerminal.Output + resumedTerminal.Error);
        Assert.Equal("Reserved", (string?)resumed["state"]);
        Assert.False((bool)resumed["isProvisioned"]!);
        Assert.True(JsonNode.DeepEquals(admitted["identity"], resumed["identity"]));
        Assert.DoesNotContain(replacement.Store.Writes, w => w.Key == "dsf/onboarding/products/widget" && w.IfNoneMatch == "*"
            || w.Key == "dsf/onboarding/repositories/913");
        initial.Boundary.AssertReadOnly();
        machine.Boundary.AssertReadOnly();
        observer.Boundary.AssertReadOnly();
        replacement.Boundary.AssertReadOnly();
    }

    [Theory]
    [InlineData("reserve", "table")]
    [InlineData("reserve", "yaml")]
    [InlineData("status", "table")]
    [InlineData("status", "yaml")]
    [InlineData("resume", "table")]
    [InlineData("resume", "yaml")]
    public async Task Valid_operator_can_reserve_inspect_and_resume_with_non_json_Azure_CLI_defaults(string action, string defaultOutput)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        Assert.Equal(0, reviewed.Exit);
        var target = machine;
        string? factoryId = null;
        string? operationId = null;
        if (action != "reserve")
        {
            if (action == "resume") machine.Store.LoseResponseForKey = "dsf/onboarding/repositories/913";
            var (admissionExit, admitted, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
            Assert.Equal(action == "resume" ? 1 : 0, admissionExit);
            factoryId = (string)admitted["factoryId"]!;
            operationId = (string)admitted["operationId"]!;
            target = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        }
        target.Boundary.DefaultOutput = defaultOutput;

        var (exit, result, terminal) = action == "reserve"
            ? await target.ReserveAsync((string)reviewed.Plan["fingerprint"]!)
            : await target.InvokeAsync(action);

        Assert.True(exit == 0, terminal.Output + terminal.Error);
        Assert.Equal("Reserved", (string?)result["state"]);
        Assert.False((bool)result["isProvisioned"]!);
        if (factoryId is not null)
        {
            Assert.Equal(factoryId, (string?)result["factoryId"]);
            Assert.Equal(operationId, (string?)result["operationId"]);
        }
        if (action == "status") Assert.Empty(target.Store.Writes);
        var requests = target.Boundary.Calls.Where(args => args.Contains("https://azconfig.io")).ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, args => Assert.Equal("json", args[args.IndexOf("--output") + 1]));
        Assert.DoesNotContain("fixture-output-token-do-not-print", terminal.Output + terminal.Error + target.Store.Export());
        Assert.DoesNotContain(".signature", terminal.Output + terminal.Error + target.Store.Export());
        machine.Boundary.AssertReadOnly();
        target.Boundary.AssertReadOnly();
    }

    [Theory]
    [InlineData("product")]
    [InlineData("tenant")]
    [InlineData("digest")]
    [InlineData("progress")]
    [InlineData("resource")]
    public async Task Malformed_known_schema_is_not_downgraded_to_proven_nonoverlap(string field)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var approved = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)approved.Plan["fingerprint"]!);
        var persisted = JsonNode.Parse(machine.Store.Export())!.AsObject();
        var key = field == "resource" ? persisted.First(r => r.Key.StartsWith("dsf/onboarding/resources/")).Key
            : "dsf/onboarding/products/widget";
        var record = JsonNode.Parse((string)persisted[key]!)!;
        var value = JsonNode.Parse((string)record["value"]!)!;
        var newKey = key;
        if (field == "product")
        {
            value["identity"]!["productKey"] = "WIDGET";
            newKey = "dsf/onboarding/products/WIDGET";
        }
        if (field == "tenant") value["identity"]!["tenantId"] = "not-a-tenant";
        if (field == "digest") value["planSha256"] = "";
        if (field == "progress") value["completedSteps"] = 0;
        if (field == "resource")
        {
            const string invalid = "/subscriptions/not-a-resource";
            newKey = "dsf/onboarding/resources/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(invalid)));
            value["claim"]!["target"] = invalid;
            value["claim"]!["key"] = newKey;
        }
        record["key"] = newKey;
        record["value"] = value.ToJsonString();
        persisted.Remove(key);
        persisted[newKey] = record.ToJsonString();
        var other = new ReservationMachine(ReservationTestStore.Restore(persisted.ToJsonString()));
        other.SelectOtherProduct();

        var (exit, plan, _) = await other.Boundary.ReviewAsync();

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)plan["state"]);
        Assert.Empty(other.Store.Writes);
    }

    [Fact]
    public async Task A_missing_previously_confirmed_chunk_is_corruption_not_a_hole_to_skip_during_recovery()
    {
        var machine = new ReservationMachine(new ReservationTestStore
        {
            LoseResponseWhen = (key, value) => key.StartsWith("dsf/onboarding/plans/") && (int?)JsonNode.Parse(value)!["index"] == 1,
        });
        var approved = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)approved.Plan["fingerprint"]!);
        var persisted = JsonNode.Parse(machine.Store.Export())!.AsObject();
        persisted.Remove(persisted.Single(r => r.Key.EndsWith("/0000", StringComparison.Ordinal)).Key);
        var other = new ReservationMachine(ReservationTestStore.Restore(persisted.ToJsonString()));
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, machine.Boundary.Input.ToJsonString());

            var (exit, result, _) = await other.InvokeAsync("resume", "--input", file);

            Assert.Equal(1, exit);
            Assert.Equal("Unknown", (string?)result["state"]);
            Assert.Empty(other.Store.Writes);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Incomplete_plan_repair_accepts_equivalent_provider_ordering_without_changing_the_admitted_plan()
    {
        var machine = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/products/widget" });
        var permissions = "https://management.azure.com" + BoundaryPlanFixture.Root + "/providers/Microsoft.Authorization/permissions?api-version=2022-04-01";
        machine.Boundary.Overrides[permissions] = new(0, """
            {"value":[
                {"actions":["*/read"],"notActions":[],"dataActions":[],"notDataActions":[]},
                {"actions":["Microsoft.Resources/deployments/*"],"notActions":[],"dataActions":[],"notDataActions":[]}
            ]}
            """, "");
        var approved = await machine.Boundary.ReviewAsync();
        var (_, original, _) = await machine.ReserveAsync((string)approved.Plan["fingerprint"]!);
        var other = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        other.Boundary.Overrides[permissions] = new(0, """
            {"value":[
                {"actions":["Microsoft.Resources/deployments/*"],"notActions":[],"dataActions":[],"notDataActions":[]},
                {"actions":["*/read"],"notActions":[],"dataActions":[],"notDataActions":[]}
            ]}
            """, "");
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, machine.Boundary.Input.ToJsonString());

            var (exit, result, terminal) = await other.InvokeAsync("resume", "--input", file);

            Assert.True(exit == 0, terminal.Output + terminal.Error);
            Assert.Equal(original["operationId"]!.ToJsonString(), result["operationId"]!.ToJsonString());
            Assert.Equal((string?)approved.Plan["fingerprint"], (string?)result["identity"]!["planFingerprint"]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Known_nonoverlapping_reservation_schemas_allow_other_products_but_unknown_records_never_mean_available()
    {
        var first = new ReservationMachine(new ReservationTestStore());
        var firstReview = await first.Boundary.ReviewAsync();
        await first.ReserveAsync((string)firstReview.Plan["fingerprint"]!);
        var other = new ReservationMachine(ReservationTestStore.Restore(first.Store.Export()));
        other.SelectOtherProduct();

        var approved = await other.Boundary.ReviewAsync();

        Assert.Equal(0, approved.Exit);
        Assert.Equal("Reviewed", (string?)approved.Plan["state"]);
        other.Boundary.OwnerEntries = () =>
        {
            var records = JsonNode.Parse(other.Store.ListJson())!.AsArray();
            records.Add(new JsonObject
            {
                ["key"] = "dsf/onboarding/future/opaque",
                ["label"] = null,
                ["etag"] = "opaque-revision",
                ["value"] = """{"schemaVersion":999,"secret":"secret-unknown-record"}""",
            });
            return records.ToJsonString();
        };
        var (exit, result, terminal) = await other.ReserveAsync((string)approved.Plan["fingerprint"]!);
        Assert.Equal(1, exit);
        Assert.NotEqual("Reserved", (string?)result["state"]);
        Assert.Contains("Unknown", terminal.Output);
        Assert.DoesNotContain("secret-unknown-record", terminal.Output + terminal.Error);
        Assert.Empty(other.Store.Writes);
    }

    [Fact]
    public async Task An_unobserved_initial_admission_never_becomes_a_fabricated_prior_write_or_a_new_identity_on_resume()
    {
        var store = new ReservationTestStore { LoseBeforeCommit = true };
        var machine = new ReservationMachine(store);
        var approved = await machine.Boundary.ReviewAsync();
        var (_, result, _) = await machine.ReserveAsync((string)approved.Plan["fingerprint"]!);
        Assert.Equal("Unknown", (string?)result["state"]);
        Assert.Contains("no earlier durable intent", (string?)result["detail"]);
        Assert.Single(store.Writes);
        Assert.Equal("{}", store.Export());
        var other = new ReservationMachine(ReservationTestStore.Restore(store.Export()));

        var (exit, resumed, _) = await other.InvokeAsync("resume");

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)resumed["state"]);
        Assert.Null(resumed["factoryId"]);
        Assert.Empty(other.Store.Writes);
    }

    [Fact]
    public async Task Stale_local_repair_input_cannot_amend_an_admitted_operation()
    {
        var machine = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" });
        var approved = await machine.Boundary.ReviewAsync();
        var (_, original, _) = await machine.ReserveAsync((string)approved.Plan["fingerprint"]!);
        var other = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        var file = Path.GetTempFileName();
        try
        {
            machine.Boundary.Input["factoryLocation"] = "westeurope";
            await File.WriteAllTextAsync(file, machine.Boundary.Input.ToJsonString());

            var (exit, result, _) = await other.InvokeAsync("resume", "--input", file);

            Assert.Equal(1, exit);
            Assert.Equal("Blocked", (string?)result["state"]);
            Assert.Equal(original["operationId"]!.ToJsonString(), result["operationId"]!.ToJsonString());
            Assert.Empty(other.Store.Writes);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Concurrent_resume_clients_cannot_overwrite_newer_progress_with_a_stale_revision()
    {
        var initial = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" });
        var reviewed = await initial.Boundary.ReviewAsync();
        var (_, admitted, _) = await initial.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        var store = ReservationTestStore.Restore(initial.Store.Export());
        var first = new ReservationMachine(store);
        var second = new ReservationMachine(store);
        var arrivals = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforePut = async (key, create) =>
        {
            if (!create && key == "dsf/onboarding/products/widget")
            {
                if (Interlocked.Increment(ref arrivals) == 2) both.SetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        var results = await Task.WhenAll(first.InvokeAsync("resume"), second.InvokeAsync("resume"));

        Assert.Single(results, r => r.Exit == 0);
        Assert.Single(results, r => r.Exit == 1 && (string?)r.Result["state"] == "Blocked");
        Assert.All(results, r => Assert.Equal(admitted["operationId"]!.ToJsonString(), r.Result["operationId"]!.ToJsonString()));
        var observer = new ReservationMachine(ReservationTestStore.Restore(store.Export()));
        var (exit, status, _) = await observer.InvokeAsync("status");
        Assert.Equal(0, exit);
        Assert.Equal(5, (int?)status["confirmedClaims"]);
        Assert.Empty(observer.Store.Writes);
    }

    [Theory]
    [InlineData("chunk")]
    [InlineData("checkpoint")]
    [InlineData("final")]
    public async Task Lost_staged_receipts_are_reconciled_under_the_same_identity_and_intent(string point)
    {
        var store = new ReservationTestStore
        {
            LoseResponseWhen = (key, value) => point switch
            {
                "chunk" => key.StartsWith("dsf/onboarding/plans/") && (int?)JsonNode.Parse(value)!["index"] == 2,
                "checkpoint" => key == "dsf/onboarding/products/widget" && (int?)JsonNode.Parse(value)!["completedSteps"] == 1,
                _ => key == "dsf/onboarding/products/widget" && (string?)JsonNode.Parse(value)!["state"] == "Reserved",
            },
        };
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();
        var (_, uncertain, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal("Unknown", (string?)uncertain["state"]);
        var replacement = new ReservationMachine(ReservationTestStore.Restore(store.Export()));
        var input = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(input, machine.Boundary.Input.ToJsonString());
            var (exit, result, terminal) = await replacement.InvokeAsync("resume", "--input", input);
            Assert.True(exit == 0, terminal.Output + terminal.Error);
            Assert.Equal(uncertain["factoryId"]!.ToJsonString(), result["factoryId"]!.ToJsonString());
            Assert.Equal(uncertain["operationId"]!.ToJsonString(), result["operationId"]!.ToJsonString());
            if (point == "final") Assert.Empty(replacement.Store.Writes);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task Large_review_is_stored_losslessly_in_bounded_records_with_prior_intent_and_no_runtime_registration()
    {
        var store = new ReservationTestStore();
        store.AfterPut = (key, value) =>
        {
            if (key == "dsf/onboarding/products/widget") return;
            var anchor = JsonNode.Parse((string)JsonNode.Parse(store.ListJson())!.AsArray()
                .Single(r => (string?)r!["key"] == "dsf/onboarding/products/widget")!["value"]!)!;
            Assert.Equal(key, (string?)anchor["intent"]!["key"]);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))),
                (string?)anchor["intent"]!["valueSha256"]);
        };
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();
        var (exit, result, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal(0, exit);
        var entries = JsonNode.Parse(store.ListJson())!.AsArray();
        Assert.All(entries, entry => Assert.True(Encoding.UTF8.GetByteCount(entry!.ToJsonString()) <= 10 * 1024));
        Assert.All(entries, entry => Assert.Null(entry!["label"]));
        var chunks = entries.Where(e => (string?)e!["key"] is { } key && key.StartsWith("dsf/onboarding/plans/"))
            .Select(e => JsonNode.Parse((string)e!["value"]!)!).OrderBy(c => (int)c["index"]!)
            .SelectMany(c => Convert.FromBase64String((string)c["data"]!)).ToArray();
        Assert.True(chunks.Length > 10 * 1024);
        var plan = JsonNode.Parse(chunks)!;
        Assert.Equal(2, plan["selection"]!["applicationResourceIds"]!.AsArray().Count);
        Assert.Single(plan["selection"]!["evidenceBackends"]!.AsArray());
        Assert.Equal(7, (int?)plan["repositoryReview"]!["app"]!["id"]);
        Assert.Equal(42, (int?)plan["repositoryReview"]!["installation"]!["id"]);
        Assert.Equal((string?)reviewed.Plan["fingerprint"], (string?)plan["fingerprint"]);
        Assert.Equal("2026-09-15T23:00:00+00:00", (string?)result["identity"]!["createdAt"]);
        var plainPlan = Encoding.UTF8.GetString(chunks);
        Assert.DoesNotContain("PRIVATE KEY", plainPlan);
        Assert.DoesNotContain("accessToken", plainPlan);
        Assert.DoesNotContain(".signature", plainPlan);
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        var listExit = await CliApplication.InvokeAsync(
            ["list", "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--json"],
            CancellationToken.None, terminal, new AzureCliAppConfigurationClient(machine.Boundary), new RecordingCharterRepositoryClient(null));
        Assert.Equal(0, listExit);
        Assert.Equal("[]", terminal.Output.Trim());
        machine.Boundary.AssertReadOnly();
    }

    [Fact]
    public async Task Oversized_admission_fails_before_any_anchor_without_truncating_the_review()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        for (var i = 0; i < 510; i++)
        {
            var id = BoundaryPlanFixture.Root + "/resourceGroups/app-b/providers/Microsoft.Storage/storageAccounts/data" + i;
            machine.Boundary.Resources[id] = BoundaryPlanFixture.Resource(id, "Microsoft.Storage/storageAccounts");
            machine.Boundary.Input["applicationResourceIds"]!.AsArray().Add(id);
        }
        var reviewed = await machine.Boundary.ReviewAsync();
        Assert.Equal(0, reviewed.Exit);

        var (exit, result, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);

        Assert.Equal(1, exit);
        Assert.Equal("Failed", (string?)result["state"]);
        Assert.Contains("limits", (string?)result["detail"]);
        Assert.Empty(machine.Store.Writes);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("resume")]
    public async Task Later_read_failure_retains_the_already_verified_remote_identity_and_inflight_intent(string action)
    {
        var machine = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" });
        var reviewed = await machine.Boundary.ReviewAsync();
        var (_, original, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        var store = ReservationTestStore.Restore(machine.Store.Export());
        store.RejectReadKey = "dsf/onboarding/repositories/913";
        var replacement = new ReservationMachine(store);

        var (exit, result, _) = await replacement.InvokeAsync(action);

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)result["state"]);
        Assert.Equal(original["operationId"]!.ToJsonString(), result["operationId"]?.ToJsonString());
        Assert.NotNull(result["pendingIntent"]);
        Assert.Empty(store.Writes);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("resume")]
    public async Task Invalid_remote_locator_returns_typed_redacted_failure_without_default_authority(string action)
    {
        var boundary = new BoundaryPlanFixture();
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        var exit = await boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", action, "--owner-appconfig-endpoint", "https://secret-user:secret-password@owner.azconfig.io",
                "--product", "widget", "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription, "--json"]);

        Assert.Equal(1, exit);
        Assert.Contains("\"state\": \"Failed\"", terminal.Output);
        Assert.DoesNotContain("secret-password", terminal.Output + terminal.Error);
        Assert.Empty(boundary.Calls);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("intent")]
    [InlineData("claim")]
    public async Task Cancellation_stops_new_dispatch_without_rollback_and_exposes_held_partial_or_inflight_state(string point)
    {
        var store = new ReservationTestStore();
        using var cancel = new CancellationTokenSource();
        var machine = new ReservationMachine(store) { Token = cancel.Token };
        var reviewed = await machine.Boundary.ReviewAsync();
        var stoppedAt = 0;
        store.AfterPut = (key, value) =>
        {
            if (point == "admission" && store.Writes.Count == 1
                || point == "intent" && key == "dsf/onboarding/products/widget" && JsonNode.Parse(value)!["intent"] is not null
                || point == "claim" && key == "dsf/onboarding/repositories/913")
            {
                stoppedAt = store.Writes.Count;
                cancel.Cancel();
            }
        };

        var (exit, outcome, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);

        Assert.Equal(130, exit);
        Assert.Equal("Canceled", (string?)outcome["state"]);
        Assert.Equal(stoppedAt, store.Writes.Count);
        Assert.True(stoppedAt > 0);
        var replacement = new ReservationMachine(ReservationTestStore.Restore(store.Export()));
        var (_, status, _) = await replacement.InvokeAsync("status");
        Assert.Equal(outcome["factoryId"]!.ToJsonString(), status["factoryId"]!.ToJsonString());
        Assert.Contains((string?)status["state"], new[] { "Partial", "Unknown" });
        Assert.Empty(replacement.Store.Writes);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("operator")]
    [InlineData("repository")]
    [InlineData("unreviewed")]
    [InlineData("fingerprint")]
    public async Task Only_the_complete_current_exact_review_can_dispatch_initial_admission(string change)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        var fingerprint = (string)reviewed.Plan["fingerprint"]!;
        switch (change)
        {
            case "selection": machine.Boundary.Input["applicationEnvironment"] = "staging"; break;
            case "operator": machine.Boundary.Repository.Responses["user"] = new(0, """{"id":13,"login":"different"}""", ""); break;
            case "repository":
                machine.Boundary.Repository.Responses["repos/acme/old-name"] =
                    machine.Boundary.Repository.Responses["repos/acme/old-name"] with
                    { StandardOutput = machine.Boundary.Repository.Responses["repos/acme/old-name"].StandardOutput.Replace("\"id\":913", "\"id\":914") };
                break;
            case "unreviewed": machine.Boundary.Input["boundaryReview"] = null; break;
            case "fingerprint": fingerprint = new string('0', 64); break;
        }

        var (exit, result, _) = await machine.ReserveAsync(fingerprint);

        Assert.Equal(1, exit);
        Assert.NotEqual("Reserved", (string?)result["state"]);
        Assert.Null(result["factoryId"]);
        Assert.Empty(machine.Store.Writes);
        machine.Boundary.AssertReadOnly();
    }

    [Theory]
    [InlineData("gh-authority")]
    [InlineData("azure-authority")]
    [InlineData("boundary")]
    [InlineData("claim-revision")]
    public async Task Resume_revalidates_remote_approval_and_current_authority_before_any_additional_effect(string change)
    {
        var machine = new ReservationMachine(new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" });
        var reviewed = await machine.Boundary.ReviewAsync();
        var (_, old, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        var replacement = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));
        if (change == "gh-authority") replacement.Boundary.Repository.Responses["user"] = new(1, "", "secret-provider-error");
        if (change == "azure-authority") replacement.Boundary.Overrides["account list"] = new(1, "", "secret-provider-error");
        if (change == "boundary") replacement.Boundary.Resources[BoundaryPlanFixture.Api]["tags"]!["environment"] = "staging";
        if (change == "claim-revision") replacement.Boundary.OwnerEntries = () =>
            replacement.Store.ListJson().Replace("revision-", "stale-local-revision-");

        var (exit, outcome, terminal) = await replacement.InvokeAsync("resume");

        Assert.Equal(1, exit);
        Assert.Contains((string?)outcome["state"], new[] { "Blocked", "Unknown", "Partial" });
        Assert.Equal(old["operationId"]!.ToJsonString(), outcome["operationId"]!.ToJsonString());
        Assert.Empty(replacement.Store.Writes);
        Assert.DoesNotContain("secret-provider-error", terminal.Output + terminal.Error);
    }

    [Theory]
    [InlineData("product")]
    [InlineData("repository")]
    [InlineData("resource")]
    public async Task Independent_concurrent_contenders_cannot_both_reserve_overlapping_owner_identity_or_resources(string overlap)
    {
        var store = new ReservationTestStore();
        var first = new ReservationMachine(store);
        var second = new ReservationMachine(store);
        if (overlap != "product") second.Boundary.Input["productKey"] = "other";
        if (overlap == "resource")
        {
            foreach (var key in new[] { "repos/acme/old-name", "repos/acme/renamed" })
                second.Boundary.Repository.Responses[key] = second.Boundary.Repository.Responses[key] with
                {
                    StandardOutput = second.Boundary.Repository.Responses[key].StandardOutput.Replace("\"id\":913", "\"id\":914"),
                };
            second.Boundary.Input["applicationResourceIds"]![0] = BoundaryPlanFixture.Api.ToUpperInvariant() + "/";
        }
        var firstPlan = await first.Boundary.ReviewAsync();
        var secondPlan = await second.Boundary.ReviewAsync();
        var arrivals = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforePut = async (key, create) =>
        {
            if (create && key.StartsWith("dsf/onboarding/products/"))
            {
                if (Interlocked.Increment(ref arrivals) == 2) both.SetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        var results = await Task.WhenAll(
            first.ReserveAsync((string)firstPlan.Plan["fingerprint"]!),
            second.ReserveAsync((string)secondPlan.Plan["fingerprint"]!));

        Assert.Single(results, r => r.Exit == 0 && (string?)r.Result["state"] == "Reserved");
        Assert.Single(results, r => r.Exit == 1 && (string?)r.Result["state"] == "Blocked");
        var persisted = JsonNode.Parse(store.Export())!.AsObject();
        Assert.Equal(overlap == "product" ? 1 : 2, persisted.Count(r => r.Key.StartsWith("dsf/onboarding/products/")));
        Assert.Single(persisted, r => r.Key == "dsf/onboarding/repositories/913");
        Assert.All(store.Writes.Where(w => w.IfNoneMatch == "*"), w => Assert.Null(w.IfMatch));
        first.Boundary.AssertReadOnly();
        second.Boundary.AssertReadOnly();
    }

    [Fact]
    public async Task Resuming_a_verified_complete_current_reservation_does_not_rewrite_terminal_state()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        var replacement = new ReservationMachine(ReservationTestStore.Restore(machine.Store.Export()));

        var (exit, result, terminal) = await replacement.InvokeAsync("resume");

        Assert.True(exit == 0, terminal.Output + terminal.Error);
        Assert.Equal("Reserved", (string?)result["state"]);
        Assert.Empty(replacement.Store.Writes);
    }

    [Fact]
    public async Task Lost_initial_admission_response_keeps_identity_and_requires_exact_bytes_to_repair_incomplete_plan_upload()
    {
        var store = new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/products/widget" };
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();
        var (_, uncertain, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal("Unknown", (string?)uncertain["state"]);
        Assert.Single(store.Writes);
        var replacement = new ReservationMachine(ReservationTestStore.Restore(store.Export()));
        var (blocked, _, _) = await replacement.InvokeAsync("resume");
        Assert.Equal(1, blocked);
        Assert.Empty(replacement.Store.Writes);
        var input = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(input, machine.Boundary.Input.ToJsonString());

            var (exit, repaired, terminal) = await replacement.InvokeAsync("resume", "--input", input);

            Assert.True(exit == 0, terminal.Output + terminal.Error);
            Assert.Equal("Reserved", (string?)repaired["state"]);
            Assert.Equal(uncertain["factoryId"]!.ToJsonString(), repaired["factoryId"]!.ToJsonString());
            Assert.Equal(uncertain["operationId"]!.ToJsonString(), repaired["operationId"]!.ToJsonString());
            Assert.DoesNotContain(replacement.Store.Writes, w => w.Key == "dsf/onboarding/products/widget" && w.IfNoneMatch == "*");
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task A_known_rejection_is_durably_distinct_from_a_lost_response_and_retains_admission()
    {
        var store = new ReservationTestStore { RejectKey = "dsf/onboarding/repositories/913" };
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();
        var (exit, outcome, terminal) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal(1, exit);
        Assert.Equal("Failed", (string?)outcome["state"]);
        var replacement = new ReservationMachine(ReservationTestStore.Restore(store.Export()));

        var (statusExit, status, _) = await replacement.InvokeAsync("status");

        Assert.Equal(1, statusExit);
        Assert.Equal("Failed", (string?)status["state"]);
        Assert.Equal(outcome["operationId"]!.ToJsonString(), status["operationId"]!.ToJsonString());
        Assert.Equal(1, (int?)status["confirmedClaims"]);
        Assert.NotNull(status["pendingIntent"]);
        Assert.DoesNotContain("secret-provider", terminal.Output + terminal.Error);
        Assert.Empty(replacement.Store.Writes);
    }

    [Fact]
    public async Task Reservation_refuses_an_App_Configuration_token_for_a_different_operator_than_the_reviewed_plan()
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        machine.Boundary.AppConfigActor = "55555555-5555-5555-5555-555555555555";

        var (exit, outcome, terminal) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);

        Assert.Equal(1, exit);
        Assert.Equal("Failed", (string?)outcome["state"]);
        Assert.Empty(machine.Store.Writes);
        Assert.Equal("{}", machine.Store.Export());
        Assert.DoesNotContain("signature", terminal.Output + terminal.Error);
    }

    [Theory]
    [InlineData("chunk")]
    [InlineData("claim")]
    [InlineData("missing-claim")]
    public async Task Status_never_reports_reserved_from_anchor_counts_when_remote_immutable_records_disagree(string damage)
    {
        var machine = new ReservationMachine(new ReservationTestStore());
        var reviewed = await machine.Boundary.ReviewAsync();
        await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        var serialized = JsonNode.Parse(machine.Store.Export())!.AsObject();
        var key = damage == "chunk" ? serialized.First(p => p.Key.StartsWith("dsf/onboarding/plans/")).Key
            : "dsf/onboarding/repositories/913";
        if (damage == "missing-claim") serialized.Remove(key);
        else
        {
            var record = JsonNode.Parse((string)serialized[key]!)!;
            var value = JsonNode.Parse((string)record["value"]!)!;
            if (damage == "chunk") value["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("changed"));
            else value["identity"]!["operationId"] = Guid.NewGuid().ToString();
            record["value"] = value.ToJsonString();
            serialized[key] = record.ToJsonString();
        }
        var replacement = new ReservationMachine(ReservationTestStore.Restore(serialized.ToJsonString()));

        var (exit, status, _) = await replacement.InvokeAsync("status");

        Assert.Equal(1, exit);
        Assert.Contains((string?)status["state"], new[] { "Blocked", "Unknown" });
        Assert.Empty(replacement.Store.Writes);
    }

    [Fact]
    public async Task Lost_claim_response_resumes_same_approved_identity_after_machine_loss_without_repeating_confirmed_writes()
    {
        var store = new ReservationTestStore { LoseResponseForKey = "dsf/onboarding/repositories/913" };
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();
        var (exit, interrupted, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);
        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)interrupted["state"]);
        Assert.Single(store.Writes, w => w.Key == "dsf/onboarding/repositories/913");
        var replacement = new ReservationMachine(ReservationTestStore.Restore(store.Export()));

        var (resumedExit, resumed, terminal) = await replacement.InvokeAsync("resume");

        Assert.True(resumedExit == 0, terminal.Output + terminal.Error);
        Assert.Equal("Reserved", (string?)resumed["state"]);
        Assert.Equal(interrupted["factoryId"]!.ToJsonString(), resumed["factoryId"]!.ToJsonString());
        Assert.Equal(interrupted["operationId"]!.ToJsonString(), resumed["operationId"]!.ToJsonString());
        Assert.DoesNotContain(replacement.Store.Writes, w => w.Key == "dsf/onboarding/repositories/913" || w.Key.StartsWith("dsf/onboarding/plans/"));
        Assert.Equal(5, (int?)resumed["confirmedClaims"]);
        replacement.Boundary.AssertReadOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_resource_claim_lost_response_recovers_with_stable_steps_despite_resource_and_hash_order_inversion(bool committed)
    {
        const string resourcePrefix = "dsf/onboarding/resources/";
        const string apiKey = resourcePrefix + "745a407aaca9f9fbe55457f6ebb9949e0a123276a78baad891729c6d751600c0";
        const string dataKey = resourcePrefix + "14406275b036284866acd0ab65bcb72093443b8016affef9231eee7bc06df13f";
        const string rootKey = "dsf/onboarding/products/widget";
        var store = new ReservationTestStore();
        if (committed)
            store.LoseResponseWhen = (key, _) => key.StartsWith(resourcePrefix, StringComparison.Ordinal);
        else
            store.BeforePut = (key, _) =>
            {
                if (key.StartsWith(resourcePrefix, StringComparison.Ordinal)) store.LoseBeforeCommit = true;
                return Task.CompletedTask;
            };
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();
        Assert.Equal(0, reviewed.Exit);
        var claims = reviewed.Plan["ownerAdmission"]!["proposedClaims"]!.AsArray()
            .Where(c => (string?)c!["kind"] == "resource").ToArray();
        Assert.Equal(BoundaryPlanFixture.Api.ToLowerInvariant(), (string?)claims[0]!["target"]);
        Assert.Equal(BoundaryPlanFixture.Data.ToLowerInvariant(), (string?)claims[1]!["target"]);
        Assert.Equal(apiKey, (string?)claims[0]!["key"]);
        Assert.Equal(dataKey, (string?)claims[1]!["key"]);
        Assert.True(StringComparer.Ordinal.Compare((string?)claims[0]!["target"], (string?)claims[1]!["target"]) < 0);
        Assert.True(StringComparer.Ordinal.Compare(apiKey, dataKey) > 0);

        var (exit, interrupted, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);

        Assert.Equal(1, exit);
        Assert.Equal("Unknown", (string?)interrupted["state"]);
        var attempt = Assert.Single(store.Writes, w => w.Key.StartsWith(resourcePrefix, StringComparison.Ordinal));
        var persisted = store.Export();
        var before = JsonNode.Parse(persisted)!.AsObject();
        Assert.Equal(committed, before.ContainsKey(attempt.Key));
        var root = JsonNode.Parse((string)JsonNode.Parse((string)before[rootKey]!)!["value"]!)!;
        var intent = root["intent"]!;
        Assert.Equal(attempt.Key, (string?)intent["key"]);
        Assert.Equal("\0", (string?)intent["label"]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(attempt.Value))),
            (string?)intent["valueSha256"]);
        Assert.Equal((int)root["planChunks"]! + 1, (int?)intent["step"]);

        var replacement = new ReservationMachine(ReservationTestStore.Restore(persisted));
        replacement.Store.BeforePut = (key, _) =>
        {
            if (key == attempt.Key)
            {
                var current = JsonNode.Parse(replacement.Store.Export())!;
                var currentRoot = JsonNode.Parse((string)JsonNode.Parse((string)current[rootKey]!)!["value"]!)!;
                Assert.True(JsonNode.DeepEquals(intent, currentRoot["intent"]), "The pending intent must remain unchanged before redispatch.");
            }
            return Task.CompletedTask;
        };

        var (resumedExit, resumed, terminal) = await replacement.InvokeAsync("resume");

        Assert.True(resumedExit == 0, terminal.Output + terminal.Error);
        Assert.Equal("Reserved", (string?)resumed["state"]);
        Assert.False((bool)resumed["isProvisioned"]!);
        Assert.Equal(interrupted["factoryId"]!.ToJsonString(), resumed["factoryId"]!.ToJsonString());
        Assert.Equal(interrupted["operationId"]!.ToJsonString(), resumed["operationId"]!.ToJsonString());
        Assert.Equal(5, (int?)resumed["confirmedClaims"]);
        Assert.Equal(dataKey, attempt.Key);
        Assert.Equal(committed ? 0 : 1, replacement.Store.Writes.Count(w => w.Key == attempt.Key));
        Assert.DoesNotContain(replacement.Store.Writes,
            w => w.Key == rootKey && w.IfNoneMatch == "*" || w.Key.StartsWith("dsf/onboarding/plans/", StringComparison.Ordinal)
                || w.Key == "dsf/onboarding/repositories/913");
        var after = JsonNode.Parse(replacement.Store.Export())!.AsObject();
        foreach (var record in before.Where(r => r.Key != rootKey))
            Assert.Equal((string?)record.Value, (string?)after[record.Key]);
        foreach (var write in replacement.Store.Writes.Where(w => w.Key != rootKey))
        {
            Assert.Equal("*", write.IfNoneMatch);
            var owner = JsonNode.Parse(write.Value)!["identity"]!;
            Assert.Equal(interrupted["factoryId"]!.ToJsonString(), owner["factoryId"]!.ToJsonString());
            Assert.Equal(interrupted["operationId"]!.ToJsonString(), owner["operationId"]!.ToJsonString());
        }
        machine.Boundary.AssertReadOnly();
        replacement.Boundary.AssertReadOnly();
    }

    [Fact]
    public async Task Reviewed_operator_reserves_identity_inspectable_from_an_independent_machine_without_provisioning()
    {
        var store = new ReservationTestStore();
        var machine = new ReservationMachine(store);
        var reviewed = await machine.Boundary.ReviewAsync();

        var (exit, reservation, _) = await machine.ReserveAsync((string)reviewed.Plan["fingerprint"]!);

        Assert.Equal(0, exit);
        Assert.Equal("Reserved", (string?)reservation["state"]);
        Assert.False((bool)reservation["isProvisioned"]!);
        Assert.NotEqual(Guid.Empty, Guid.Parse((string)reservation["factoryId"]!));
        Assert.NotEqual(Guid.Empty, Guid.Parse((string)reservation["operationId"]!));
        var other = new ReservationMachine(ReservationTestStore.Restore(store.Export()));
        var (statusExit, status, _) = await other.InvokeAsync("status");
        Assert.Equal(0, statusExit);
        Assert.Equal(reservation["factoryId"]!.ToJsonString(), status["factoryId"]!.ToJsonString());
        Assert.Equal(reservation["operationId"]!.ToJsonString(), status["operationId"]!.ToJsonString());
        Assert.Equal("Reserved", (string?)status["state"]);
        Assert.Equal("widget", (string?)status["productKey"]);
        Assert.Equal(913, (long?)status["repositoryId"]);
        Assert.Equal(5, (int?)status["confirmedClaims"]);
        Assert.False((bool)status["hasRegistryEffects"]!);
        Assert.Empty(other.Store.Writes);
        Assert.All(store.Writes, write => Assert.StartsWith("dsf/onboarding/", write.Key));
        machine.Boundary.AssertReadOnly();
        other.Boundary.AssertReadOnly();
    }

    private static void MalformReceipt(JsonNode record, string field) =>
        record[field] = field == "value" ? new JsonObject { ["unexpected"] = MalformedReceiptMarker } : JsonValue.Create(MalformedReceiptMarker);
}

internal sealed class ReservationMachine
{
    public BoundaryPlanFixture Boundary { get; } = new();
    public ReservationTestStore Store { get; }
    public CancellationToken Token { get; init; }

    public ReservationMachine(ReservationTestStore store)
    {
        Store = store;
        Boundary.OwnerEntries = store.ListJson;
        Boundary.Repository.ReservationHttp = new HttpClient(store.Client());
        Boundary.Repository.ReservationClock = new ReservationClock();
        Boundary.Repository.BoundaryAzureRunner = Boundary;
    }

    public void SelectOtherProduct()
    {
        Boundary.Input["productKey"] = "other";
        Boundary.Input["factoryResourceGroup"] = "other-decide";
        foreach (var key in new[] { "repos/acme/old-name", "repos/acme/renamed" })
            Boundary.Repository.Responses[key] = Boundary.Repository.Responses[key] with
            {
                StandardOutput = Boundary.Repository.Responses[key].StandardOutput.Replace("\"id\":913", "\"id\":914"),
            };
        var data = BoundaryPlanFixture.Data + "other";
        var logs = BoundaryPlanFixture.Logs + "other";
        Boundary.Resources[data] = BoundaryPlanFixture.Resource(data, "Microsoft.Storage/storageAccounts");
        Boundary.Resources[logs] = BoundaryPlanFixture.Resource(logs, "Microsoft.OperationalInsights/workspaces");
        Boundary.Input["applicationResourceIds"] = new JsonArray(data);
        Boundary.Input["evidenceBackends"]![0]!["resourceId"] = logs;
    }

    public async Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> ReserveAsync(string fingerprint)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Boundary.Input.ToJsonString());
            return await InvokeAsync("reserve", "--input", file, "--reviewed-fingerprint", fingerprint);
        }
        finally { File.Delete(file); }
    }

    public async Task<(int Exit, JsonNode Result, ScriptedTerminal Terminal)> InvokeAsync(string action, params string[] extra)
    {
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        var locator = action == "reserve" ? Array.Empty<string>() : new[]
        {
            "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
            "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription,
        };
        var exit = await Boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", action, .. locator, .. extra, "--json"], Token);
        return (exit, string.IsNullOrWhiteSpace(terminal.Output) ? new JsonObject() : JsonNode.Parse(terminal.Output)!, terminal);
    }
}

internal sealed class ReservationClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-15T23:00:00Z");
}

// Each client exchanges serialized HTTP documents; no domain-object aliases cross the boundary.
internal sealed class ReservationTestStore
{
    private readonly object gate = new();
    private Dictionary<string, string> records = [];
    private int revision;
    private Dictionary<string, string>? valueSnapshot;
    public string? LoseResponseForKey { get; set; }
    public Func<string, string, bool>? LoseResponseWhen { get; set; }
    public bool LoseBeforeCommit { get; set; }
    public string? RejectKey { get; set; }
    public string? RejectDeleteKey { get; set; }
    public string? RejectReadKey { get; set; }
    public Func<string, bool, Task>? BeforePut { get; set; }
    public Action<string, string>? AfterPut { get; set; }
    public Action<HttpMethod, JsonNode>? ModifyResponse { get; set; }
    public List<(string Key, string? IfMatch, string? IfNoneMatch, string Value)> Writes { get; } = [];
    public HttpMessageHandler Client() => new ClientHandler(this);
    public string Export() { lock (gate) return JsonSerializer.Serialize(records); }
    public void Remove(string key) { lock (gate) { records.Remove(key); valueSnapshot = null; } }
    public void Seed(string key, string value)
    {
        lock (gate)
        {
            records[key] = new JsonObject
            {
                ["key"] = key,
                ["label"] = null,
                ["value"] = value,
                ["content_type"] = OwnerReservationStore.ContentType,
                ["etag"] = "revision-" + ++revision,
                ["locked"] = false,
                ["last_modified"] = "2026-09-15T23:00:00Z",
                ["tags"] = new JsonObject(),
            }.ToJsonString();
            valueSnapshot = null;
        }
    }
    public Dictionary<string, string> Values()
    {
        lock (gate)
        {
            valueSnapshot ??= records.ToDictionary(p => p.Key, p => (string)JsonNode.Parse(p.Value)!["value"]!, StringComparer.Ordinal);
            return new(valueSnapshot, StringComparer.Ordinal);
        }
    }
    public void Change(string key, Func<string, string> change)
    {
        lock (gate)
        {
            var record = JsonNode.Parse(records[key])!;
            record["value"] = change((string)record["value"]!);
            record["etag"] = "revision-" + ++revision;
            records[key] = record.ToJsonString();
            valueSnapshot = null;
        }
    }
    public static ReservationTestStore Restore(string persisted)
    {
        var records = JsonSerializer.Deserialize<Dictionary<string, string>>(persisted)!;
        var maximum = records.Values.Select(value => (string?)JsonNode.Parse(value)?["etag"])
            .Select(etag => etag?.StartsWith("revision-", StringComparison.Ordinal) == true
                && int.TryParse(etag["revision-".Length..], out var number) ? number : 0).DefaultIfEmpty().Max();
        return new() { records = records, revision = Math.Max(1000, maximum) };
    }
    public string ListJson()
    {
        lock (gate)
            return "[" + string.Join(",", records.Values) + "]";
    }

    private sealed class ClientHandler(ReservationTestStore store) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.RequestUri!.Host == "owner.azconfig.io"
                || request.RequestUri.Host.StartsWith("dsfo-", StringComparison.Ordinal) && request.RequestUri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal));
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/kv")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var filter = query["key"].ToString();
                Assert.EndsWith("*", filter);
                Assert.Contains(query["label"].ToString(), new[] { "*", "\0" });
                var projection = query.GetValueOrDefault("$select").ToString();
                var keyOnly = projection == "key";
                Assert.True(keyOnly || string.IsNullOrEmpty(projection));
                lock (store.gate)
                    return Reply(HttpStatusCode.OK, new JsonObject { ["items"] = new JsonArray(store.records.Keys
                        .Where(key => key.StartsWith(filter[..^1], StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal)
                        .Select(key => keyOnly ? (JsonNode)new JsonObject { ["key"] = key } : JsonNode.Parse(store.records[key])!).ToArray()) });
            }
            var key = Uri.UnescapeDataString(request.RequestUri.AbsolutePath["/kv/".Length..]);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Put && store.BeforePut is not null)
                await store.BeforePut(key, request.Headers.Contains("If-None-Match"));
            lock (store.gate)
            {
                var current = store.records.TryGetValue(key, out var serialized) ? JsonNode.Parse(serialized)! : null;
                if (request.Method == HttpMethod.Get)
                {
                    if (key == store.RejectReadKey) return Reply(HttpStatusCode.Forbidden, null);
                    if (current is not null) store.ModifyResponse?.Invoke(request.Method, current);
                    return Reply(current is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, current);
                }
                if (request.Method == HttpMethod.Delete)
                {
                    if (key == store.RejectDeleteKey) return Reply(HttpStatusCode.Forbidden, null);
                    var deleteMatch = request.Headers.TryGetValues("If-Match", out var deleteMatches)
                       ? deleteMatches.Single() : null;
                    Assert.NotNull(deleteMatch);
                    if (current is null) return Reply(HttpStatusCode.NotFound, null);
                    if (deleteMatch != "\"" + (string?)current["etag"] + "\"")
                        return Reply(HttpStatusCode.PreconditionFailed, null);
                    store.records.Remove(key);
                    store.valueSnapshot = null;
                    return Reply(HttpStatusCode.NoContent, null);
                }
                Assert.Equal(HttpMethod.Put, request.Method);
                var match = request.Headers.TryGetValues("If-Match", out var matches) ? matches.Single() : null;
                var none = request.Headers.TryGetValues("If-None-Match", out var nones) ? nones.Single() : null;
                var incoming = JsonNode.Parse(body!)!;
                store.Writes.Add((key, match, none, (string)incoming["value"]!));
                Assert.True(none == "*" || match is not null, "Every registry write must be conditional.");
                if (store.LoseBeforeCommit) throw new HttpRequestException("secret-lost-response");
                if (key == store.RejectKey) return Reply(HttpStatusCode.Forbidden, null);
                if (none == "*" && current is not null || match is not null && match != "\"" + (string?)current?["etag"] + "\"")
                    return Reply(HttpStatusCode.PreconditionFailed, null);
                var record = new JsonObject
                {
                    ["key"] = key,
                    ["label"] = null,
                    ["value"] = (string)incoming["value"]!,
                    ["content_type"] = (string?)incoming["content_type"],
                    ["etag"] = "revision-" + ++store.revision,
                    ["locked"] = false,
                    ["last_modified"] = "2026-09-15T23:00:00Z",
                    ["tags"] = new JsonObject(),
                };
                store.records[key] = record.ToJsonString();
                store.valueSnapshot = null;
                store.AfterPut?.Invoke(key, (string)incoming["value"]!);
                if (store.LoseResponseForKey == key || store.LoseResponseWhen?.Invoke(key, (string)incoming["value"]!) == true)
                {
                    store.LoseResponseForKey = null;
                    throw new HttpRequestException("secret-provider-detail-do-not-print");
                }
                store.ModifyResponse?.Invoke(request.Method, record);
                return Reply(HttpStatusCode.OK, record);
            }
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, JsonNode? record)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(record?.ToJsonString() ?? "{}", Encoding.UTF8, "application/vnd.microsoft.appconfig.kv+json"),
            };
            if (record?["etag"] is { } etag) response.Headers.TryAddWithoutValidation("ETag", "\"" + etag.GetValue<string>() + "\"");
            return response;
        }
    }
}
