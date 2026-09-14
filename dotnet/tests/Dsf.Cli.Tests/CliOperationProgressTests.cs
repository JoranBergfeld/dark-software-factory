using System.Collections.Concurrent;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CliOperationProgressTests
{
    [Fact]
    public async Task Reports_before_work_then_while_waiting_and_stops_before_returning()
    {
        var terminal = new ProgressTerminal();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = CliOperationProgress.RunAsync(
            terminal, "Deploying test resources",
            _ =>
            {
                Assert.Contains("[dsf] Deploying test resources...", terminal.Output);
                return completion.Task;
            },
            CancellationToken.None,
            TimeSpan.FromMilliseconds(10));
        try
        {
            await terminal.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Still waiting: Deploying test resources", terminal.Output);
            Assert.DoesNotContain("Completed:", terminal.Output);
            completion.SetResult(42);

            Assert.Equal(42, await operation);
            Assert.Contains("Completed: Deploying test resources", terminal.Output);
            var count = terminal.Lines.Count;
            await Task.Delay(50);
            Assert.Equal(count, terminal.Lines.Count);
        }
        finally
        {
            completion.TrySetResult(42);
            await operation;
        }
    }

    [Fact]
    public async Task Failure_preserves_original_exception_without_printing_its_payload()
    {
        var terminal = new ProgressTerminal();
        var failure = new InvalidOperationException("test-secret-payload");

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CliOperationProgress.RunAsync(
                terminal, "Copying credentials", _ => Task.FromException(failure), CancellationToken.None));

        Assert.Same(failure, observed);
        Assert.Contains("Stopped: Copying credentials", terminal.Output);
        Assert.DoesNotContain("Completed:", terminal.Output);
        Assert.DoesNotContain("test-secret-payload", terminal.Output);
    }

    [Fact]
    public async Task Cancellation_stops_progress_without_reporting_completion()
    {
        var terminal = new ProgressTerminal();
        using var cancellation = new CancellationTokenSource();
        var operation = CliOperationProgress.RunAsync(
            terminal, "Waiting for service", token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            cancellation.Token, TimeSpan.FromMilliseconds(10));
        try
        {
            await terminal.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Contains("Stopped: Waiting for service", terminal.Output);
        Assert.DoesNotContain("Completed:", terminal.Output);
    }

    private sealed class ProgressTerminal : ICliTerminal
    {
        public TerminalCapabilities Capabilities => new(false, false, false);
        public ConcurrentQueue<string> Lines { get; } = new();
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Output => string.Join('\n', Lines);
        public void WriteLine(string value)
        {
            Lines.Enqueue(value);
            if (value.Contains("Still waiting:", StringComparison.Ordinal))
            {
                Waiting.TrySetResult();
            }
        }
        public void WriteErrorLine(string value) => WriteLine(value);
        public string? Prompt(string message) => throw new NotSupportedException();
        public Task<string?> PromptSecretAsync(string message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
