using System.Diagnostics;

namespace Dsf.Core.Runtime;

/// <summary>A process boundary supporting cancellation-driven process-tree termination.</summary>
public interface IManagedProcess : IDisposable
{
    void Start();

    Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken);

    Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken);

    Task WaitForExitAsync(CancellationToken cancellationToken);

    int ExitCode { get; }

    void Kill(bool entireProcessTree);
}

/// <summary>Wraps a real <see cref="Process"/>.</summary>
public sealed class RealManagedProcess(Process process) : IManagedProcess
{
    public void Start() => process.Start();

    public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) =>
        process.StandardOutput.ReadToEndAsync(cancellationToken);

    public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
        process.StandardError.ReadToEndAsync(cancellationToken);

    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    public int ExitCode => process.ExitCode;

    public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

    public void Dispose() => process.Dispose();
}
