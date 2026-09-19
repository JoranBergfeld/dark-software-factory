using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryActivationTests
{
    internal const string Prefix = "dsf/factory/audit/activation/";
    internal const string Root = Prefix + "current";

    [Theory]
    [InlineData("GO,GO,GO", "Proceed")]
    [InlineData("GO,GO,NO-GO", "Escalate")]
    [InlineData("NO-GO,NO-GO,NO-GO", "Kill")]
    public async Task Exact_current_complete_preview_can_retain_operator_approval_but_cannot_claim_activation(string votes, string disposition)
    {
        using var transport = await ReadyAsync(votes);
        var machine = transport.Scenario.Machine;
        var operation = Guid.NewGuid().ToString();
        var before = transport.Scenario.Arm.Product.Export();
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var models = transport.EvaluationInference.Count;
        var progress = System.Text.Json.JsonSerializer.Serialize(transport.Scenario.Source.Source!.Progress);
        var requests = transport.Requests.Count;
        var review = await machine.InvokeAsync("activate", "--operation", operation);
        Assert.True(review.Exit == 0, review.Terminal.Output + review.Terminal.Error);
        Assert.Equal("Reviewed", (string?)review.Result["state"]);
        Assert.True((bool)review.Result["currentReady"]!);
        Assert.False((bool)review.Result["isActivated"]!);
        Assert.False((bool)review.Result["historicallyCompleted"]!);
        Assert.False((bool)review.Result["firstConfirmedFiling"]!);
        Assert.Equal(disposition, (string?)review.Result["evaluation"]!["proof"]!["candidates"]![0]!["disposition"]);
        Assert.NotEmpty((string)review.Result["evaluation"]!["run"]!["proposals"]![0]!["intendedPublication"]!["body"]!);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        var approved = await machine.InvokeAsync("activate", "--operation", operation,
            "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal(1, approved.Exit);
        Assert.Equal("ApprovalOnly", (string?)approved.Result["state"]);
        Assert.True((bool)approved.Result["approvalRetained"]!);
        Assert.True((bool)approved.Result["approvalCurrent"]!);
        Assert.True((bool)approved.Result["currentReady"]!);
        Assert.False((bool)approved.Result["isActivated"]!);
        Assert.False((bool)approved.Result["historicallyCompleted"]!);
        Assert.False((bool)approved.Result["firstConfirmedFiling"]!);
        Assert.Contains("Live", (string?)approved.Result["promotionBlockedBy"]);
        Assert.Equal(transport.Manifest.Identity.AzureActorId, (string?)approved.Result["approval"]!["approvedBy"]);
        Assert.NotNull(approved.Result["approval"]!["approvedAt"]);
        Assert.Equal(models, transport.EvaluationInference.Count);
        Assert.Equal(progress, System.Text.Json.JsonSerializer.Serialize(transport.Scenario.Source.Source!.Progress));
        Assert.DoesNotContain(transport.Requests.Skip(requests), r => r.Method is "DELETE" or "PATCH"
            || r.Method == "PUT" && (r.Uri.Host != $"dsfo-{transport.Manifest.Identity.FactoryId:N}.azconfig.io"
                || !Uri.UnescapeDataString(r.Uri.AbsolutePath).StartsWith("/kv/" + Prefix, StringComparison.Ordinal))
            || r.Method != "GET" && r.Uri.Host is "api.github.com" or "github.com"
                && !r.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal));
        Assert.Equal(native, transport.SeedDocuments);
        var current = transport.Values();
        Assert.Equal("Inactive", (string?)JsonNode.Parse(current[ProductConfigurationKeys.FactoryManifest])!["publication"]);
        Assert.Equal(before, BeforeWithoutActivation(transport));
        var saved = transport.Scenario.Arm.Product.Export();
        transport.Restart();
        ConnectAudit(transport);
        var inspected = await machine.InvokeAsync("activate", "--status");
        Assert.True(inspected.Exit == 0, inspected.Terminal.Output + inspected.Terminal.Error);
        Assert.Equal("ApprovalOnly", (string?)inspected.Result["state"]);
        Assert.True((bool)inspected.Result["approvalCurrent"]!);
        Assert.Equal(saved, transport.Scenario.Arm.Product.Export());
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Equal(models, transport.EvaluationInference.Count);
        if (disposition != "Proceed") return;
        var records = new List<JsonNode?>();
        for (var page = 0; ; page++)
        {
            var audit = await machine.InvokeAsync("audit", "--page", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(audit.Exit == 0, audit.Terminal.Output + audit.Terminal.Error);
            records.AddRange(audit.Result["records"]!.AsArray());
            Assert.False((bool)audit.Result["currentAuthorization"]!);
            if (audit.Result["nextPage"] is null) break;
        }
        var root = records.Single(r => (string?)r!["identity"]!["key"] == Root)!;
        Assert.Contains(root["references"]!.AsArray(), r => (string?)r!["key"] == Prefix + Guid.Parse(operation).ToString("N") + "/approval");
        var reviewRecord = records.Single(r => (string?)r!["identity"]!["key"] == Prefix + Guid.Parse(operation).ToString("N") + "/review")!;
        Assert.Contains(reviewRecord["references"]!.AsArray(), r => (string?)r!["store"] == "Preview");
        Assert.Contains(reviewRecord["references"]!.AsArray(), r => (string?)r!["store"] == "Run");
        Assert.Contains(reviewRecord["references"]!.AsArray(), r => (string?)r!["store"] == "Analysis"
            && ((string)r!["key"]!).Contains("/steps/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-operation")]
    [InlineData("empty-operation")]
    [InlineData("invalid-approval")]
    [InlineData("conflicting")]
    [InlineData("promotion-missing-operation")]
    public async Task Redirected_activation_requires_complete_explicit_input_before_access(string invalid)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        string[] action = invalid switch
        {
            "empty-operation" => ["--operation", Guid.Empty.ToString()],
            "invalid-approval" => ["--operation", Guid.NewGuid().ToString(), "--approve", "yes"],
            "conflicting" => ["--status", "--approve", new string('a', 64)],
            "promotion-missing-operation" => ["--promote"],
            _ => [],
        };
        var exit = await CliApplication.InvokeAsync(["onboard", "decide", "activate", "--json",
            "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
            "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription, .. action],
            CancellationToken.None, terminal);
        Assert.Equal(1, exit);
        Assert.Contains("\"state\": \"Blocked\"", terminal.Output);
        Assert.Empty(terminal.Prompts);
        Assert.Empty(terminal.Error);
    }

    internal static async Task<PreviewPreparationTransport> ReadyAsync(string votes = "GO,GO,GO", bool publisher = false,
        TimeSpan? applicationRecordAge = null)
    {
        var transport = await PreviewEvaluationTests.ConfiguredAsync(publisher: publisher);
        if (applicationRecordAge is not null)
            transport.AlterApplication = body =>
            {
                body["tables"]![0]!["rows"]![0]![0] = (transport.Scenario.Source.Source!.Clock.Now - applicationRecordAge.Value).ToString("O");
                return body;
            };
        var jury = votes.Split(',');
        transport.AlterCompletion = (prompt, text) =>
        {
            for (var i = 0; i < 3; i++)
                if (prompt.Contains($"As juror 'juror-{i + 1}'", StringComparison.Ordinal))
                    return jury[i] + ": The genuine scoped application record and bounded proposal were independently reviewed.";
            return text;
        };
        var evaluated = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True(evaluated.Exit == 0, evaluated.Terminal.Output + evaluated.Terminal.Error);
        transport.AlterApplication = null;
        ConnectAudit(transport);
        return transport;
    }

    internal static void ConnectAudit(PreviewPreparationTransport transport) =>
        transport.Scenario.Machine.Boundary.Repository.ReservationHttp = new HttpClient(new FactoryAuditTests.PreviewAuditTransport(transport))
        { Timeout = Timeout.InfiniteTimeSpan };

    private static string BeforeWithoutActivation(PreviewPreparationTransport transport)
    {
        var copy = ReservationTestStore.Restore(transport.Scenario.Arm.Product.Export());
        foreach (var key in transport.Values().Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal))) copy.Remove(key);
        return copy.Export();
    }
}
