using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class EntryPointTests
{
    [Fact]
    public async Task Simulated_ctrl_c_maps_to_canonical_130_without_dispatching()
    {
        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            var exitCode = await EntryPoint.RunAsync(["list", "--json"], cancel => cancel());

            Assert.Equal(CliApplication.CanceledExitCode, exitCode);
            Assert.Equal(string.Empty, capturedOut.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Entry_point_subscribes_and_forwards_when_not_canceled()
    {
        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            var exitCode = await EntryPoint.RunAsync(["list", "--json"], _ => { });

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, capturedOut.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Late_cancellation_remains_valid_until_invocation_finishes()
    {
        Action? cancel = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var invocation = EntryPoint.RunAsync(
            ["list", "--json"],
            subscribedCancel => cancel = subscribedCancel,
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await release.Task;
                return cancellationToken.IsCancellationRequested ? CliApplication.CanceledExitCode : 0;
            });

        await started.Task;
        var exception = Record.Exception(() => cancel!());
        release.SetResult();

        Assert.Null(exception);
        Assert.Equal(CliApplication.CanceledExitCode, await invocation);
    }
}
