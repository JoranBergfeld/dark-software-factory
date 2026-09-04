using System.Text.Json.Nodes;
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
}
