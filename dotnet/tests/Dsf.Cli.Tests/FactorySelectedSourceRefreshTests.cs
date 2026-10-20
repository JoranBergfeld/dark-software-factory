using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class SelectedSourceActivatedFactoryFixture : ActivatedFactoryFixture
{
    protected override bool SelectedSources => true;
}

public sealed class FactorySelectedSourceRefreshTests(SelectedSourceActivatedFactoryFixture fixture)
    : IClassFixture<SelectedSourceActivatedFactoryFixture>
{
    [Fact]
    public async Task Refresh_and_qualified_cache_require_both_selected_sources_without_omission_or_fallback()
    {
        var transport = fixture.Reset();
        FactoryEvidenceRefreshTests.AdvanceDefaultBranch(transport);
        var governing = transport.Scenario.Arm.Product.Export();
        var web = transport.WebPayload;
        var foundry = transport.FoundryPayload;
        var webQueries = transport.WebQueries;
        var foundryQueries = transport.FoundryQueries;
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        for (var phase = 0; phase < 2; phase++)
        {
            var calls = transport.EvaluationInference.Count;
            var retained = transport.Scenario.Source.Source!.Progress.ToDictionary();
            foreach (var selected in new[] { "webiq", "foundryiq" })
            {
                try
                {
                    transport.WebPayload = selected == "webiq" ? """{"webResults":null}""" : web;
                    transport.FoundryPayload = selected == "foundryiq" ? """{"response":null,"activity":[],"references":[]}""" : foundry;
                    using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                        new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
                    Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, selected + ": " + await denied.Content.ReadAsStringAsync());
                    Assert.Equal(calls, transport.EvaluationInference.Count);
                    Assert.Equal(retained, transport.Scenario.Source.Source.Progress);
                    Assert.Equal(phase, transport.IssuePosts);
                }
                finally
                {
                    transport.WebPayload = web;
                    transport.FoundryPayload = foundry;
                }
            }
            if (phase != 0) continue;
            var completed = await fixture.DecideAsync(transport, new FactoryDecisionRequest(Guid.NewGuid()));
            Assert.Equal("ReadyAwaitingActivation", (string?)completed["readiness"]);
            Assert.Equal(new[] { "foundryiq", "webiq" }, completed["evaluation"]!["inputs"]!["supplemental"]!["sources"]!.AsArray()
                .Select(s => (string)s!["evidence"]!["proof"]!["kind"]!).Order());
            Assert.True(transport.WebQueries > webQueries);
            Assert.True(transport.FoundryQueries > foundryQueries);
            Assert.Equal(1, transport.IssuePosts);
        }
        transport.WebPayload = """{"webResults":[{"url":"https://provider.example","content":"Current public scheduling accessibility guidance."}]}""";
        try
        {
            var updated = await fixture.DecideAsync(transport, new FactoryDecisionRequest(Guid.NewGuid()));
            Assert.Equal("ReadyAwaitingActivation", (string?)updated["readiness"]);
            var native = new List<string>();
            transport.AfterHostResponse = (route, body) =>
            {
                if (route is "/activation-readiness" or "/activation-capability") native.Add(route + ": " + body);
            };
            var current = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
            Assert.True((bool)current.Result["currentReady"]!, current.Terminal.Output + "\n" + string.Join("\n", native));
        }
        finally { transport.WebPayload = web; transport.AfterHostResponse = null; }
        Assert.Equal(governing, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Qualified_refresh_preserves_governed_query_and_exact_live_selected_content_fences()
    {
        var transport = fixture.Reset();
        FactoryEvidenceRefreshTests.AdvanceDefaultBranch(transport);
        var completed = await fixture.DecideAsync(transport, new FactoryDecisionRequest(Guid.NewGuid()));
        Assert.Equal("ReadyAwaitingActivation", (string?)completed["readiness"]);
        var governing = transport.Scenario.Arm.Product.Export();
        var calls = transport.EvaluationInference.Count;
        var retained = transport.Scenario.Source.Source!.Progress.ToDictionary();
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactorySupplemental, value =>
        {
            var configuration = JsonNode.Parse(value)!;
            configuration["configuration"]!["selection"]!["sources"]![0]!["query"] = "A different decision-relevant public query";
            return configuration.ToJsonString();
        });
        try
        {
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(calls, transport.EvaluationInference.Count);
            Assert.Equal(retained, transport.Scenario.Source.Source.Progress);
        }
        finally { transport.Scenario.Arm.Product = ReservationTestStore.Restore(governing); }

        var web = transport.WebPayload;
        transport.BeforeEvaluationResponse = (_, _) =>
        {
            transport.WebPayload = """{"webResults":[{"url":"https://provider.example","content":"Selected content changed after this live evaluation captured its inputs."}]}""";
            return Task.CompletedTask;
        };
        try
        {
            var request = new FactoryDecisionRequest(Guid.NewGuid());
            using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
            JsonNode observed = null!;
            for (var i = 0; i < 2000; i++)
            {
                await Task.Delay(100);
                using var status = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", request, FactorySourceValidation.RecordJson);
                observed = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
                if (status.StatusCode != HttpStatusCode.Accepted) break;
            }
            Assert.Equal("Blocked", (string?)observed["readiness"]);
            Assert.Contains("CapturedPreviewInputsChanged", (string?)observed["reason"]);
            Assert.True(transport.EvaluationInference.Count > calls);
            Assert.Equal(1, transport.IssuePosts);
            Assert.Equal(governing, transport.Scenario.Arm.Product.Export());
        }
        finally { transport.WebPayload = web; transport.BeforeEvaluationResponse = null; }
    }
}
