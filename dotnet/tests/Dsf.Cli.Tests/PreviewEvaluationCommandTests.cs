using System.Text.Json.Nodes;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewEvaluationCommandTests
{
    [Fact]
    public async Task Configuration_discloses_exact_explicit_evaluation_request_without_starting_it()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        Assert.Empty(transport.EvaluationInference);
        Assert.Single(transport.SeedDocuments);
    }

    [Theory]
    [InlineData("--owner-appconfig-endpoint")]
    [InlineData("--product")]
    [InlineData("--tenant")]
    [InlineData("--subscription")]
    [InlineData("--operation")]
    [InlineData("--approve")]
    [InlineData("invalid-operation")]
    [InlineData("conflicting")]
    public async Task Redirected_evaluation_requires_explicit_complete_valid_input_before_any_provider_access(string missing)
    {
        var fixture = new BoundaryPlanFixture();
        var args = new List<string> { "onboard", "decide", "preview", "--evaluate", "--json",
            "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget", "--tenant", BoundaryPlanFixture.Tenant,
            "--subscription", BoundaryPlanFixture.Subscription, "--operation", "ab000000-0000-0000-0000-000000000214",
            "--approve", new string('a', 64) };
        var index = args.IndexOf(missing);
        if (index >= 0) args.RemoveRange(index, 2);
        if (missing == "invalid-operation") args[args.IndexOf("--operation") + 1] = "not-an-operation";
        if (missing == "conflicting") args.Add("--configure");
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        var result = await fixture.Repository.InvokeCommandAsync(terminal, args.ToArray());
        Assert.NotEqual(0, result);
        Assert.Empty(terminal.Prompts);
        Assert.Equal("Blocked", (string?)JsonNode.Parse(terminal.Output)!["readiness"]);
        Assert.Empty(fixture.Repository.Invocations);
    }
}
