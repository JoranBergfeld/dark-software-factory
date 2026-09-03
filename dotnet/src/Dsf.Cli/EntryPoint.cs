namespace Dsf.Cli;

/// <summary>
/// Thin, testable glue between the process entry point and <see cref="CliApplication"/>.
/// Owns the <see cref="CancellationTokenSource"/> that a caller-supplied cancellation
/// source (e.g. Ctrl+C) cancels, so an external signal maps to the canonical
/// <see cref="CliApplication.CanceledExitCode"/> exit path instead of being ignored.
/// </summary>
public static class EntryPoint
{
    public static Task<int> RunAsync(string[] args, Action<Action> subscribeCancel) =>
        RunAsync(args, subscribeCancel, CliApplication.InvokeAsync);

    internal static async Task<int> RunAsync(
        string[] args,
        Action<Action> subscribeCancel,
        Func<string[], CancellationToken, Task<int>> invokeAsync)
    {
        using var cts = new CancellationTokenSource();
        subscribeCancel(cts.Cancel);
        return await invokeAsync(args, cts.Token);
    }
}
