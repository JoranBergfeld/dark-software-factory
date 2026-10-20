using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class SourceTestClockTests
{
    [Fact]
    public async Task Concurrent_workload_deadlines_can_be_created_and_disposed_without_corrupting_the_clock()
    {
        var clock = new SourceTestClock();
        using var live = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
        using var start = new Barrier(4);
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 10000; i++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1), clock);
                Assert.False(deadline.IsCancellationRequested);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.False(live.IsCancellationRequested);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(live.IsCancellationRequested);
    }
}
