using System.Diagnostics;

namespace Dsf.Cli;

internal static class CliOperationProgress
{
    public static Task RunAsync(
        ICliTerminal? terminal,
        string description,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        TimeSpan? heartbeatInterval = null) =>
        RunAsync(
            terminal,
            description,
            async token =>
            {
                await operation(token);
                return true;
            },
            cancellationToken,
            heartbeatInterval);

    public static async Task<T> RunAsync<T>(
        ICliTerminal? terminal,
        string description,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        TimeSpan? heartbeatInterval = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal is null)
        {
            return await operation(cancellationToken);
        }

        terminal.WriteLine($"[dsf] {description}...");
        var elapsed = Stopwatch.StartNew();
        using var stopProgress = new CancellationTokenSource();
        var reporting = ReportWaitingAsync(
            terminal, description, elapsed, heartbeatInterval ?? TimeSpan.FromSeconds(10), stopProgress.Token);
        var completed = false;
        try
        {
            var result = await operation(cancellationToken);
            completed = true;
            return result;
        }
        finally
        {
            await stopProgress.CancelAsync();
            await reporting;
            terminal.WriteLine(
                $"[dsf] {(completed ? "Completed" : "Stopped")}: {description} ({elapsed.Elapsed.TotalSeconds:F0}s).");
        }
    }

    private static async Task ReportWaitingAsync(
        ICliTerminal terminal,
        string description,
        Stopwatch elapsed,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                terminal.WriteLine($"[dsf] Still waiting: {description} ({elapsed.Elapsed.TotalSeconds:F0}s elapsed).");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Join the reporter before the next stage can write to the terminal.
        }
    }
}
