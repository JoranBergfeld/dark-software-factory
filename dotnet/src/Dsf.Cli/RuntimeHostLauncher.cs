using System.Diagnostics;
using System.Runtime.InteropServices;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

/// <summary>
/// Launches the runtime host for a runtime verb. The factory CLI is the operator's
/// front door for the runtime verbs, but the runtime itself (the conveyor, the
/// source agent hosts) lives in its own executable the CLI must not reference, so
/// the front door runs <c>dsf-runtime</c> and returns its exit code.
/// </summary>
internal interface IRuntimeHostLauncher
{
    Task<int> LaunchAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the real <c>dsf-runtime</c> executable, inheriting this process's stdio so
/// the runtime host's own output and exit code reach the operator unchanged.
/// Resolves the executable from <c>DSF_RUNTIME_HOST</c> when set, else from the
/// directory this CLI was installed into; a missing executable is reported by name
/// rather than silently skipped.
/// </summary>
internal sealed class ProcessRuntimeHostLauncher : IRuntimeHostLauncher
{
    /// <summary>Overrides where the runtime host executable is looked up.</summary>
    public const string ExecutableEnvironmentVariable = "DSF_RUNTIME_HOST";

    private const string ExecutableName = "dsf-runtime";

    public async Task<int> LaunchAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(ResolveExecutable()) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new RuntimeVerbException($"failed to start the runtime host '{startInfo.FileName}'.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    /// <summary>
    /// Resolves the runtime host executable path, throwing
    /// <see cref="RuntimeVerbException"/> naming both places it looked when the
    /// executable is not installed next to the CLI.
    /// </summary>
    public static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            if (!File.Exists(configured))
            {
                throw new RuntimeVerbException(
                    $"{ExecutableEnvironmentVariable} points at '{configured}', which does not exist.");
            }

            return configured;
        }

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{ExecutableName}.exe"
            : ExecutableName;
        var sibling = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(sibling))
        {
            throw new RuntimeVerbException(
                $"the runtime host executable '{fileName}' was not found next to the CLI (looked in "
                + $"'{AppContext.BaseDirectory}'). Install the runtime host alongside the CLI, or set "
                + $"{ExecutableEnvironmentVariable} to its path.");
        }

        return sibling;
    }
}
