using Dsf.Runtime;

namespace Dsf.AgentHost;

public static class AgentHostApplication
{
    public static Task<int> InvokeAsync(string[] args, CancellationToken cancellationToken) =>
        RuntimeCliApplication.InvokeAsync(ServeArguments(args), cancellationToken);

    public static Task<int> InvokeAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken) =>
        RuntimeCliApplication.InvokeAsync(ServeArguments(args), env, stdout, stderr, dependencies, cancellationToken);

    private static string[] ServeArguments(string[] args) =>
        args is ["serve-agent", ..] ? args : ["serve-agent", .. args];
}
