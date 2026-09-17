using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewCloseoutTests
{
    [Theory]
    [InlineData("deadline-read")]
    [InlineData("deadline-release-read")]
    [InlineData("failure-read")]
    [InlineData("deadline-write")]
    [InlineData("failure-write")]
    [InlineData("lost-write")]
    [InlineData("lost-quarantine")]
    [InlineData("canceled-write")]
    [InlineData("revision-read")]
    public async Task Unconfirmed_post_result_closeout_never_returns_cached_readiness(string fault)
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        // Rebuild the actual hosts with a controllable scheduler, retaining native state.
        transport.Restart();
        var clock = new CloseoutClock(transport.Scenario.Source.Source!.Clock);
        transport.PreviewClock = clock;
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeIngressRequest = uri => uri.AbsolutePath == "/evaluation"
            ? poll.Task.WaitAsync(TimeSpan.FromMinutes(3)) : Task.CompletedTask;
        var interrupted = false;
        var closeoutReads = 0;
        void Interrupt()
        {
            Assert.Equal(1, clock.ExecutionTimers);
            interrupted = true;
            poll.TrySetResult();
            if (fault.StartsWith("deadline-", StringComparison.Ordinal)) clock.ExpireExecution();
            else if (fault == "canceled-write") throw new OperationCanceledException();
            else throw new HttpRequestException("Unconfirmed native closeout response.");
        }
        transport.AfterEvaluationRead = body =>
        {
            if (interrupted || body["proof"] is null || clock.ExecutionTimers != 1) return;
            Assert.Equal("Running", (string?)body["admission"]!["state"]);
            Assert.Equal(16, body["dispatches"]!.AsArray().Count);
            closeoutReads++;
            if (fault == "revision-read")
            {
                interrupted = true;
                body["_etag"] = "\"changed-after-current-evaluation\"";
                var path = transport.SeedDocuments.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Key;
                transport.SeedDocuments[path] = body.ToJsonString();
                poll.TrySetResult();
            }
            if (fault is "deadline-read" or "failure-read" || fault == "deadline-release-read" && closeoutReads == 2) Interrupt();
        };
        transport.BeforeEvaluationWrite = body =>
        {
            if (!interrupted && fault == "failure-write" && (string?)body["admission"]?["state"] == "Released") Interrupt();
        };
        transport.AfterEvaluationWrite = body =>
        {
            if (interrupted && fault == "lost-quarantine" && (string?)body["admission"]?["state"] == "Unknown")
                throw new HttpRequestException("Native quarantine accepted; response lost.");
            if (!interrupted && fault is "deadline-write" or "lost-write" or "lost-quarantine" or "canceled-write"
                && (string?)body["admission"]?["state"] == "Released") Interrupt();
        };
        var pins = PreviewEvaluationTests.Pins(transport);
        var effects = transport.Requests.Count;
        try
        {
            var result = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
            Assert.True(interrupted, result.Result.ToJsonString());
            Assert.Equal("Unknown", (string?)result.Result["readiness"]);
            Assert.NotEqual(0, result.Exit);
            Assert.Equal("Unknown", (string?)result.Result["evaluation"]!["readiness"]);
            Assert.NotNull(result.Result["evaluation"]!["proof"]);
            var closeout = Evaluation(transport);
            Assert.Equal("Unknown", (string?)closeout["admission"]!["state"]);
            Assert.Equal("Unknown", (string?)closeout["admission"]!["readiness"]);
            Assert.Equal(16, transport.EvaluationInference.Count);
            Assert.NotNull(closeout["proof"]);
            Assert.All(closeout["dispatches"]!.AsArray(), d => Assert.Equal("Confirmed", (string?)d!["state"]));
            var sameWorker = await transport.InspectEvaluationAsync(closeout["admission"]!["request"]!.Deserialize<PreviewCapabilityRequest>(FactoryGitHubJson.Options)!);
            Assert.Equal("Unknown", (string?)sameWorker["readiness"]);
            Assert.Equal((string?)closeout["admission"]!["reason"], (string?)sameWorker["reason"]);
            var native = transport.SeedDocuments.ToDictionary(p => p.Key, p => p.Value);
            transport.Restart();
            var status = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluation", .. pins]);
            Assert.Equal("Unknown", (string?)status.Result["readiness"]);
            Assert.NotNull(status.Result["evaluation"]!["proof"]);
            var resume = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", "--resume", .. pins]);
            Assert.Equal("Unknown", (string?)resume.Result["readiness"]);
            Assert.Equal(native, transport.SeedDocuments);
            Assert.Equal(16, transport.EvaluationInference.Count);
            PreviewEvaluationTests.AssertNoForbiddenEffects(transport, effects);
        }
        finally { poll.TrySetResult(); }
    }

    [Fact]
    public async Task Cached_confirmed_readiness_requires_the_same_current_native_admission_and_receipts()
    {
        using var transport = await PreviewEvaluationTests.ConfiguredAsync();
        var pins = PreviewEvaluationTests.Pins(transport);
        var evaluated = await transport.Scenario.Machine.InvokeAsync("preview", ["--evaluate", .. pins]);
        Assert.Equal("ReadyAwaitingActivation", (string?)evaluated.Result["readiness"]);
        var original = Evaluation(transport);
        var originalRun = transport.SeedDocuments.Single(p => p.Key.Contains("/runs/", StringComparison.Ordinal));
        var request = original["admission"]!["request"]!.Deserialize<PreviewCapabilityRequest>(FactoryGitHubJson.Options)!;
        var path = transport.SeedDocuments.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Key;
        var effects = transport.Requests.Count;
        foreach (var mutation in new[] { "unknown", "worker", "admission", "request", "operation", "revision", "etag", "run-incarnation", "unknown-missing-run" })
        {
            transport.SeedDocuments[path] = original.ToJsonString();
            transport.SeedDocuments[originalRun.Key] = originalRun.Value;
            if (mutation != "unknown") request = request with { RequestId = Guid.NewGuid() };
            using var inspectionDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            JsonNode ready;
            do
            {
                ready = await transport.InspectEvaluationAsync(request).WaitAsync(inspectionDeadline.Token);
                if ((string?)ready["readiness"] == "Checking") await Task.Delay(25, inspectionDeadline.Token);
            } while ((string?)ready["readiness"] == "Checking");
            Assert.Equal("ReadyAwaitingActivation", (string?)ready["readiness"]);
            var changed = original.DeepClone();
            switch (mutation)
            {
                case "unknown":
                case "unknown-missing-run":
                    changed["admission"]!["state"] = "Unknown";
                    changed["admission"]!["readiness"] = "Unknown";
                    changed["admission"]!["reason"] = "ExternalNativeCloseoutUnconfirmed";
                    if (mutation == "unknown-missing-run") transport.SeedDocuments.Remove(originalRun.Key);
                    break;
                case "worker":
                    changed["owner"] = Guid.NewGuid().ToString();
                    changed["admission"]!["workerId"] = changed["owner"]!.DeepClone();
                    break;
                case "admission": changed["admission"]!["id"] = Guid.NewGuid().ToString(); break;
                case "request": changed["admission"]!["request"]!["requestId"] = Guid.NewGuid().ToString(); break;
                case "operation": changed["admission"]!["request"]!["operationId"] = Guid.NewGuid().ToString(); break;
                case "revision": changed["admission"]!["capabilityRevision"] = new string('f', 64); break;
                case "etag": changed["_etag"] = "\"changed-native-version\""; break;
                case "run-incarnation":
                    var runPath = transport.SeedDocuments.Single(p => p.Key.Contains("/runs/", StringComparison.Ordinal)).Key;
                    var run = JsonNode.Parse(transport.SeedDocuments[runPath])!;
                    run["_rid"] = transport.SeedResourceId;
                    transport.SeedDocuments[runPath] = run.ToJsonString();
                    break;
            }
            var payload = changed.DeepClone().AsObject();
            payload.Remove("_rid");
            payload.Remove("_etag");
            changed["checksum"] = payload.Deserialize<PreviewEvaluationDocument>(FactoryGitHubJson.Options)!.CalculateChecksum();
            transport.SeedDocuments[path] = changed.ToJsonString();
            JsonNode observed;
            do
            {
                observed = await transport.InspectEvaluationAsync(request).WaitAsync(inspectionDeadline.Token);
                if ((string?)observed["readiness"] == "Checking") await Task.Delay(25, inspectionDeadline.Token);
            } while ((string?)observed["readiness"] == "Checking");
            Assert.True((string?)observed["readiness"] == "Unknown" || (string?)observed["state"] == "Unknown",
                mutation + ": " + (string?)observed["readiness"] + "/" + (string?)observed["state"] + "/" + (string?)observed["reason"]);
            if (mutation == "unknown") Assert.Equal("ExternalNativeCloseoutUnconfirmed", (string?)observed["reason"]);
            Assert.Equal(16, transport.EvaluationInference.Count);
        }
        PreviewEvaluationTests.AssertNoForbiddenEffects(transport, effects);
    }

    private static JsonNode Evaluation(PreviewPreparationTransport transport) =>
        JsonNode.Parse(transport.SeedDocuments.Single(p => p.Key.Contains("/council-preview/", StringComparison.Ordinal)).Value)!;

    private sealed class CloseoutClock(TimeProvider clock) : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ScheduledTimer> executions = [];
        internal int ExecutionTimers { get { lock (sync) return executions.Count; } }
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != TimeSpan.FromSeconds(5400)) return clock.CreateTimer(callback, state, dueTime, period);
            var scheduled = new ScheduledTimer(this, callback, state);
            scheduled.Timer = clock.CreateTimer(callback, state, dueTime, period);
            lock (sync) executions.Add(scheduled);
            return scheduled;
        }
        internal void ExpireExecution()
        {
            ScheduledTimer timer;
            lock (sync) timer = Assert.Single(executions);
            timer.Expire();
        }
        private sealed class ScheduledTimer(CloseoutClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal ITimer Timer = null!;
            internal void Expire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => Timer.Change(dueTime, period);
            public void Dispose()
            {
                Timer.Dispose();
                lock (owner.sync) owner.executions.Remove(this);
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
