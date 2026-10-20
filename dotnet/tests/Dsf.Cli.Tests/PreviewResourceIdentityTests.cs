using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewResourceIdentityTests
{
    [Theory]
    [InlineData("-")]
    [InlineData("/")]
    public async Task Native_Cosmos_seed_and_run_alphabet_survives_configuration_evaluation_and_serialized_inspection(string alphabet)
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        transport.SeedResourceId = alphabet + "wAAAIAAAAABAAAAAAAAAA==";
        transport.RunResourceId = alphabet + "wAAAIAAAAACAAAAAAAAAA==";
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("preview", "--configure");
        Assert.True(review.Exit == 0, review.Terminal.Output);
        var seeded = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(seeded.Exit == 0, (string?)seeded.Result["detail"] + "\n" + string.Join("\n", transport.HostFailures));
        Assert.Equal(transport.SeedResourceId, (string?)seeded.Result["publication"]!["execution"]!["seed"]!["resourceId"]);
        var configured = await machine.InvokeAsync("preview", "--configure", "--approve", (string)seeded.Result["approvalFingerprint"]!);
        Assert.True(configured.Exit == 0, configured.Terminal.Output);
        var pins = PreviewEvaluationTests.Pins(transport);
        var result = await machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.True(result.Exit == 0, result.Terminal.Output);
        Assert.Equal(transport.RunResourceId, (string?)result.Result["evaluation"]!["receipt"]!["resourceId"]);
        Assert.Equal(transport.RunResourceId, (string?)result.Result["evaluation"]!["proof"]!["runResourceId"]);
        var calls = transport.EvaluationInference.Count;
        transport.Restart();
        var status = await machine.InvokeAsync("preview", ["--evaluation", .. pins]);
        Assert.Equal("ReadyAwaitingActivation", (string?)status.Result["readiness"]);
        Assert.Equal(transport.RunResourceId, (string?)status.Result["evaluation"]!["proof"]!["runResourceId"]);
        Assert.Equal(calls, transport.EvaluationInference.Count);
    }

    [Theory]
    [InlineData(true, "-wAAAA==")]
    [InlineData(true, "-wAAAIAAAAABAAAAAAAAEA==")]
    [InlineData(false, "-wAAAA==")]
    [InlineData(false, "-wAAAIAAAAABAAAAAAAAEA==")]
    public async Task Accepted_native_writes_with_malformed_document_identities_remain_quarantined(bool seed, string rid)
    {
        using var transport = seed ? await PreviewPublicationTests.PreparedAsync() : await PreviewEvaluationTests.ConfiguredAsync();
        var machine = transport.Scenario.Machine;
        string[] command;
        if (seed)
        {
            transport.SeedResourceId = rid;
            var review = await machine.InvokeAsync("preview", "--configure");
            command = ["--configure", "--approve", (string)review.Result["approvalFingerprint"]!];
        }
        else
        {
            transport.RunResourceId = rid;
            command = ["--evaluate", .. PreviewEvaluationTests.Pins(transport)];
        }
        var result = await machine.InvokeAsync("preview", command);
        Assert.NotEqual(0, result.Exit);
        Assert.False((bool)result.Result["currentReady"]!);
        Assert.Empty(transport.EvaluationInference);
        Assert.Equal(1, transport.SeedCreates);
        var retained = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
        transport.Restart();
        var start = transport.Requests.Count;
        var resumed = await machine.InvokeAsync("preview", command);
        Assert.NotEqual(0, resumed.Exit);
        Assert.Equal(retained, transport.SeedDocuments);
        Assert.Equal(1, transport.SeedCreates);
        Assert.Empty(transport.EvaluationInference);
        Assert.DoesNotContain(transport.Requests.Skip(start), r => r.Method is "PUT" or "PATCH" or "DELETE");
    }
}
