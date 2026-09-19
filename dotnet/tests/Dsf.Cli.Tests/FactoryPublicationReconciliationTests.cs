using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryPublicationReconciliationTests(ActivatedFactoryFixture fixture) : IClassFixture<ActivatedFactoryFixture>
{
    [Fact]
    public async Task Original_issue_response_reconciles_via_installed_operator_and_native_hosts_after_pause()
    {
        var transport = fixture.Reset();
        transport.AfterEvaluationWrite = body =>
        {
            if ((string?)body["state"] == "Dispatched" && body["response"] is not null)
            {
                transport.AfterEvaluationWrite = null;
                throw new HttpRequestException("test-only-lost-original-witness-ack");
            }
        };
        var decision = await fixture.DecideAsync(transport, new(Guid.NewGuid()));
        Assert.Equal("Unknown", (string?)decision["readiness"]);
        var operation = (string)decision["evaluation"]!["publications"]![0]!["journal"]!["id"]!;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        var path = transport.SeedDocuments.Keys.Single(k => k.EndsWith("/" + operation, StringComparison.Ordinal));
        var retained = transport.SeedDocuments[path];
        foreach (var damage in new[] { "factoryId", "repositoryId", "attempt", "nativeResourceId", "responseHash", "missing" })
        {
            var original = JsonNode.Parse(retained)!;
            if (damage == "missing") transport.SeedDocuments.Remove(path);
            else
            {
                var witness = original["response"]!;
                if (damage == "repositoryId") witness[damage] = 42;
                else if (damage == "nativeResourceId") witness[damage] = "PD5DALigD00BAAAAAAAAAA==";
                else if (damage == "responseHash") witness["receipt"]!["responseSha256"] = new string('a', 64);
                else witness[damage] = Guid.NewGuid();
                var payload = original.DeepClone().AsObject();
                foreach (var key in new[] { "_rid", "_etag", "_self", "_attachments", "_ts" }) payload.Remove(key);
                original["checksum"] = System.Text.Json.JsonSerializer.Deserialize<FactoryPublicationDocument>(
                    payload.ToJsonString(), FactorySourceValidation.RecordJson)!.CalculateChecksum();
                transport.SeedDocuments[path] = original.ToJsonString();
            }
            var damaged = transport.SeedDocuments.ToDictionary();
            var unavailable = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation);
            Assert.Equal("Unknown", (string?)unavailable.Result["state"]);
            Assert.Equal(damaged, transport.SeedDocuments);
            Assert.Equal(1, transport.IssuePosts);
            transport.SeedDocuments[path] = retained;
        }
        await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
        var inspected = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation);
        Assert.Equal("Unknown", (string?)inspected.Result["state"]);
        var models = transport.EvaluationInference.Count;
        var github = transport.Requests.Count(r => r.Uri.Host == "api.github.com");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        transport.AfterNativeReadResponse = async (key, _) =>
        {
            if (key != path || Interlocked.Increment(ref captures) != 1) return;
            entered.TrySetResult();
            await release.Task;
        };
        var args = new[] { "--operation", operation, "--reconcile", "--revision", (string)inspected.Result["revision"]!,
            "--attempt", (string)inspected.Result["attempt"]! };
        var first = transport.Scenario.Machine.InvokeAsync("publication", args);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = transport.Scenario.Machine.InvokeAsync("publication", args);
        try { await second; }
        finally { release.TrySetResult(); transport.AfterNativeReadResponse = null; }
        var resolved = await second;
        Assert.Contains((string?)(await first).Result["state"], new[] { "Blocked", "Unknown" });
        Assert.Equal("Confirmed", (string?)resolved.Result["state"]);
        Assert.Equal(1, transport.IssuePosts);
        Assert.Equal(models, transport.EvaluationInference.Count);
        Assert.Equal(github, transport.Requests.Count(r => r.Uri.Host == "api.github.com"));
        Assert.NotNull(resolved.Result["reconciliation"]);
        Assert.DoesNotContain("responseJson", resolved.Terminal.Output);
        Assert.DoesNotContain(PreviewPreparationTransport.PublisherToken, resolved.Terminal.Output);
        Assert.Contains(transport.Requests, r => r.Uri.AbsolutePath == "/publication-reconcile");
        var stale = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation, "--reconcile",
            "--revision", (string)inspected.Result["revision"]!, "--attempt", (string)inspected.Result["attempt"]!);
        Assert.Contains((string?)stale.Result["state"], new[] { "Blocked", "Unknown" });
        Assert.Equal(1, transport.IssuePosts);
        var pause = await transport.Scenario.Machine.InvokeAsync("pause");
        Assert.Equal("DrainingUnknown", (string?)pause.Result["state"]);
        Assert.Contains(pause.Result["issuedPublications"]!.AsArray(), p =>
            (string?)p!["operation"] == operation && (string?)p["state"] == "Confirmed");
        Assert.False((bool)pause.Result["stopCompleted"]!);
    }

    [Theory]
    [InlineData("lost", "Unknown", 1)]
    [InlineData("witness-before", "Unknown", 1)]
    [InlineData("rejected", "Confirmed", 2)]
    public async Task Original_outcome_controls_explicit_retry_across_reconstructed_clients(string failure, string state, int writes)
    {
        var transport = fixture.Reset();
        transport.PublicationFailure = failure == "witness-before" ? null : failure;
        if (failure == "witness-before")
            transport.BeforeEvaluationWrite = body =>
            {
                if ((string?)body["state"] != "Dispatched" || body["response"] is null) return;
                transport.BeforeEvaluationWrite = null;
                throw new HttpRequestException("original-response-not-persisted");
            };
        var decision = await fixture.DecideAsync(transport, new(Guid.NewGuid()));
        var operation = (string)decision["evaluation"]!["publications"]![0]!["journal"]!["id"]!;
        transport.Restart();
        FactoryActivationTests.ConnectAudit(transport);
        transport.PublicationFailure = null;
        var inspected = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation);
        Assert.True(inspected.Result["revision"] is not null, inspected.Terminal.Output);
        var before = transport.SeedDocuments.ToDictionary();
        var result = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation,
            failure != "rejected" ? "--reconcile" : "--retry",
            "--revision", (string)inspected.Result["revision"]!, "--attempt", (string)inspected.Result["attempt"]!);
        Assert.Equal(state, (string?)result.Result["state"]);
        Assert.Equal(writes, transport.IssuePosts);
        Assert.Equal(operation, (string?)result.Result["operation"]);
        if (failure != "rejected") Assert.Equal(before, transport.SeedDocuments);
        else
        {
            var stale = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation, "--retry",
                "--revision", (string)inspected.Result["revision"]!, "--attempt", (string)inspected.Result["attempt"]!);
            Assert.Contains((string?)stale.Result["state"], new[] { "Blocked", "Unknown" });
            Assert.Equal(writes, transport.IssuePosts);
        }
    }

    [Fact]
    public async Task Delayed_original_response_after_pause_is_retained_without_operator_adoption_or_repetition()
    {
        var transport = fixture.Reset();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeIssueResponse = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        using var http = new HttpClient(transport, false) { Timeout = Timeout.InfiniteTimeSpan };
        using var admitted = await http.PostAsJsonAsync(fixture.LiveEndpoint(transport) + "/decide",
            new FactoryDecisionRequest(Guid.NewGuid()), FactorySourceValidation.RecordJson);
        Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        await entered.Task.WaitAsync(TimeSpan.FromMinutes(3));
        try
        {
            var path = transport.SeedDocuments.Keys.Single(k => k.Contains("/proposal-publications/docs/proposal-", StringComparison.Ordinal)
                && !k.EndsWith("-origin", StringComparison.Ordinal));
            var operation = path.Split('/')[^1];
            var inspected = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation);
            Assert.Equal("Unknown", (string?)inspected.Result["state"]);
            await transport.Scenario.Machine.InvokeAsync("pause", "--request", "--operation", Guid.NewGuid().ToString());
            var untouched = transport.SeedDocuments[path];
            var result = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation, "--reconcile",
                "--revision", (string)inspected.Result["revision"]!, "--attempt", (string)inspected.Result["attempt"]!);
            Assert.Equal("Unknown", (string?)result.Result["state"]);
            Assert.Equal(untouched, transport.SeedDocuments[path]);
            var pause = await transport.Scenario.Machine.InvokeAsync("pause");
            Assert.Contains(pause.Result["issuedPublications"]!.AsArray(), p =>
                (string?)p!["operation"] == operation && (string?)p["state"] == "Unknown");
            release.TrySetResult();
            for (var i = 0; i < 2000 && (string?)JsonNode.Parse(transport.SeedDocuments[path])!["state"] != "Confirmed"; i++)
                await Task.Delay(100);
            Assert.Equal("Confirmed", (string?)JsonNode.Parse(transport.SeedDocuments[path])!["state"]);
            transport.Restart();
            FactoryActivationTests.ConnectAudit(transport);
            var resolved = await transport.Scenario.Machine.InvokeAsync("publication", "--operation", operation);
            Assert.Equal("Confirmed", (string?)resolved.Result["state"]);
            Assert.True((bool)resolved.Result["admissionClosed"]!);
            Assert.Equal(1, transport.IssuePosts);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("--reconcile")]
    [InlineData("--retry")]
    [InlineData("--json")]
    public async Task Incomplete_redirected_recovery_has_no_provider_or_prompt(string action)
    {
        var terminal = new ScriptedTerminal(new(false, true, true), []);
        Assert.Equal(1, await CliApplication.InvokeAsync(["onboard", "decide", "publication", action], CancellationToken.None, terminal));
        Assert.Contains("Blocked", terminal.Output);
        Assert.Empty(terminal.Prompts);
    }
}
