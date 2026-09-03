using System.Xml.Linq;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class ReleaseArtifactAutomationTests
{
    private static readonly string[] ExpectedRids =
    [
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
        "win-x64",
        "win-arm64",
    ];

    [Fact]
    public void Cli_project_packs_as_global_tool_at_directory_version()
    {
        var root = FindRepoRoot().FullName;
        var directoryProps = XDocument.Load(Path.Combine(root, "dotnet", "Directory.Build.props"));
        var cliProject = XDocument.Load(Path.Combine(root, "dotnet", "src", "Dsf.Cli", "Dsf.Cli.csproj"));

        Assert.Equal(
            "0.0.1",
            directoryProps.Root!.Elements("PropertyGroup").Elements("Version").Single().Value);
        Assert.Equal(
            "true",
            cliProject.Root!.Elements("PropertyGroup").Elements("PackAsTool").Single().Value);
        Assert.Equal(
            "dsf",
            cliProject.Root!.Elements("PropertyGroup").Elements("ToolCommandName").Single().Value);
        Assert.Equal(
            "true",
            cliProject.Root!.Elements("PropertyGroup").Elements("IsPackable").Single().Value);
    }

    [Fact]
    public void Release_workflow_is_manual_jbergfeld_only_and_builds_all_release_shapes()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("github.actor == 'jbergfeld'", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet pack src/Dsf.Cli/Dsf.Cli.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishTrimmed=false", workflow, StringComparison.Ordinal);

        foreach (var rid in ExpectedRids)
        {
            Assert.Contains(rid, workflow, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Release_workflow_smoke_tests_every_rid_before_any_publish_gate()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        foreach (var rid in ExpectedRids)
        {
            Assert.Contains($"rid: {rid}", workflow, StringComparison.Ordinal);
        }

        AssertContainsBefore(
            workflow,
            "Smoke test published CLI artifact",
            "environment: release-nuget-publish");
        AssertContainsBefore(
            workflow,
            "needs: [build-and-smoke-test, sign-windows, sign-macos, test-and-pack]",
            "environment: release-nuget-publish");
    }

    [Fact]
    public void Release_workflow_keeps_signing_and_publish_hooks_behind_protected_environments()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.Contains("environment: release-windows-signing", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: release-macos-notarization", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: release-nuget-publish", workflow, StringComparison.Ordinal);
        Assert.Contains("signtool", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notarytool", workflow, StringComparison.Ordinal);
        Assert.Contains("NUGET_API_KEY", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_metadata_scripts_generate_final_byte_hashes_sboms_signatures_and_native_metadata()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");
        var manifest = ReadRepoFile("dotnet", "eng", "release-manifest.json");
        var metadataScript = ReadRepoFile("dotnet", "eng", "generate-release-metadata.py");

        Assert.Contains("final-artifacts", workflow, StringComparison.Ordinal);
        Assert.Contains("Generate hashes, SBOMs, provenance, keys, native metadata", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/attest-build-provenance", workflow, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", metadataScript, StringComparison.Ordinal);
        Assert.Contains("SPDX-2.3", metadataScript, StringComparison.Ordinal);
        Assert.Contains(".spdx.json", metadataScript, StringComparison.Ordinal);
        Assert.Contains(".spdx.json.sig", metadataScript, StringComparison.Ordinal);
        Assert.Contains("release-verification-key.pem", metadataScript, StringComparison.Ordinal);
        Assert.Contains("provenance.json", metadataScript, StringComparison.Ordinal);
        Assert.Contains("winget-portable", manifest, StringComparison.Ordinal);
        Assert.Contains("homebrew-cask", manifest, StringComparison.Ordinal);
        Assert.Contains("debian", manifest, StringComparison.Ordinal);
        Assert.Contains("rpm", manifest, StringComparison.Ordinal);
        Assert.Contains("OpenPGP", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotnet_ci_runs_release_static_tests_when_release_workflow_changes()
    {
        var ciWorkflow = ReadRepoFile(".github", "workflows", "dotnet-ci.yml");

        Assert.Contains(".github/workflows/dotnet-release.yml", ciWorkflow, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        return File.ReadAllText(Path.Combine(new[] { FindRepoRoot().FullName }.Concat(parts).ToArray()));
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

    private static void AssertContainsBefore(string text, string earlier, string later)
    {
        var earlierIndex = text.IndexOf(earlier, StringComparison.Ordinal);
        var laterIndex = text.IndexOf(later, StringComparison.Ordinal);

        Assert.True(earlierIndex >= 0, $"Missing '{earlier}'.");
        Assert.True(laterIndex >= 0, $"Missing '{later}'.");
        Assert.True(earlierIndex < laterIndex, $"Expected '{earlier}' before '{later}'.");
    }
}
