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
        Assert.Equal(
            """
            Description:
              Dark Software Factory — factory CLI (create product instances)

            Usage:
              dsf [command] [options]

            Options:
              -h, --help  Show help and usage information

            Commands:
              new                    create a new isolated product factory instance
              list, ls               list provisioned product factories from the owner App Config index
              offboard <product>     remove Azure/runtime artifacts for a product
              bootstrap              one-time: create the DSF GitHub App and store it in the owner Key Vault
              delete <product>       permanently destroy a product factory instance
              deprovision <product>  permanently destroy a product factory instance
              run                    run the intake line for one signal (runtime)
              sweep                  sweep enabled source agents once (runtime)
              serve-orchestrator     run the orchestrator worker (runtime)
              serve-agent            serve a source agent over A2A (runtime)
              charter                manage the product charter (.dsf/charter.md)


            """.Replace("\r\n", "\n"),
            result.Stdout.Replace("\r\n", "\n"));
        Assert.DoesNotContain("/?", result.Stdout);
    }

    [Fact]
    public async Task New_help_exposes_frozen_options()
    {
        var result = await DsfProcess.RunAsync("new", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """
            Description:
              create a new isolated product factory instance

            Usage:
              dsf new [options]

            Options:
              --product <product> (REQUIRED)                         product key (e.g. 'microbi')
              --owner <owner>                                        GitHub owner/org for the product repo
              --repo <repo>                                          repo name (defaults to product key)
              --visibility <internal|private|public>                 product repo visibility [default: private]
              --runtime-target <aca>                                 where the factory runtime is hosted [default: aca]
              --name-prefix <name-prefix>                            base Azure resource name prefix
              --environment <environment>                            Azure environment moniker [default: dev]
              --location <location>                                  Azure region [default: swedencentral]
              --creation-maturity <high|low>                         creation-phase autonomy [default: low]
              --dry-run                                              preview only: print the what-if plan without running steps
              --no-charter                                           skip the post-provision charter prompt
              --write-plan                                           with --dry-run, still write the instance manifest
              --config-root <config-root>                            override repo root where config/instances/ is written
              --owner-keyvault-uri <owner-keyvault-uri>              owner Key Vault URI
              --owner-appconfig-endpoint <owner-appconfig-endpoint>  owner App Configuration endpoint
              --admin-principal-id <admin-principal-id>              human owner/governance principal object id
              -h, --help                                             Show help and usage information


            """.Replace("\r\n", "\n"),
            result.Stdout.Replace("\r\n", "\n"));
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
    public async Task New_dry_run_write_plan_prints_deterministic_path_without_filesystem_writes()
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
            Assert.Contains($"[dsf]  15. write_config   [{manifestPath}]", result.Stdout);
            Assert.False(Directory.Exists(configRoot), $"Dry-run must not create {configRoot}");
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
    public async Task Unknown_command_and_invalid_option_return_python_parity_exit_2()
    {
        var unknown = await DsfProcess.RunAsync("wat");
        var invalid = await DsfProcess.RunAsync("new", "--product", "demo", "--definitely-invalid");

        Assert.Equal(2, unknown.ExitCode);
        Assert.Equal(2, invalid.ExitCode);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("/h")]
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
