using Dsf.Core.Runtime;

namespace Dsf.AgentHost;

public static class AgentHostApplication
{
    public static Task<int> InvokeAsync(string[] args, CancellationToken cancellationToken) =>
        InvokeAsync(args, new ProcessRuntimeHostLauncher(), Console.Error, cancellationToken);

    public static async Task<int> InvokeAsync(
        string[] args,
        IRuntimeHostLauncher runtimeHostLauncher,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runtimeHostLauncher.LaunchAsync(ServeArguments(args), cancellationToken);
        }
        catch (RuntimeVerbException exception)
        {
            await stderr.WriteLineAsync($"[dsf] error: {exception.Message}");
            return 1;
        }
    }

    private static string[] ServeArguments(string[] args) =>
        args is ["serve-agent", ..] ? args : ["serve-agent", .. args];
}
