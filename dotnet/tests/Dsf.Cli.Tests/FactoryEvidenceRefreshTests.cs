using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryEvidenceRefreshTests(ActivatedFactoryFixture fixture) : IClassFixture<ActivatedFactoryFixture>
{
    [Fact]
    public async Task Completed_promotion_recovers_after_model_observation_expiry_using_current_native_checks_and_real_evaluation()
    {
        var transport = fixture.Reset();
        var authority = FactoryModelsValidation.ReadPreparation(transport.Manifest, transport.Values());
        var historicalProof = JsonSerializer.Deserialize<ModelCapabilityProof>(
            transport.Values()[authority.LastProofReference!], FactorySourceValidation.RecordJson)!;
        var governance = transport.Scenario.Arm.Product.Export();
        transport.Restart(historicalProof.ExpiresAt.AddMinutes(2));
        FactoryActivationTests.ConnectAudit(transport);
        var calls = transport.EvaluationInference.Count;
        var requests = transport.Requests.Count;

        transport.BeforeIngressRequest = uri => uri.AbsolutePath == "/authority"
            ? throw new HttpRequestException("test-only-model-authority-outage") : Task.CompletedTask;
        var unavailable = await fixture.AdvanceUntilCompletedAsync(transport, transport.Scenario.Machine.InvokeAsync("activate", "--status"));
        Assert.Equal(1, unavailable.Exit);
        Assert.True((bool)unavailable.Result["historicallyCompleted"]!);
        Assert.False((bool)unavailable.Result["currentReady"]!);
        Assert.Equal(calls, transport.EvaluationInference.Count);
        Assert.Equal(0, transport.IssuePosts);

        transport.BeforeIngressRequest = null;
        var current = await fixture.AdvanceUntilCompletedAsync(transport, transport.Scenario.Machine.InvokeAsync("activate", "--status"));
        Assert.Equal(1, current.Exit);
        Assert.False((bool)current.Result["currentReady"]!);
        Assert.False((bool)current.Result["isActivated"]!);
        Assert.True((bool)current.Result["historicallyCompleted"]!);
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        Assert.Equal(FactoryModelsValidation.Hash(historicalProof), FactoryModelsValidation.Hash(
            JsonSerializer.Deserialize<ModelCapabilityProof>(transport.Values()[authority.LastProofReference!], FactorySourceValidation.RecordJson)!));
        Assert.Contains(transport.Requests.Skip(requests), r => r.Uri.AbsolutePath == "/authority");
        Assert.Contains(transport.Requests.Skip(requests), r => r.Uri.Host == "api.loganalytics.io");

        var invocation = new FactoryDecisionRequest(Guid.NewGuid());
        var decision = await fixture.DecideAsync(transport, invocation);
        Assert.Equal("ReadyAwaitingActivation", (string?)decision["readiness"]);
        Assert.Equal("Confirmed", (string?)Assert.Single(decision["evaluation"]!["publications"]!.AsArray())!["state"]);
        Assert.Equal(16, transport.EvaluationInference.Count - calls);
        Assert.Equal(FactoryModelsValidation.RouteNames.Order(), transport.EvaluationInference.Skip(calls).Distinct().Order());
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal(governance, transport.Scenario.Arm.Product.Export());
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == "api.github.com" && r.Method is "PATCH" or "PUT" or "DELETE");
        var evaluated = await fixture.AdvanceUntilCompletedAsync(transport, transport.Scenario.Machine.InvokeAsync("activate", "--status"));
        Assert.True(evaluated.Exit == 0, evaluated.Terminal.Output + "\n" + string.Join("\n", transport.HostFailures.TakeLast(5)));
        Assert.True((bool)evaluated.Result["currentReady"]!);
        Assert.True((bool)evaluated.Result["isActivated"]!);
        var observedAt = transport.Scenario.Source.Source!.Clock.Now;
        transport.Restart(observedAt.AddSeconds(authority.Configuration.ValidForSeconds + 1));
        FactoryActivationTests.ConnectAudit(transport);
        var expired = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)expired.Result["currentReady"]!);
        Assert.True((bool)expired.Result["historicallyCompleted"]!);
        Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        transport.Restart(observedAt);
        FactoryActivationTests.ConnectAudit(transport);
        var resumed = await fixture.DecideAsync(transport, invocation);
        Assert.Equal("ReadyAwaitingActivation", (string?)resumed["readiness"]);
        Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        Assert.Equal(1, transport.IssuePosts);

        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var failure in new[] { "empty", "stale", "denied" })
        {
            var documents = transport.SeedDocuments.ToDictionary();
            transport.ApplicationQueryStatus = failure == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            transport.AlterApplication = body =>
            {
                if (failure == "empty") body["tables"]![0]!["rows"] = new JsonArray();
                if (failure == "stale") body["tables"]![0]!["rows"]![0]![0] =
                    transport.Scenario.Source.Source!.Clock.Now.AddDays(-2).ToString("O");
                return body;
            };
            var blocked = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
            Assert.False((bool)blocked.Result["currentReady"]!);
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(documents, transport.SeedDocuments);
            Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        }
        transport.ApplicationQueryStatus = HttpStatusCode.OK;
        transport.AlterApplication = null;

        var pause = await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        Assert.True((bool)pause.Result["admissionClosed"]!);
        var stopped = transport.Scenario.Arm.Product.Export();
        var status = await transport.Scenario.Machine.InvokeAsync("activate", "--status");
        Assert.False((bool)status.Result["isActivated"]!);
        foreach (var route in new[] { "/decide", "/decision" })
        {
            using var denied = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + route,
                new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Equal(stopped, transport.Scenario.Arm.Product.Export());
        Assert.Equal(calls + 16, transport.EvaluationInference.Count);
        Assert.Equal(1, transport.IssuePosts);
    }

    [Fact]
    public async Task Expired_model_history_never_replays_an_unconfirmed_actual_provider_response_after_recovery()
    {
        var transport = fixture.Reset();
        var model = FactoryModelsValidation.ReadPreparation(transport.Manifest, transport.Values());
        transport.Restart(model.VerifiedUntil!.Value.AddMinutes(2));
        FactoryActivationTests.ConnectAudit(transport);
        transport.BeforeEvaluationResponse = (_, _) => throw new HttpRequestException("test-only-issued-model-response-lost");
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new FactoryDecisionRequest(Guid.NewGuid());
        var calls = transport.EvaluationInference.Count;
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync());
        JsonNode observed = null!;
        for (var i = 0; i < 1000; i++)
        {
            using var response = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", request, FactorySourceValidation.RecordJson);
            observed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            if (response.StatusCode != HttpStatusCode.Accepted) break;
            await Task.Delay(100);
        }
        Assert.Equal("Unknown", (string?)observed["readiness"]);
        Assert.Equal(calls + 1, transport.EvaluationInference.Count);
        var original = transport.SeedDocuments.ToDictionary();
        transport.BeforeEvaluationResponse = null;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        using var retried = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide", request, FactorySourceValidation.RecordJson);
        Assert.Contains((string?)JsonNode.Parse(await retried.Content.ReadAsStringAsync())!["readiness"], new[] { "Unknown", "Checking" });
        using var inspected = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decision", request, FactorySourceValidation.RecordJson);
        Assert.Equal("Unknown", (string?)JsonNode.Parse(await inspected.Content.ReadAsStringAsync())!["readiness"]);
        using var newWork = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Forbidden, newWork.StatusCode);
        Assert.Equal(calls + 1, transport.EvaluationInference.Count);
        Assert.Equal(original, transport.SeedDocuments);
        Assert.Equal(0, transport.IssuePosts);
    }
}
