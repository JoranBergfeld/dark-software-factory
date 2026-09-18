using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewEvaluationTests
{
    [Theory]
    [InlineData("GO,GO,GO", "Proceed")]
    [InlineData("GO,GO,NO-GO", "Escalate")]
    [InlineData("GO,NO-GO,NO-GO", "Escalate")]
    [InlineData("NO-GO,NO-GO,NO-GO", "Kill")]
    public async Task Configured_operator_evaluates_actual_council_and_inspects_durable_proposal_without_publication(string votes, string disposition)
    {
        using var transport = await ConfiguredAsync();
        var machine = transport.Scenario.Machine;
        var capability = JsonNode.Parse(transport.Values()[ProductConfigurationKeys.FactoryPreview])!;
        string[] pins = ["--operation", (string)capability["operationId"]!, "--approve", (string)capability["approvalFingerprint"]!];
        var jury = votes.Split(',');
        transport.AlterCompletion = (prompt, text) =>
        {
            for (var i = 0; i < 3; i++)
                if (prompt.Contains($"As juror 'juror-{i + 1}'", StringComparison.Ordinal))
                    return jury[i] + ": The actual scoped request and bounded recommendation were independently evaluated.";
            return text;
        };
        var before = transport.Requests.Count;
        var original = transport.Scenario.Arm.Product.Export();
        var evaluated = await machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.True(evaluated.Exit == 0, evaluated.Terminal.Output + evaluated.Terminal.Error + transport.LastPreviewResponse
            + "\n" + string.Join("\n", transport.HostFailures) + "\n" + string.Join("\n", transport.Requests.TakeLast(6)));
        Assert.Equal("ReadyAwaitingActivation", (string?)evaluated.Result["readiness"]);
        var evaluation = evaluated.Result["evaluation"]!;
        Assert.Single(evaluation["proof"]!["candidates"]!.AsArray());
        Assert.Equal(disposition, (string?)evaluation["proof"]!["candidates"]![0]!["disposition"]);
        Assert.Equal(16, transport.EvaluationInference.Count);
        Assert.Single(evaluation["run"]!["evidence"]!.AsArray());
        Assert.Empty(evaluation["inputs"]!["supplemental"]!["sources"]!.AsArray());
        var proposal = Assert.Single(evaluation["run"]!["proposals"]!.AsArray())!;
        Assert.Equal("Explain the observed application request", (string?)proposal["title"]);
        Assert.Equal(3, proposal["councilReview"]!["juryVerdicts"]!.AsArray().Count);
        Assert.Equal(2, proposal["councilReview"]!["rounds"]!.AsArray().Count);
        Assert.True((bool)proposal["grounding"]!["result"]!["contentSupported"]!);
        Assert.NotEmpty(proposal["intendedPublication"]!["body"]!.GetValue<string>());
        Assert.Equal("dsf:proposal", (string?)Assert.Single(proposal["intendedPublication"]!["labels"]!.AsArray()));
        Assert.Empty(proposal["intendedPublication"]!["assignees"]!.AsArray());
        Assert.Empty(evaluation["run"]!["filedIssues"]!.AsArray());
        var stored = transport.Scenario.Arm.Product.Export();
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(stored);
        transport.Restart();
        transport.AlterApplication = body =>
        {
            body["tables"]![0]!["rows"]![0]![2] = "newer-genuine-application-record";
            return body;
        };
        var status = await machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.True(status.Exit == 0, status.Terminal.Output + status.Terminal.Error);
        Assert.Equal("ReadyAwaitingActivation", (string?)status.Result["readiness"]);
        Assert.True(JsonNode.DeepEquals(evaluation["proof"], status.Result["evaluation"]!["proof"]));
        Assert.Equal(stored, transport.Scenario.Arm.Product.Export());
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Equal(16, transport.EvaluationInference.Count);
        var duplicate = await machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
        Assert.Equal("ReadyAwaitingActivation", (string?)duplicate.Result["readiness"]);
        Assert.Equal(16, transport.EvaluationInference.Count);
        Assert.Equal(native, transport.SeedDocuments);
        Assert.Equal(original, stored);
        AssertNoForbiddenEffects(transport, before);
    }

    internal static string[] Pins(PreviewPreparationTransport transport) =>
        ["--operation", transport.Manifest.PreviewOperationId!.Value.ToString("D"), "--approve", transport.Manifest.PreviewApprovalFingerprint!];

    internal static void AssertNoForbiddenEffects(PreviewPreparationTransport transport, int start) =>
        Assert.DoesNotContain(transport.Requests.Skip(start), r => r.Method is "DELETE" or "PATCH"
            || r.Method == "PUT" && !r.Uri.Host.EndsWith("-cosmos.documents.azure.com", StringComparison.Ordinal)
                && !(r.Uri.Host == new Uri(transport.Scenario.Source.Source!.ProgressEndpoint).Host
                    && Uri.UnescapeDataString(r.Uri.AbsolutePath).StartsWith("/kv/intent/", StringComparison.Ordinal)
                    && Uri.UnescapeDataString(r.Uri.AbsolutePath).EndsWith("/retention", StringComparison.Ordinal))
            || r.Method != "GET" && r.Uri.Host is "api.github.com" or "github.com"
                && !r.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal));

    internal static async Task<PreviewPreparationTransport> ConfiguredAsync(bool web = false, bool foundry = false)
    {
        var transport = await PreviewPublicationTests.PreparedAsync(web, foundry);
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("preview", "--configure");
        Assert.True(review.Exit == 0, (string?)review.Result["detail"] + "\n" + string.Join("\n", transport.Requests.TakeLast(8)));
        var seeded = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(seeded.Exit == 0, (string?)seeded.Result["detail"] + "\n" + string.Join("\n", transport.HostFailures));
        var configured = await machine.InvokeAsync("preview", "--configure", "--approve", (string)seeded.Result["approvalFingerprint"]!);
        Assert.True(configured.Exit == 0, configured.Terminal.Output);
        Assert.Equal(transport.Manifest.PreviewApprovalFingerprint, (string?)configured.Result["evaluationRequest"]?["approvalFingerprint"]);
        return transport;
    }
}
