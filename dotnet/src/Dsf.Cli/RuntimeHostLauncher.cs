using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
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
/// Runs the bundled native or framework-dependent runtime out of process, inheriting
/// stdio and environment. <c>DSF_RUNTIME_HOST</c> overrides the bundle with an executable.
/// </summary>
internal sealed class ProcessRuntimeHostLauncher : IRuntimeHostLauncher
{
    /// <summary>Overrides where the runtime host executable is looked up.</summary>
    public const string ExecutableEnvironmentVariable = "DSF_RUNTIME_HOST";

    private const string ExecutableName = "dsf-runtime";
    private readonly string baseDirectory;
    private readonly Func<ProcessStartInfo, IManagedProcess> processFactory;
    private readonly Func<string, string?> getEnvironmentVariable;

    public ProcessRuntimeHostLauncher()
        : this(AppContext.BaseDirectory,
            startInfo => new RealManagedProcess(new Process { StartInfo = startInfo }),
            Environment.GetEnvironmentVariable)
    {
    }

    internal ProcessRuntimeHostLauncher(
        string baseDirectory,
        Func<ProcessStartInfo, IManagedProcess> processFactory,
        Func<string, string?> getEnvironmentVariable)
    {
        this.baseDirectory = baseDirectory;
        this.processFactory = processFactory;
        this.getEnvironmentVariable = getEnvironmentVariable;
    }

    public async Task<int> LaunchAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = ResolveStartInfo();
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = processFactory(startInfo);
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new RuntimeVerbException(
                $"could not start runtime host '{startInfo.FileName}': {exception.Message}. "
                + "Check executable permissions and platform compatibility; reinstall the matching DSF distribution "
                + $"or correct {ExecutableEnvironmentVariable}.");
        }

        // Canceling the wait alone leaves the runtime (and its mutations) running.
        await using var killOnCancellation = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between cancellation and termination.
            }
        });
        await process.WaitForExitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private ProcessStartInfo ResolveStartInfo()
    {
        var configured = getEnvironmentVariable(ExecutableEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            if (!File.Exists(configured))
            {
                throw new RuntimeVerbException(
                    $"{ExecutableEnvironmentVariable} points at '{configured}', which does not exist.");
            }

            return new ProcessStartInfo(configured) { UseShellExecute = false };
        }

        var runtimeDirectory = Path.Combine(baseDirectory, "runtime");
        var nativeHost = Path.Combine(runtimeDirectory, NativeFileName(ExecutableName));
        if (File.Exists(nativeHost))
        {
            return new ProcessStartInfo(nativeHost) { UseShellExecute = false };
        }

        var runtimeAssembly = Path.Combine(runtimeDirectory, $"{ExecutableName}.dll");
        ValidateAssembly(runtimeAssembly);
        ValidateRuntimeConfiguration(Path.Combine(runtimeDirectory, $"{ExecutableName}.runtimeconfig.json"));
        ValidateDependencies(Path.Combine(runtimeDirectory, $"{ExecutableName}.deps.json"), runtimeDirectory);

        var startInfo = new ProcessStartInfo(ResolveDotnetHost()) { UseShellExecute = false };
        startInfo.ArgumentList.Add(runtimeAssembly);
        return startInfo;
    }

    private string ResolveDotnetHost()
    {
        foreach (var candidate in DotnetHostCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode executableBits =
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(candidate) & executableBits) == 0)
                {
                    continue;
                }
            }

            return Path.GetFullPath(candidate);
        }

        throw new RuntimeVerbException(
            "the bundled runtime requires a dotnet host, but none was found. Install the .NET runtime "
            + "required by runtime/dsf-runtime.runtimeconfig.json and add dotnet to PATH, "
            + "set DOTNET_HOST_PATH to its executable, or reinstall DSF using a self-contained native archive.");
    }

    private IEnumerable<string?> DotnetHostCandidates()
    {
        yield return getEnvironmentVariable("DOTNET_HOST_PATH");
        var dotnetName = NativeFileName("dotnet");
        foreach (var path in (getEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(path.Trim('"'), dotnetName);
        }

        foreach (var variable in new[]
                 {
                     $"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}",
                     "DOTNET_ROOT",
                 })
        {
            var root = getEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, dotnetName);
            }
        }

        if (string.Equals(Path.GetFileName(Environment.ProcessPath), dotnetName, StringComparison.OrdinalIgnoreCase))
        {
            yield return Environment.ProcessPath;
        }

        // Framework-dependent CLI/tool installs can locate their own dotnet host,
        // even when invoked by an apphost with dotnet absent from PATH.
        var sharedFramework = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        yield return sharedFramework.Parent?.Parent?.Parent is { } installation
            ? Path.Combine(installation.FullName, dotnetName)
            : null;
    }

    private static string NativeFileName(string name) => OperatingSystem.IsWindows() ? $"{name}.exe" : name;

    private static void ValidateAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            throw InvalidBundle(path, exception.Message);
        }
    }

    private static void ValidateRuntimeConfiguration(string path)
    {
        ValidateJson(path, root =>
        {
            var options = root.GetProperty("runtimeOptions");
            var frameworks = options.TryGetProperty("framework", out var framework)
                ? new[] { framework }
                : options.GetProperty("frameworks").EnumerateArray().ToArray();
            if (frameworks.Length == 0 || frameworks.Any(item =>
                    string.IsNullOrWhiteSpace(item.GetProperty("name").GetString())
                    || string.IsNullOrWhiteSpace(item.GetProperty("version").GetString())))
            {
                throw new JsonException("No required .NET framework was declared.");
            }
        });
    }

    private static void ValidateDependencies(string path, string runtimeDirectory)
    {
        ValidateJson(path, root =>
        {
            var targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString()
                ?? throw new JsonException("No runtime target was declared.");
            var target = root.GetProperty("targets").GetProperty(targetName);
            var assemblies = target.EnumerateObject()
                .Where(library => library.Value.TryGetProperty("runtime", out _))
                .SelectMany(library => library.Value.GetProperty("runtime").EnumerateObject())
                .Select(asset => Path.GetFileName(asset.Name))
                .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (!assemblies.Contains($"{ExecutableName}.dll", StringComparer.Ordinal))
            {
                throw new JsonException("The runtime entry assembly is absent from the dependency manifest.");
            }

            foreach (var assembly in assemblies)
            {
                ValidateAssembly(Path.Combine(runtimeDirectory, assembly));
            }
        });
    }

    private static void ValidateJson(string path, Action<JsonElement> validate)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            validate(document.RootElement);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                         or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw InvalidBundle(path, exception.Message);
        }
    }

    private static RuntimeVerbException InvalidBundle(string path, string reason) => new(
        $"the bundled runtime component '{path}' is missing or corrupt: {reason} "
        + "Reinstall the complete matching DSF distribution (including runtime/), "
        + $"or set {ExecutableEnvironmentVariable} to a runtime executable.");
}
