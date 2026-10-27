using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryCharterCapacityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Maximum_native_responses_fit_the_complete_charter_lifecycle_with_retained_attempts(int rejections)
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        var native = scenario.Machine.Boundary.Repository.NativeCharter!;
        var observed = new List<int>();
        var writes = new List<(string Key, string Value)>();
        scenario.Machine.Boundary.Repository.NativeCharter = (args, input) =>
        {
            var response = native(args, input);
            if (response is null || input is null || args[^1] != "graphql"
                || !response.StandardOutput.StartsWith("HTTP/2.0 200", StringComparison.Ordinal)) return response;
            var text = response.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
            var separator = text.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            var body = JsonNode.Parse(text[separator..])!;
            body["extensions"] = "";
            var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var prefix = body.ToJsonString(json);
            var padding = new StringBuilder();
            while (Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(padding.ToString()) + 5 <= 2048)
                padding.Append("<&'Ω");
            padding.Append('<', 2048 - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(padding.ToString()));
            body["extensions"] = padding.ToString();
            var raw = body.ToJsonString(json);
            observed.Add(Encoding.UTF8.GetByteCount(raw));
            Assert.Equal(2048, observed[^1]);
            return response with { StandardOutput = text[..separator] + raw };
        };
        scenario.RejectEffect = rejections == 0 ? null : 1;
        void LoseResponsePointer()
        {
            if (rejections == 0) return;
            scenario.Arm.Product.LoseResponseWhen = (key, value) => key == "dsf/factory/charter/publication"
                && JsonNode.Parse(value)!["effects"]?.AsArray().LastOrDefault() is { } last
                && (string?)last["state"] == "Dispatched" && last["responseRecord"] is not null;
        }
        void Reconstruct()
        {
            writes.AddRange(scenario.Arm.Product.Writes.Select(w => (w.Key, w.Value)));
            scenario.Reconstruct();
            LoseResponsePointer();
        }
        LoseResponsePointer();
        var result = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        for (var index = 0; index < rejections; index++)
        {
            Assert.Equal("Failed", (string?)result.Result["state"]);
            Reconstruct();
            scenario.RejectEffect = index + 1 == rejections ? null : scenario.GitHubWrites.Count + 1;
            var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
            result = await scenario.Machine.InvokeAsync("charter", "--retry",
                "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!,
                "--approve", (string)review.Result["revision"]!);
        }
        var reconciliations = 0;
        while ((string?)result.Result["state"] == "Unknown" && reconciliations < 3)
        {
            Reconstruct();
            var inspection = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
            var count = scenario.GitHubWrites.Count;
            result = await scenario.Machine.InvokeAsync("charter", "--reconcile",
                "--operation", (string)inspection.Result["publication"]!["operation"]!, "--revision", (string)inspection.Result["revision"]!);
            Assert.Equal(count, scenario.GitHubWrites.Count);
            reconciliations++;
            if ((string?)result.Result["state"] == "Partial")
                result = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        }
        Assert.True((string?)result.Result["state"] == "Published", result.Terminal.Output);
        Assert.Equal(3, observed.Count);
        Assert.Equal(3 + rejections, scenario.GitHubWrites.Count);
        Assert.Equal(rejections, result.Result["publication"]!["effects"]![0]!["previousAttempts"]!.AsArray().Count);
        Assert.Equal(rejections == 0 ? 0 : 3, reconciliations);
        writes.AddRange(scenario.Arm.Product.Writes.Select(w => (w.Key, w.Value)));
        Assert.All(writes, record =>
            Assert.InRange(OwnerReservationStore.RecordSizeBytes(record.Key, "\0", record.Value), 1, OwnerReservationStore.RecordLimit));
        scenario.Reconstruct();
        var retained = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        Assert.Equal("Published", (string?)retained.Result["state"]);
        Assert.Equal(3 + rejections, scenario.GitHubWrites.Count);
        var records = await AuditAsync(scenario);
        var root = Assert.Single(records, r => (string?)r!["identity"]?["key"] == "dsf/factory/charter/publication")!;
        var responses = records.Where(r => ((string?)r!["identity"]?["key"])?.Contains("/responses/", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(3, responses.Length);
        Assert.All(responses, record => Assert.Contains(root["references"]!.AsArray(),
            reference => reference!.ToJsonString() == record!["identity"]!.ToJsonString()));
    }

    [Fact]
    public async Task Unacknowledged_response_record_is_discoverable_but_not_adopted_without_original_root_pin()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.LoseResponseWhen = (key, _) => key.Contains("/responses/", StringComparison.Ordinal);
        var interrupted = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Unknown", (string?)interrupted.Result["state"]);
        Assert.Single(scenario.Arm.Product.Writes, w => w.Key.Contains("/responses/", StringComparison.Ordinal));
        scenario.Reconstruct();
        var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        Assert.Equal("Unknown", (string?)inspected.Result["state"]);
        var before = scenario.Arm.Product.Export();
        var reconciled = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!);
        Assert.Equal("Unknown", (string?)reconciled.Result["state"]);
        Assert.Equal(before, scenario.Arm.Product.Export());
        Assert.Single(scenario.GitHubWrites);
        var records = await AuditAsync(scenario);
        Assert.Contains(records, r => ((string?)r!["identity"]?["key"])?.Contains("/responses/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Large_legacy_inline_proof_reconciles_but_blocks_next_dispatch_before_capacity_is_exhausted()
    {
        using var scenario = await CharterScenario.ReadyAsync();
        var native = scenario.Machine.Boundary.Repository.NativeCharter!;
        scenario.Machine.Boundary.Repository.NativeCharter = (args, input) =>
        {
            var response = native(args, input);
            if (response is null || input is null || args[^1] != "graphql") return response;
            var separator = response.StandardOutput.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
            var body = JsonNode.Parse(response.StandardOutput[separator..])!;
            body["extensions"] = new string('<', 1050);
            return response with { StandardOutput = response.StandardOutput[..separator]
                + body.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) };
        };
        var review = await scenario.Machine.InvokeAsync("charter");
        scenario.Arm.Product.LoseResponseWhen = (key, value) => key == "dsf/factory/charter/publication"
            && JsonNode.Parse(value)!["effects"] is JsonArray effects && effects.Count > 0 && effects[0]!["responseRecord"] is not null;
        await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        var responseWrite = scenario.Arm.Product.Writes.Single(w => w.Key.Contains("/responses/", StringComparison.Ordinal));
        var record = JsonSerializer.Deserialize<CharterResponseRecord>(responseWrite.Value, FactorySourceValidation.RecordJson)!;
        var base64 = record.ResponseUtf8.Replace('-', '+').Replace('_', '/');
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
        var legacy = new CharterResponseWitness(record.FactoryId, record.RepositoryId, record.Operation, record.Attempt,
            record.ReviewFingerprint, record.RequestSha256, record.Native, raw);
        scenario.Arm.Product.Change("dsf/factory/charter/publication", text =>
        {
            var publication = JsonSerializer.Deserialize<CharterPublication>(text, FactorySourceValidation.RecordJson)!;
            var changed = publication with { Effects = [publication.Effects[0] with { ResponseRecord = null, Response = legacy }] };
            var value = JsonSerializer.Serialize(changed, FactorySourceValidation.RecordJson);
            OwnerReservationStore.CheckSize("dsf/factory/charter/publication", "\0", value);
            return value;
        });
        scenario.Reconstruct();
        var inspected = await scenario.Machine.InvokeAsync("charter", "--inspect-publication");
        Assert.Equal("Unknown", (string?)inspected.Result["state"]);
        var reconciled = await scenario.Machine.InvokeAsync("charter", "--reconcile",
            "--operation", (string)inspected.Result["publication"]!["operation"]!, "--revision", (string)inspected.Result["revision"]!);
        Assert.Equal("Partial", (string?)reconciled.Result["state"]);
        var continued = await scenario.Machine.InvokeAsync("charter", "--publish", "--approve", (string)review.Result["revision"]!);
        Assert.Equal("Partial", (string?)continued.Result["state"]);
        Assert.Contains("no next GitHub dispatch", (string?)continued.Result["detail"]);
        Assert.Single(continued.Result["publication"]!["effects"]!.AsArray());
        Assert.Single(scenario.GitHubWrites);
        Assert.All(scenario.Arm.Product.Writes, w =>
            Assert.InRange(OwnerReservationStore.RecordSizeBytes(w.Key, "\0", w.Value), 1, OwnerReservationStore.RecordLimit));
    }

    private static async Task<IReadOnlyList<JsonNode?>> AuditAsync(CharterScenario scenario)
    {
        using var auditTransport = new FactoryAuditTests.AuditTransport(scenario);
        using var http = new HttpClient(auditTransport);
        scenario.Machine.Boundary.Repository.ReservationHttp = http;
        var audit = await scenario.Machine.InvokeAsync("audit");
        Assert.True(audit.Exit == 0, audit.Terminal.Output);
        var records = audit.Result["records"]!.AsArray().ToList();
        while (audit.Result["nextPage"] is { } page)
        {
            audit = await scenario.Machine.InvokeAsync("audit", "--page", ((int)page).ToString());
            records.AddRange(audit.Result["records"]!.AsArray());
        }
        return records;
    }
}
