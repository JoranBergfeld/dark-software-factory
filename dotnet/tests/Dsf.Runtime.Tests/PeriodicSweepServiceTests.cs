using Dsf.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PeriodicSweepServiceTests
{
    private static readonly RuntimeSettings Settings = new(
        "acme", "https://appconfig.example", "", "", "https://cosmos.example",
        "https://openai.example", "gpt", "embed", "", "", "", "");

    [Fact]
    public async Task Pause_written_while_waiting_prevents_the_very_next_sweep()
    {
        var controls = new ObservedControls();
        var lease = new ConflictingLease();
        var clock = new ControlledSweepClock();
        using var service = Build(controls, lease, TimeSpan.FromSeconds(1), clock);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await controls.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await controls.SetPausedAsync(true, CancellationToken.None);
            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();

            Assert.Equal(0, lease.Attempts);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Lengthened_interval_is_checked_before_driving_a_due_sweep()
    {
        var controls = new ObservedControls();
        var lease = new ConflictingLease();
        var clock = new ControlledSweepClock();
        using var service = Build(controls, lease, TimeSpan.FromSeconds(1), clock);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await controls.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await controls.SetIntervalSecondsAsync(60, CancellationToken.None);
            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();

            Assert.Equal(0, lease.Attempts);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Shortened_interval_does_not_wait_out_the_previous_long_delay()
    {
        var controls = new ObservedControls();
        var lease = new ConflictingLease();
        var clock = new ControlledSweepClock();
        using var service = Build(controls, lease, TimeSpan.FromSeconds(60), clock);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await controls.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await controls.SetIntervalSecondsAsync(1, CancellationToken.None);
            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();

            Assert.Equal(1, lease.Attempts);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unreadable_controls_fail_closed_instead_of_sweeping_with_defaults()
    {
        var controls = new ObservedControls { ReadFailure = new HttpRequestException("403 Forbidden") };
        var lease = new ConflictingLease();
        var clock = new ControlledSweepClock();
        using var service = Build(controls, lease, TimeSpan.FromSeconds(1), clock);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();

            Assert.Equal(0, lease.Attempts);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dependency_cancellation_does_not_silently_terminate_the_host_loop()
    {
        var controls = new ObservedControls { ReadFailure = new OperationCanceledException("request timed out") };
        var clock = new ControlledSweepClock();
        using var service = Build(controls, new ConflictingLease(), TimeSpan.FromSeconds(1), clock);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();

            Assert.True(controls.SecondRead.Task.IsCompleted);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resume_and_interval_changes_respect_the_elapsed_cadence()
    {
        var controls = new ObservedControls();
        await controls.SetPausedAsync(true, CancellationToken.None);
        var lease = new ConflictingLease();
        var clock = new ControlledSweepClock();
        using var service = Build(controls, lease, TimeSpan.FromSeconds(1), clock);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();
            Assert.Equal(0, lease.Attempts);

            await controls.SetPausedAsync(false, CancellationToken.None);
            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();
            Assert.Equal(1, lease.Attempts);

            await controls.SetIntervalSecondsAsync(10, CancellationToken.None);
            await clock.TickAsync(TimeSpan.FromSeconds(9));
            await clock.WaitForPollAsync();
            Assert.Equal(1, lease.Attempts);

            await clock.TickAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForPollAsync();
            Assert.Equal(2, lease.Attempts);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static PeriodicSweepService Build(
        ISweepControlStore controls, ISweepLease lease, TimeSpan interval, TimeProvider clock) =>
        new(Settings, TestDependencies.Empty, interval,
            new Dictionary<string, string?> { [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true" }, controls, lease,
            NullLogger<PeriodicSweepService>.Instance, clock);

    private sealed class ConflictingLease : ISweepLease
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAsyncDisposable?> TryAcquireAsync(
            string product, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            Attempted.TrySetResult();
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    private sealed class ObservedControls : ISweepControlStore
    {
        private SweepControlState state = SweepControlState.Unset;
        private int reads;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ReadFailure { get; init; }

        public Task<SweepControlState> ReadAsync(CancellationToken cancellationToken)
        {
            var snapshot = Volatile.Read(ref state);
            if (Interlocked.Increment(ref reads) == 1)
            {
                FirstRead.TrySetResult();
            }
            else
            {
                SecondRead.TrySetResult();
            }

            return ReadFailure is null
                ? Task.FromResult(snapshot)
                : Task.FromException<SweepControlState>(ReadFailure);
        }

        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
        {
            state = state with { Paused = paused };
            return Task.CompletedTask;
        }

        public Task SetIntervalSecondsAsync(int seconds, CancellationToken cancellationToken)
        {
            state = state with { IntervalSeconds = seconds };
            return Task.CompletedTask;
        }
    }
}
