using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewEvaluationOptionalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_selected_web_source_is_really_consumed_and_cannot_replace_application_evidence(bool failing)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync(web: true);
        Assert.True(transport.WebQueries > 0);
        var calls = transport.WebQueries;
        transport.WebPayload = failing ? """{"webResults":null}""" : """{"webResults":[]}""";
        var start = transport.Requests.Count;
        var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. PreviewEvaluationTests.Pins(transport)]);
        Assert.True(transport.WebQueries > calls);
        Assert.Equal(!failing, (bool)result.Result["currentReady"]!);
        if (!failing)
        {
            Assert.Equal("ReadyAwaitingActivation", (string?)result.Result["readiness"]);
            Assert.Single(result.Result["evaluation"]!["inputs"]!["supplemental"]!["sources"]!.AsArray());
            Assert.Single(result.Result["evaluation"]!["run"]!["evidence"]!.AsArray());
        }
        else Assert.Empty(transport.EvaluationInference);
        Assert.DoesNotContain("private-web-key", result.Terminal.Output);
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, start);
    }
}
