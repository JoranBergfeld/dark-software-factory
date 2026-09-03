using System.Diagnostics;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CliSurfaceTests
{
    [Fact]
    public async Task Top_level_help_exposes_frozen_command_grammar()
    {
        var result = await DsfProcess.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("new", result.Stdout);
        Assert.Contains("list", result.Stdout);
        Assert.Contains("offboard", result.Stdout);
        Assert.Contains("delete", result.Stdout);
        Assert.Contains("deprovision", result.Stdout);
        Assert.Contains("bootstrap", result.Stdout);
        Assert.Contains("run", result.Stdout);
        Assert.Contains("sweep", result.Stdout);
        Assert.Contains("serve-orchestrator", result.Stdout);
        Assert.Contains("serve-agent", result.Stdout);
        Assert.Contains("charter", result.Stdout);
        Assert.DoesNotContain("/?", result.Stdout);
    }

    [Fact]
    public async Task New_help_exposes_frozen_options()
    {
        var result = await DsfProcess.RunAsync("new", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--product", result.Stdout);
        Assert.Contains("--owner", result.Stdout);
        Assert.Contains("--repo", result.Stdout);
        Assert.Contains("--visibility", result.Stdout);
        Assert.Contains("--runtime-target", result.Stdout);
        Assert.Contains("--name-prefix", result.Stdout);
        Assert.Contains("--creation-maturity", result.Stdout);
        Assert.Contains("--dry-run", result.Stdout);
        Assert.Contains("--write-plan", result.Stdout);
        Assert.Contains("--config-root", result.Stdout);
        Assert.Contains("--owner-keyvault-uri", result.Stdout);
        Assert.Contains("--owner-appconfig-endpoint", result.Stdout);
        Assert.Contains("--admin-principal-id", result.Stdout);
    }

    [Fact]
    public async Task List_json_is_deterministic_and_noninteractive()
    {
        var result = await DsfProcess.RunAsync("list", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[]\n", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task New_rejects_invalid_explicit_name_prefix_with_parity_exit()
    {
        var result = await DsfProcess.RunAsync(
            "new", "--product", "demo", "--owner", "acme", "--name-prefix", "123", "--dry-run");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            "[dsf] error: cannot derive an Azure name prefix from '123': name prefix base must start with a letter: '123' Pass --name-prefix explicitly.\n",
            result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task New_dry_run_write_plan_prints_and_persists_deterministic_shell_manifest()
    {
        var root = DsfProcess.FindSolutionRoot();
        var configRoot = Path.Combine(root.FullName, ".test-artifacts", "cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await DsfProcess.RunAsync(
                "new",
                "--product",
                "paritydemo",
                "--owner",
                "acme",
                "--name-prefix",
                "paritydemo",
                "--dry-run",
                "--write-plan",
                "--config-root",
                configRoot);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("[dsf] WARNING: DSF_OWNER_KEYVAULT_URI is unset", result.Stdout);
            Assert.Contains("[dsf]  1. create_repo", result.Stdout);
            Assert.Contains("[dsf]  15. write_config", result.Stdout);
            Assert.Equal(string.Empty, result.Stderr);

            var manifestPath = Path.Combine(configRoot, "config", "instances", "paritydemo.json");
            Assert.True(File.Exists(manifestPath), $"Expected manifest at {manifestPath}");
            var manifest = await File.ReadAllTextAsync(manifestPath);
            Assert.Contains("\"product\": \"paritydemo\"", manifest);
            Assert.Contains("\"owner\": \"acme\"", manifest);
            Assert.Contains("\"name_prefix\": \"parityde0000\"", manifest);
            Assert.Contains("\"executed\": false", manifest);
        }
        finally
        {
            if (Directory.Exists(configRoot))
            {
                Directory.Delete(configRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Runtime_run_without_product_preserves_missing_env_failure()
    {
        var result = await DsfProcess.RunAsync("run", "--dry-run", "--signal", "signals/demo.json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(
            "[dsf] error: DSF_PRODUCT is required to scope the factory runtime (set DSF_PRODUCT=<product>).\n",
            result.Stderr);
    }

    [Fact]
    public async Task Unknown_command_and_invalid_option_return_nonzero()
    {
        var unknown = await DsfProcess.RunAsync("wat");
        var invalid = await DsfProcess.RunAsync("new", "--product", "demo", "--definitely-invalid");

        Assert.NotEqual(0, unknown.ExitCode);
        Assert.NotEqual(0, invalid.ExitCode);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-?")]
    [InlineData("/?")]
    public async Task Frozen_root_grammar_rejects_non_parity_options(string option)
    {
        var result = await DsfProcess.RunAsync(option);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("status")]
    public async Task Charter_source_commands_reject_file_and_ref_together(string command)
    {
        var result = await DsfProcess.RunAsync(
            "charter", command, "--product", "demo", "--file", "charter.md", "--ref", "main");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task New_accepts_and_normalizes_uppercase_explicit_name_prefix()
    {
        var result = await DsfProcess.RunAsync(
            "new", "--product", "demo", "--name-prefix", "Demo", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("namePrefix=demoxxxx0000", result.Stdout);
    }

    [Fact]
    public async Task Canceled_invocation_returns_nonzero_without_dispatching_shell()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await CliApplication.InvokeAsync(["list"], cts.Token);

        Assert.Equal(CliApplication.CanceledExitCode, result);
    }

    private static class DsfProcess
    {
        public static async Task<CommandResult> RunAsync(params string[] args)
        {
            var root = FindSolutionRoot();
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment.Remove("DSF_PRODUCT");
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add("src/Dsf.Cli/Dsf.Cli.csproj");
            startInfo.ArgumentList.Add("--");
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet run failed to start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"dotnet run for '{string.Join(' ', args)}' did not exit within 60s; killed the process tree.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new CommandResult(process.ExitCode, stdout, stderr);
        }

        public static DirectoryInfo FindSolutionRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dsf.sln")))
            {
                dir = dir.Parent;
            }

            return dir ?? throw new InvalidOperationException("Could not locate Dsf.sln.");
        }
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
}
