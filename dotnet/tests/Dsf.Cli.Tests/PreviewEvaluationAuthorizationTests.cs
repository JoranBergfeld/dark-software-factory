using Dsf.Cli;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewEvaluationAuthorizationTests
{
    [Theory]
    [InlineData("--evaluate")]
    [InlineData("--evaluation")]
    public async Task Missing_operator_council_token_is_structured_nonsecret_and_has_no_effect(string mode)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var boundary = transport.Scenario.Machine.Boundary;
        boundary.Repository.BoundaryAzureRunner = new RejectCouncilToken(boundary);
        var start = transport.Requests.Count;
        var before = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        var result = await transport.Scenario.Machine.InvokeAsync("preview", [mode, .. PreviewEvaluationTests.Pins(transport)]);
        Assert.NotEqual(0, result.Exit);
        Assert.Equal("Unknown", (string?)result.Result["readiness"]);
        Assert.DoesNotContain("private-operator-token", result.Terminal.Output + result.Terminal.Error);
        Assert.Empty(transport.EvaluationInference);
        Assert.Equal(before, transport.SeedDocuments);
        Assert.DoesNotContain(transport.Requests.Skip(start), r => r.Uri.AbsolutePath is "/evaluate" or "/evaluation");
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offboarding_root_or_history_blocks_operator_preview_evaluation(bool history)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var operation = Guid.NewGuid();
        transport.Scenario.Arm.Product.Seed(
            history ? FactoryOffboarding.HistoryKey(operation) : FactoryOffboarding.Key, "{}");
        var start = transport.Requests.Count;

        var result = await transport.Scenario.Machine.InvokeAsync(
            "preview", ["--evaluation", .. PreviewEvaluationTests.Pins(transport)]);

        Assert.NotEqual(0, result.Exit);
        Assert.Equal("Blocked", (string?)result.Result["readiness"]);
        Assert.Contains("OffboardingOrDetached", (string?)result.Result["reason"]);
        Assert.DoesNotContain(transport.Requests.Skip(start), request => request.Uri.AbsolutePath == "/evaluation");
    }

    private sealed class RejectCouncilToken(IAzureCliRunner next) : IAzureCliRunner
    {
        public Task<AzureCliInvocationResult> RunAsync(IReadOnlyList<string> args, CancellationToken token) =>
            args.Take(2).SequenceEqual(["account", "get-access-token"]) && args.Any(a => a.StartsWith("api://", StringComparison.Ordinal))
                ? Task.FromResult(new AzureCliInvocationResult(1, "", "private-operator-token-denied"))
                : next.RunAsync(args, token);
    }
}
