using System.Diagnostics;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class FactoryModelsTimeoutTests
{
    [Fact]
    public async Task Operator_cancellation_of_pending_proof_remains_canceled_without_a_current_revocation_claim()
    {
        using var cancellation = new CancellationTokenSource();
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync(cancellation.Token);
        using var transport = new ModelsWorkflowTransport(arm);
        using var metadata = new HttpClient(transport, disposeHandler: false);
        machine.Boundary.Repository.ReservationHttp = metadata;
        machine.Boundary.Repository.ModelProofTransport = transport;
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        Assert.Equal(0, (await machine.InvokeAsync("models", "--verify-only")).Exit);
        var previousProof = (string?)transport.Record("dsf/factory/models")["lastProofReference"];
        var effects = transport.Writes.Count;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeProofAsync = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var checking = machine.InvokeAsync("models", "--verify-only");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = await checking.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(130, result.Exit);
            Assert.Equal("Canceled", (string?)result.Result["state"]);
            Assert.Contains("after a hold was confirmed", result.Terminal.Output);
            Assert.Contains("No revocation asserted", result.Terminal.Output);
            Assert.DoesNotContain("timed out", result.Terminal.Output);
            Assert.Null(result.Result["models"]!["verifiedUntil"]);
            Assert.Equal("Partial", (string?)transport.Record("dsf/factory/models")["state"]);
            Assert.Equal(previousProof, (string?)transport.Record("dsf/factory/models")["lastProofReference"]);
            Assert.Equal(effects, transport.Writes.Count);
            Assert.Equal(2, transport.Proofs);
        }
        finally
        {
            cancellation.Cancel();
            await checking.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(3600, 330)]
    [InlineData(60, 90)]
    public async Task Exhausted_request_budget_is_unknown_and_keeps_previous_proof_historical(int validity, int budget)
    {
        using var cancellation = new CancellationTokenSource();
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync(cancellation.Token);
        using var transport = new ModelsWorkflowTransport(arm);
        using var metadata = new HttpClient(transport, disposeHandler: false);
        machine.Boundary.Repository.ReservationHttp = metadata;
        machine.Boundary.Repository.ModelProofTransport = transport;
        var topology = transport.Topology;
        topology["validForSeconds"] = validity;
        var preview = await machine.InvokeAsync("models", "--model-topology", topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        Assert.Equal(0, (await machine.InvokeAsync("models", "--verify-only")).Exit);
        var previousProof = (string?)transport.Record("dsf/factory/models")["lastProofReference"];
        var proofWrites = arm.Product.Writes.Count(w => w.Key.StartsWith("dsf/factory/model-proofs/", StringComparison.Ordinal));
        var effects = transport.Writes.Count;
        var clock = new ProofDeadlineClock();
        machine.Boundary.Repository.ReservationClock = clock;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeProofAsync = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var checking = machine.InvokeAsync("models", "--verify-only");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(budget - 1));
            Assert.False(checking.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            var result = await checking.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(cancellation.IsCancellationRequested);
            Assert.Equal(1, result.Exit);
            Assert.Equal("Unknown", (string?)result.Result["state"]);
            Assert.Equal("Unknown", (string?)result.Result["models"]!["state"]);
            Assert.Contains("timed out", result.Terminal.Output);
            Assert.Contains("hold was confirmed", result.Terminal.Output);
            Assert.Null(result.Result["models"]!["verifiedUntil"]);
            Assert.Equal(previousProof, (string?)result.Result["models"]!["lastProofReference"]);
            Assert.Equal(proofWrites, arm.Product.Writes.Count(w => w.Key.StartsWith("dsf/factory/model-proofs/", StringComparison.Ordinal)));
            Assert.Equal(effects, transport.Writes.Count);
            Assert.Equal(2, transport.Proofs);
            var status = await machine.InvokeAsync("status");
            Assert.Equal("Unknown", (string?)status.Result["models"]!["state"]);
            Assert.False((bool)status.Result["isActivated"]!);
        }
        finally
        {
            cancellation.Cancel();
            await checking.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Valid_120_second_aggregate_verifies_through_the_production_proof_client()
    {
        var (machine, arm) = await FactoryTelemetryWorkflowTests.FoundationAsync();
        using var transport = new ModelsWorkflowTransport(arm);
        using var metadata = new HttpClient(transport, disposeHandler: false);
        machine.Boundary.Repository.ReservationHttp = metadata;
        machine.Boundary.Repository.ModelProofTransport = transport;
        var preview = await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString());
        await machine.InvokeAsync("models", "--model-topology", transport.Topology.ToJsonString(),
            "--approve", (string)preview.Result["models"]!["approvalFingerprint"]!);
        var effects = transport.Writes.Count;
        var completedRoutes = 0;
        transport.BeforeProofAsync = async token =>
        {
            for (var route = 0; route < 5; route++)
            {
                await Task.Delay(TimeSpan.FromSeconds(24), token);
                completedRoutes++;
            }
        };

        var elapsed = Stopwatch.StartNew();
        machine.Boundary.Repository.ReservationClock = new ElapsedProofClock(elapsed);
        var result = await machine.InvokeAsync("models", "--verify-only");

        Assert.True(result.Exit == 0, $"120s aggregate returned {result.Result["state"]} after {elapsed.Elapsed.TotalSeconds:F0}s.");
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(120));
        Assert.Equal(5, completedRoutes);
        Assert.Equal("Verified", (string?)result.Result["models"]!["state"]);
        Assert.Equal(5, result.Result["models"]!["proof"]!["observations"]!.AsArray().Count);
        Assert.Equal(1, transport.Proofs);
        Assert.Equal(effects, transport.Writes.Count);
        Assert.False(machine.Token.IsCancellationRequested);
        Assert.Equal("Verified", (string?)(await machine.InvokeAsync("status")).Result["models"]!["state"]);
    }

    private sealed class ElapsedProofClock(Stopwatch elapsed) : TimeProvider
    {
        private readonly DateTimeOffset origin = new ReservationClock().GetUtcNow();
        public override DateTimeOffset GetUtcNow() => origin + elapsed.Elapsed;
    }

    private sealed class ProofDeadlineClock : TimeProvider
    {
        private readonly List<Deadline> timers = [];
        private DateTimeOffset now = new ReservationClock().GetUtcNow();
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Deadline(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            foreach (var timer in timers.Where(t => t.DueAt <= now).ToArray())
            {
                timer.Dispose();
                timer.Fire();
            }
        }

        private sealed class Deadline(ProofDeadlineClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt { get; private set; }
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot proof deadlines are expected.");
                if (disposed) return false;
                clock.timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock.now + dueTime;
                    clock.timers.Add(this);
                }
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { disposed = true; clock.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
