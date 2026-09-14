using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Dsf.Cli;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class RuntimeHostLauncherTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Directory.GetCurrentDirectory(), ".runtime-launcher-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> environment = [];
    private readonly RecordingProcess process = new();
    private ProcessStartInfo? invocation;

    [Fact]
    public async Task Native_bundle_is_preferred_and_preserves_arguments_stdio_environment_and_exit_code()
    {
        var executable = WriteFile($"runtime/{NativeName}", "native bundle");
        environment["DOTNET_HOST_PATH"] = "not-needed";
        process.Result = 37;
        var args = new[] { "run", "--signal", "a path/with \"quotes\".json", "", "雪", "$HOME; echo nope" };

        var result = await Launcher().LaunchAsync(args, CancellationToken.None);

        Assert.Equal(37, result);
        Assert.Equal(executable, invocation!.FileName);
        Assert.Equal(args, invocation.ArgumentList);
        Assert.False(invocation.UseShellExecute);
        Assert.False(invocation.RedirectStandardInput);
        Assert.False(invocation.RedirectStandardOutput);
        Assert.False(invocation.RedirectStandardError);
        Assert.Equal(string.Empty, invocation.WorkingDirectory);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), invocation.Environment["PATH"]);
        Assert.True(process.Disposed);
        Assert.False(process.Killed);
    }

    [Fact]
    public async Task Framework_bundle_uses_dotnet_host_path_and_prepends_only_the_runtime_dll()
    {
        WriteFrameworkBundle();
        var dotnet = WriteExecutable("host with spaces/dotnet");
        environment["DOTNET_HOST_PATH"] = dotnet;

        await Launcher().LaunchAsync(["sweep", "--dry-run"], CancellationToken.None);

        Assert.Equal(dotnet, invocation!.FileName);
        Assert.Equal(
            [Path.Combine(directory, "runtime", "dsf-runtime.dll"), "sweep", "--dry-run"],
            invocation.ArgumentList);
    }

    [Fact]
    public async Task Invalid_dotnet_host_path_falls_back_to_installed_host_on_path()
    {
        WriteFrameworkBundle();
        environment["DOTNET_HOST_PATH"] = Path.Combine(directory, "missing-dotnet");
        var dotnet = WriteExecutable($"installed sdk/{DotnetName}");
        environment["PATH"] = Path.GetDirectoryName(dotnet);

        await Launcher().LaunchAsync(["sweep"], CancellationToken.None);

        Assert.Equal(dotnet, invocation!.FileName);
    }

    [Fact]
    public async Task Dotnet_root_is_used_when_path_has_no_host()
    {
        WriteFrameworkBundle();
        var dotnet = WriteExecutable($"installed sdk/{DotnetName}");
        environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet);

        await Launcher().LaunchAsync(["sweep"], CancellationToken.None);

        Assert.Equal(dotnet, invocation!.FileName);
    }

    [Fact]
    public async Task Explicit_override_bypasses_bundle_and_preserves_executable_semantics()
    {
        var executable = WriteFile("custom host", "custom executable");
        environment[ProcessRuntimeHostLauncher.ExecutableEnvironmentVariable] = $" {executable} ";

        await Launcher().LaunchAsync(["run", "two words"], CancellationToken.None);

        Assert.Equal(executable, invocation!.FileName);
        Assert.Equal(["run", "two words"], invocation.ArgumentList);
    }

    [Fact]
    public async Task Missing_override_does_not_fall_back_to_valid_bundle()
    {
        WriteFile($"runtime/{NativeName}", "native bundle");
        var missing = Path.Combine(directory, "missing-custom-host");
        environment[ProcessRuntimeHostLauncher.ExecutableEnvironmentVariable] = missing;

        var error = await Assert.ThrowsAsync<RuntimeVerbException>(
            () => Launcher().LaunchAsync(["run"], CancellationToken.None));

        Assert.Contains("DSF_RUNTIME_HOST", error.Message);
        Assert.Contains(missing, error.Message);
        Assert.Null(invocation);
    }

    [Theory]
    [InlineData("dsf-runtime.dll")]
    [InlineData("dsf-runtime.deps.json")]
    [InlineData("dsf-runtime.runtimeconfig.json")]
    [InlineData("Dsf.Core.dll")]
    public async Task Missing_framework_component_reports_path_and_reinstall_action(string component)
    {
        WriteFrameworkBundle();
        File.Delete(Path.Combine(directory, "runtime", component));

        var error = await Assert.ThrowsAsync<RuntimeVerbException>(
            () => Launcher().LaunchAsync(["sweep"], CancellationToken.None));

        Assert.Contains(Path.Combine(directory, "runtime", component), error.Message);
        Assert.Contains("reinstall", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(invocation);
    }

    [Theory]
    [InlineData("dsf-runtime.dll", "not an assembly")]
    [InlineData("Dsf.Core.dll", "not an assembly")]
    [InlineData("dsf-runtime.deps.json", "{broken")]
    [InlineData("dsf-runtime.deps.json", "{}")]
    [InlineData("dsf-runtime.runtimeconfig.json", "{broken")]
    [InlineData("dsf-runtime.runtimeconfig.json", "{}")]
    public async Task Corrupt_framework_component_reports_path_and_reinstall_action(string component, string content)
    {
        WriteFrameworkBundle();
        WriteFile($"runtime/{component}", content);

        var error = await Assert.ThrowsAsync<RuntimeVerbException>(
            () => Launcher().LaunchAsync(["sweep"], CancellationToken.None));

        Assert.Contains(Path.Combine(directory, "runtime", component), error.Message);
        Assert.Contains("reinstall", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(invocation);
    }

    [Fact]
    public async Task Start_failure_is_an_actionable_runtime_error()
    {
        var executable = WriteFile($"runtime/{NativeName}", "invalid");
        process.StartError = new Win32Exception("Exec format error");

        var error = await Assert.ThrowsAsync<RuntimeVerbException>(
            () => Launcher().LaunchAsync(["sweep"], CancellationToken.None));

        Assert.Contains(executable, error.Message);
        Assert.Contains("Exec format error", error.Message);
        Assert.Contains("reinstall", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Cancellation_kills_child_tree_and_disposes_the_process()
    {
        WriteFile($"runtime/{NativeName}", "native bundle");
        using var cancellation = new CancellationTokenSource();
        process.Wait = async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Launcher().LaunchAsync(["serve-agent"], cancellation.Token));

        Assert.True(process.Killed);
        Assert.True(process.KilledEntireTree);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Cancellation_after_child_exit_does_not_mask_cancellation()
    {
        WriteFile($"runtime/{NativeName}", "native bundle");
        using var cancellation = new CancellationTokenSource();
        process.KillError = new InvalidOperationException("Already exited");
        process.Wait = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Launcher().LaunchAsync(["sweep"], cancellation.Token));

        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Already_canceled_invocation_does_not_start_a_child()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Launcher().LaunchAsync(["sweep"], cancellation.Token));

        Assert.Null(invocation);
    }

    private ProcessRuntimeHostLauncher Launcher() => new(directory, startInfo =>
    {
        invocation = startInfo;
        return process;
    }, name => environment.GetValueOrDefault(name));

    private void WriteFrameworkBundle()
    {
        WriteFile("runtime/dsf-runtime.dll", "");
        File.Copy(typeof(CliApplication).Assembly.Location, Path.Combine(directory, "runtime", "dsf-runtime.dll"), true);
        File.Copy(typeof(RuntimeVerbException).Assembly.Location, Path.Combine(directory, "runtime", "Dsf.Core.dll"), true);
        WriteFile("runtime/dsf-runtime.runtimeconfig.json",
            """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
        WriteFile("runtime/dsf-runtime.deps.json", JsonSerializer.Serialize(new
        {
            runtimeTarget = new { name = ".NETCoreApp,Version=v10.0" },
            targets = new Dictionary<string, object>
            {
                [".NETCoreApp,Version=v10.0"] = new Dictionary<string, object>
                {
                    ["dsf-runtime/0.0.1"] = new { runtime = new Dictionary<string, object> { ["dsf-runtime.dll"] = new { } } },
                    ["Dsf.Core/0.0.1"] = new { runtime = new Dictionary<string, object> { ["lib/net10.0/Dsf.Core.dll"] = new { } } },
                },
            },
        }));
    }

    private string WriteExecutable(string relativePath)
    {
        var path = WriteFile(relativePath, "host");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string NativeName => OperatingSystem.IsWindows() ? "dsf-runtime.exe" : "dsf-runtime";
    private static string DotnetName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingProcess : IManagedProcess
    {
        public int Result { get; set; }
        public int ExitCode => Result;
        public bool Disposed { get; private set; }
        public bool Killed { get; private set; }
        public bool KilledEntireTree { get; private set; }
        public Exception? StartError { get; set; }
        public Exception? KillError { get; set; }
        public Func<CancellationToken, Task> Wait { get; set; } = _ => Task.CompletedTask;
        public void Start()
        {
            if (StartError is not null)
            {
                throw StartError;
            }
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Wait(cancellationToken);
        public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Runtime stdout must remain inherited.");
        public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Runtime stderr must remain inherited.");
        public void Kill(bool entireProcessTree)
        {
            Killed = true;
            KilledEntireTree = entireProcessTree;
            if (KillError is not null)
            {
                throw KillError;
            }
        }
        public void Dispose() => Disposed = true;
    }
}
