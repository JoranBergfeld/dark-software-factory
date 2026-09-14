using System.Threading.Channels;

namespace Dsf.Runtime.Tests;

internal sealed class ControlledSweepClock : TimeProvider
{
    private readonly object sync = new();
    private readonly List<SweepTimer> timers = [];
    private readonly Channel<SweepTimer> polls = Channel.CreateUnbounded<SweepTimer>();
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref timestamp);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new NotSupportedException("The sweep loop uses one-shot delays.");
        }

        var timer = new SweepTimer(this, callback, state);
        timer.Change(dueTime, period);
        polls.Writer.TryWrite(timer);
        return timer;
    }

    public async Task WaitForPollAsync() =>
        await polls.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    public async Task TickAsync(TimeSpan elapsed)
    {
        await polls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        List<SweepTimer> ready;
        lock (sync)
        {
            timestamp += elapsed.Ticks;
            ready = timers.Where(timer => timer.DueAt <= timestamp).ToList();
            foreach (var timer in ready)
            {
                timers.Remove(timer);
            }
        }

        foreach (var timer in ready)
        {
            timer.Fire();
        }
    }

    private sealed class SweepTimer(ControlledSweepClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public long DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.sync)
            {
                if (disposed)
                {
                    return false;
                }

                clock.timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock.timestamp + dueTime.Ticks;
                    clock.timers.Add(this);
                }

                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock.sync)
            {
                disposed = true;
                clock.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
