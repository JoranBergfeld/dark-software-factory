using System.Text.Json.Nodes;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CutoverAcceptanceTests
{
    private static readonly string[] RemovedPythonWorkspacePaths =
    [
        "pyproject.toml",
        "uv.lock",
        "core",
        "feature-council",
        "cli",
        "control-center",
        "testing",
        "tests",
        "dotnet/eng/generate-release-metadata.py",
    ];

    [Fact]
    public void Cutover_removes_the_python_workspace_and_its_implementation_packages()
    {
        var root = FindRepoRoot().FullName;

        foreach (var relativePath in RemovedPythonWorkspacePaths)
        {
            var path = Path.Combine(root, relativePath);
            Assert.False(
                File.Exists(path) || Directory.Exists(path),
                $"Python implementation surface remains: {relativePath}");
        }
    }

    [Fact]
    public void Current_workflows_are_dotnet_only_and_build_the_dotnet_agent_host()
    {
        var root = FindRepoRoot().FullName;
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var docs = ReadRepoFile(".github/workflows/docs.yml");
        var images = ReadRepoFile(".github/workflows/agents-images.yml");

        Assert.False(File.Exists(Path.Combine(root, ".github/workflows/dotnet-ci.yml")));
        Assert.Contains("dotnet restore Dsf.sln --locked-mode", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test Dsf.sln --no-build", ci, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~CutoverAcceptanceTests", ci, StringComparison.Ordinal);

        foreach (var workflow in new[] { ci, docs, images })
        {
            Assert.DoesNotContain("setup-uv", workflow, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("uv ", workflow, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pytest", workflow, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ruff", workflow, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pyproject", workflow, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("dotnet/src/Dsf.AgentHost/Dockerfile", images, StringComparison.Ordinal);
        Assert.Contains("docker/metadata-action", images, StringComparison.Ordinal);
        Assert.Contains("type=raw,value=latest", images, StringComparison.Ordinal);
        Assert.Contains("aquasecurity/trivy-action", images, StringComparison.Ordinal);
        Assert.Contains("docker run --rm dsf-runtime:scan --help", images, StringComparison.Ordinal);
        Assert.Contains("docker run --rm dsf-runtime:scan --version", images, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "dotnet/src/Dsf.AgentHost/Dockerfile")));
        Assert.True(
            File.Exists(Path.Combine(root, "dotnet/eng/ReleaseMetadataGenerator/ReleaseMetadataGenerator.csproj")));
    }

    [Fact]
    public void Dotnet_parity_gate_consumes_every_authoritative_matrix_surface()
    {
        var matrix = JsonNode.Parse(ReadRepoFile("parity/baseline/matrix.json"))
            ?? throw new InvalidOperationException("Parity matrix is empty.");
        var authoritativeSurfaces = matrix["surfaces"]!.AsArray()
            .Where(surface => surface?["authority"]?.GetValue<string>() == "authoritative")
            .Select(surface => surface!["surface"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var coveredSurfaces = new[]
        {
            "blackboard contracts",
            "control-center GET /api/state",
            "control-center POST /set-value",
            "control-center POST /toggle",
            "dsf delete/deprovision",
            "dsf list",
            "dsf new",
            "dsf offboard",
            "dsf run",
            "dsf serve-agent",
            "dsf serve-orchestrator",
            "dsf sweep",
            "dsf-control-center",
        }.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(authoritativeSurfaces, coveredSurfaces);

        foreach (var surface in matrix["surfaces"]!.AsArray()
                     .Where(surface => surface?["authority"]?.GetValue<string>() == "authoritative"))
        {
            foreach (var evidencePath in surface!["evidence"]!.AsArray()
                         .Select(path => path!.GetValue<string>()))
            {
                var evidence = ReadRepoFile($"parity/baseline/{evidencePath}");
                Assert.NotEqual(string.Empty, evidence.Trim());
            }
        }
    }

    [Fact]
    public async Task Command_evidence_drives_executable_dotnet_parity_checks()
    {
        AssertDsfParserSnapshotMatchesDotnetSurface();
        await AssertNewDryRunMatchesCommandEvidenceAsync();
        await AssertProcessMatchesCommandEvidenceAsync("dsf-new-invalid-prefix.json");
        await AssertProcessMatchesCommandEvidenceAsync("dsf-delete-missing-manifest.json");
        await AssertProcessMatchesCommandEvidenceAsync("dsf-runtime-run-missing-env.json");
        await AssertProcessMatchesCommandEvidenceAsync("dsf-runtime-sweep-missing-env.json");
        await AssertOffboardMissingManifestUsesDotnetErrorAsync();

        var listEvidence = LoadCommandEvidence("dsf-list-json-no-owner-index.json");
        await WithEnvironmentAsync("DSF_OWNER_APPCONFIG_ENDPOINT", "https://owner.azconfig.io", async () =>
        {
            var terminal = NonInteractiveTerminal();
            var exitCode = await CliApplication.InvokeAsync(
                TrimExecutable(listEvidence.Argv),
                CancellationToken.None,
                terminal,
                new RecordingGitHubProvisioningClient(),
                new RecordingAzureProvisioningClient(),
                new RecordingAppConfigurationClient(),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(listEvidence.ExitCode, exitCode);
            Assert.Equal(listEvidence.Stdout, terminal.Output);
            Assert.Equal(listEvidence.Stderr, terminal.Error);
        });
    }

    private static void AssertDsfParserSnapshotMatchesDotnetSurface()
    {
        var snapshot = JsonNode.Parse(ReadRepoFile(
            "parity/baseline/evidence/commands/dsf-parser-surface-snapshot.json"))
            ?? throw new InvalidOperationException("CLI parser snapshot is empty.");
        var rootCommandNames = CliApplication.BuildRootCommand()
            .Subcommands
            .Select(command => command.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var @case in snapshot["cases"]!.AsArray())
        {
            var command = @case!["namespace"]!["command"]!.GetValue<string>();
            Assert.Contains(command, rootCommandNames);
        }
    }

    [Fact]
    public void Cutover_preserves_frozen_parity_evidence_and_records_merge_time_archive_steps()
    {
        var root = FindRepoRoot().FullName;
        var matrix = Path.Combine(root, "parity/baseline/matrix.json");
        var evidence = Path.Combine(root, "parity/baseline/evidence");
        var checklist = ReadRepoFile("parity/baseline/CUTOVER.md");

        Assert.True(File.Exists(matrix), "Frozen parity matrix is required.");
        Assert.True(Directory.Exists(evidence), "Frozen parity evidence is required.");
        Assert.Contains("archive/python-final", checklist, StringComparison.Ordinal);
        Assert.Contains("after the cutover commit is merged", checklist, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delete the migration branch", checklist, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hotfix .NET", checklist, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dotnet_replacement_starts_at_version_0_0_1_and_keeps_six_rid_smoke_gates()
    {
        var props = ReadRepoFile("dotnet/Directory.Build.props");
        var release = ReadRepoFile(".github/workflows/dotnet-release.yml");

        Assert.Contains("<Version>0.0.1</Version>", props, StringComparison.Ordinal);
        foreach (var rid in new[]
                 {
                     "linux-x64",
                     "linux-arm64",
                     "osx-x64",
                     "osx-arm64",
                     "win-x64",
                     "win-arm64",
                 })
        {
            Assert.Contains($"rid: {rid}", release, StringComparison.Ordinal);
        }

        Assert.Contains("Smoke test published CLI artifact", release, StringComparison.Ordinal);
        Assert.Contains("environment: release-nuget-publish", release, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot().FullName, relativePath))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task AssertProcessMatchesCommandEvidenceAsync(string fileName)
    {
        var evidence = LoadCommandEvidence(fileName);
        var result = await RunDsfProcessAsync(NormalizeArgv(evidence.Argv));

        Assert.Equal(evidence.ExitCode, result.ExitCode);
        Assert.Equal(NormalizeRepoToken(evidence.Stdout), result.Stdout);
        Assert.Equal(NormalizeRepoToken(evidence.Stderr), result.Stderr);
    }

    private static async Task AssertNewDryRunMatchesCommandEvidenceAsync()
    {
        var evidence = LoadCommandEvidence("dsf-new-dry-run-write-plan.json");
        var captureRoot = Path.Combine(FindRepoRoot().FullName, ".parity-capture");
        try
        {
            var result = await RunDsfProcessAsync(NormalizeArgv(evidence.Argv));

            Assert.Equal(evidence.ExitCode, result.ExitCode);
            Assert.Equal(NormalizeRepoToken(evidence.Stdout), result.Stdout);
            Assert.Equal(evidence.Stderr, result.Stderr);
        }
        finally
        {
            if (Directory.Exists(captureRoot))
            {
                Directory.Delete(captureRoot, recursive: true);
            }
        }
    }

    private static async Task AssertOffboardMissingManifestUsesDotnetErrorAsync()
    {
        var evidence = LoadCommandEvidence("dsf-offboard-dry-run-missing-manifest.json");
        var result = await RunDsfProcessAsync(NormalizeArgv(evidence.Argv));

        Assert.Contains("FileNotFoundError", evidence.Stderr, StringComparison.Ordinal);
        Assert.Equal(evidence.ExitCode, result.ExitCode);
        Assert.Equal(evidence.Stdout, result.Stdout);
        Assert.DoesNotContain("Traceback", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Instance manifest not found for product 'ghost'", result.Stderr);
        Assert.Contains("Offboard requires config/instances/ghost.json", result.Stderr);
    }

    private static CommandEvidence LoadCommandEvidence(string fileName)
    {
        var path = $"parity/baseline/evidence/commands/{fileName}";
        var json = JsonNode.Parse(ReadRepoFile(path))
            ?? throw new InvalidOperationException($"Command evidence is empty: {path}");
        return new CommandEvidence(
            json["argv"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray(),
            json["exit_code"]!.GetValue<int>(),
            json["stdout"]!.GetValue<string>(),
            json["stderr"]!.GetValue<string>());
    }

    private static string[] NormalizeArgv(IReadOnlyList<string> argv)
        => TrimExecutable(argv).Select(NormalizeRepoToken).ToArray();

    private static string[] TrimExecutable(IReadOnlyList<string> argv)
    {
        Assert.NotEmpty(argv);
        Assert.Equal("dsf", argv[0]);
        return argv.Skip(1).ToArray();
    }

    private static string NormalizeRepoToken(string value) =>
        value.Replace("<repo>", FindRepoRoot().FullName, StringComparison.Ordinal);

    private static async Task<CommandResult> RunDsfProcessAsync(params string[] args)
    {
        var solution = FindSolutionRoot();
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = solution.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment.Remove("DSF_PRODUCT");
        startInfo.Environment["DSF_RUNTIME_HOST"] = FindRuntimeHostExecutable();
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("src/Dsf.Cli/Dsf.Cli.csproj");
        startInfo.ArgumentList.Add("--");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("dotnet run failed to start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindRuntimeHostExecutable()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var fileName = OperatingSystem.IsWindows() ? "dsf-runtime.exe" : "dsf-runtime";
        var path = Path.Combine(
            FindSolutionRoot().FullName,
            "src",
            "Dsf.Runtime",
            "bin",
            configuration,
            "net10.0",
            fileName);

        Assert.True(File.Exists(path), $"Expected the runtime host executable at {path}.");
        return path;
    }

    private static DirectoryInfo FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dsf.sln")))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException("Could not locate Dsf.sln.");
    }

    private static ScriptedTerminal NonInteractiveTerminal() => new(
        new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false),
        []);

    private static async Task WithEnvironmentAsync(string name, string value, Func<Task> action)
    {
        var prior = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, prior);
        }
    }

    private static DirectoryInfo FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".git"))
               && !File.Exists(Path.Combine(current.FullName, ".git")))
        {
            current = current.Parent;
        }

        return current ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed record CommandEvidence(
        IReadOnlyList<string> Argv,
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
}
