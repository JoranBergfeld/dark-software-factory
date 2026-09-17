using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewEvaluationFailureTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("citation")]
    [InlineData("unsupported")]
    [InlineData("juror")]
    [InlineData("round")]
    [InlineData("lost-model")]
    [InlineData("canceled-model")]
    public async Task Real_cli_never_releases_readiness_for_incomplete_or_uncertain_evaluation(string fault)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        transport.AlterCompletion = (prompt, text) =>
        {
            if (fault == "lost-model" && prompt.Contains("As juror 'juror-2'", StringComparison.Ordinal))
                throw new HttpRequestException("private-model-response-lost");
            if (fault == "canceled-model" && prompt.Contains("As juror 'juror-2'", StringComparison.Ordinal))
                throw new OperationCanceledException("private-model-canceled");
            if (fault == "juror" && prompt.Contains("As juror 'juror-2'", StringComparison.Ordinal)) return "No typed judgment.";
            if (fault == "round" && prompt.StartsWith("As the 'cost'", StringComparison.Ordinal)
                && !prompt.Contains("\"PriorRound\":[]", StringComparison.Ordinal)) return "No complete deliberation response.";
            if (prompt.StartsWith("PROPOSAL_SYNTHESIS", StringComparison.Ordinal) && fault is "empty" or "citation")
            {
                var synthesis = JsonNode.Parse(text)!;
                if (fault == "empty") { synthesis["Candidate"] = null; synthesis["Reason"] = "The observation does not support a sufficiently useful candidate."; }
                else synthesis["Candidate"]!["Claims"]![0]!["EvidenceReferences"] = new JsonArray("missing-source");
                return synthesis.ToJsonString();
            }
            if (fault == "unsupported" && prompt.StartsWith("SEMANTIC_GROUNDING", StringComparison.Ordinal))
            {
                var grounding = JsonNode.Parse(text)!;
                grounding["ContentSupported"] = false;
                grounding["Checks"]![0]!["Support"] = "Unsupported";
                return grounding.ToJsonString();
            }
            return text;
        };
        var pins = PreviewEvaluationTests.Pins(transport);
        var start = transport.Requests.Count;
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.NotEqual(0, result.Exit);
        Assert.False((bool)result.Result["currentReady"]!);
        Assert.Null(result.Result["evaluation"]?["proof"]);
        Assert.DoesNotContain("private-model", result.Terminal.Output);
        if (fault is "lost-model" or "canceled-model") Assert.Equal("Unknown", (string?)result.Result["readiness"]);
        var calls = transport.EvaluationInference.Count;
        transport.Restart();
        var status = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.False((bool)status.Result["currentReady"]!);
        var resumed = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
        Assert.False((bool)resumed.Result["currentReady"]!);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }
}

public sealed class PreviewEvaluationInputTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("stale")]
    [InlineData("scope")]
    [InlineData("intent")]
    [InlineData("orphan")]
    [InlineData("grant")]
    [InlineData("config")]
    public async Task Configured_cli_requires_current_real_inputs_before_run_or_model_effects(string fault)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var pins = PreviewEvaluationTests.Pins(transport);
        if (fault == "intent") transport.Scenario.Arm.Product.Remove(ProductConfigurationKeys.CharterAdopted);
        if (fault == "orphan") transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryManifest, text =>
        {
            var manifest = JsonNode.Parse(text)!;
            manifest["supplementalOperationId"] = Guid.NewGuid();
            return manifest.ToJsonString();
        });
        if (fault == "grant") transport.Resources[transport.NativeWrites[^1]]["properties"]!["condition"] = "foreign";
        if (fault == "config") transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryPreview, text =>
        {
            var capability = JsonNode.Parse(text)!;
            capability["configuration"]!["runtimeImage"] = "foreign-image";
            return capability.ToJsonString();
        });
        transport.AlterApplication = body =>
        {
            if (fault == "empty") body["tables"]![0]!["rows"] = new JsonArray();
            if (fault == "stale") body["tables"]![0]!["rows"]![0]![0] = transport.Scenario.Source.Source!.Clock.Now.AddDays(-2).ToString("O");
            if (fault == "scope") body["tables"]![0]!["rows"]![0]![1] = FactoryTelemetryWorkflowTests.Component + "-foreign";
            return body;
        };
        var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var start = transport.Requests.Count;
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.NotEqual(0, result.Exit);
        Assert.False((bool)result.Result["currentReady"]!);
        Assert.Empty(transport.EvaluationInference);
        if (fault is "empty" or "stale" or "scope")
        {
            var coordinator = JsonNode.Parse(Assert.Single(transport.SeedDocuments).Value)!;
            Assert.Null(coordinator["inputs"]);
            Assert.Null(coordinator["run"]);
            Assert.Null(coordinator["pendingRun"]);
            Assert.Empty(coordinator["dispatches"]!.AsArray());
            Assert.Contains((string?)coordinator["admission"]!["state"], new[] { "Released", "Unknown" });
            Assert.False(string.IsNullOrWhiteSpace((string?)coordinator["admission"]!["reason"]));
        }
        else Assert.Equal(native, transport.SeedDocuments);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }
}
