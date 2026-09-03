namespace Dsf.Cli;

/// <summary>
/// Thin, testable glue between the process entry point and <see cref="CliApplication"/>.
/// Owns the <see cref="CancellationTokenSource"/> that a caller-supplied cancellation
/// source (e.g. Ctrl+C) cancels, so an external signal maps to the canonical
/// <see cref="CliApplication.CanceledExitCode"/> exit path instead of being ignored.
/// </summary>
public static class EntryPoint
{
    public static Task<int> RunAsync(string[] args, Action<Action> subscribeCancel)
    {
        using var cts = new CancellationTokenSource();
        subscribeCancel(cts.Cancel);
        return CliApplication.InvokeAsync(args, cts.Token);
    }
}
