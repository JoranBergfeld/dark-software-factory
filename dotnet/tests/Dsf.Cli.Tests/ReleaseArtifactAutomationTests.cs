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
    public void Release_workflow_is_manual_JoranBergfeld_only_and_builds_all_release_shapes()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("github.actor == 'JoranBergfeld'", workflow, StringComparison.Ordinal);
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
        var metadataGenerator = ReadRepoFile(
            "dotnet",
            "eng",
            "ReleaseMetadataGenerator",
            "Program.cs");

        Assert.Contains("final-artifacts", workflow, StringComparison.Ordinal);
        Assert.Contains("Generate hashes, SBOMs, provenance, keys, native metadata", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/attest-build-provenance", workflow, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("SPDX-2.3", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains(".spdx.json", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains(".spdx.json.sig", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("release-verification-key.pem", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("provenance.json", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("winget-portable", manifest, StringComparison.Ordinal);
        Assert.Contains("homebrew-cask", manifest, StringComparison.Ordinal);
        Assert.Contains("debian", manifest, StringComparison.Ordinal);
        Assert.Contains("rpm", manifest, StringComparison.Ordinal);
        Assert.Contains("OpenPGP", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_runs_release_static_tests_when_release_workflow_changes()
    {
        var ciWorkflow = ReadRepoFile(".github", "workflows", "ci.yml");

        Assert.DoesNotContain("paths:", ciWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Signing_jobs_never_reference_nonexistent_downloaded_payload_subdirectory()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.DoesNotContain("unsigned-payload-win-*/payload", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("unsigned-payload-osx-*/payload", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("/payload/*", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("payload\"; do", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_matrix_uses_hosted_arm_runners_for_native_arm_smoke_tests()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.Contains("os: ubuntu-24.04-arm", workflow, StringComparison.Ordinal);
        Assert.Contains("os: windows-11-arm", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Nuget_package_is_signed_behind_a_protected_environment_before_publish()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.Contains("dotnet nuget sign", workflow, StringComparison.Ordinal);
        Assert.Contains("NUGET_SIGNING_CERTIFICATE_BASE64", workflow, StringComparison.Ordinal);

        AssertContainsBefore(
            workflow,
            "dotnet nuget sign",
            "dotnet run --project dotnet/eng/ReleaseMetadataGenerator");
        AssertContainsBefore(workflow, "dotnet nuget sign", "publish-nuget:");
    }

    [Fact]
    public void Release_signing_key_secret_is_passed_via_env_not_interpolated_into_shell()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.DoesNotContain(
            "printf \'%s\' \"${{ secrets.RELEASE_ED25519_PRIVATE_KEY_PEM }}\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "RELEASE_ED25519_PRIVATE_KEY_PEM: ${{ secrets.RELEASE_ED25519_PRIVATE_KEY_PEM }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("printf \'%s\' \"$RELEASE_ED25519_PRIVATE_KEY_PEM\"", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Sboms_include_dependency_components_and_relationships()
    {
        var metadataGenerator = ReadRepoFile(
            "dotnet",
            "eng",
            "ReleaseMetadataGenerator",
            "Program.cs");

        Assert.Contains("packages.lock.json", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("CollectLockfileComponents", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("ComponentSpdxId", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("relationships =", metadataGenerator, StringComparison.Ordinal);
        Assert.Contains("DEPENDS_ON", metadataGenerator, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_sbom_for_a_sample_artifact_lists_real_nuget_components_and_relationships()
    {
        var repoRoot = FindRepoRoot().FullName;
        var tempRoot = Directory.CreateTempSubdirectory("dsf-release-metadata-test-");
        try
        {
            var artifactRoot = Path.Combine(tempRoot.FullName, "final-artifacts");
            Directory.CreateDirectory(artifactRoot);
            File.WriteAllText(Path.Combine(artifactRoot, "dsf-cli-linux-x64.tar.gz"), "fake-archive-bytes");

            var keyPath = Path.Combine(tempRoot.FullName, "signing-key.pem");
            RunProcess(repoRoot, "openssl", $"genpkey -algorithm Ed25519 -out \"{keyPath}\"");

            RunProcess(
                repoRoot,
                "dotnet",
                $"run --project \"{Path.Combine(repoRoot, "dotnet", "eng", "ReleaseMetadataGenerator")}\" -- " +
                $"--artifact-root \"{artifactRoot}\" --version 9.9.9 --commit deadbeef " +
                $"--repository dark-software-factory/dark-software-factory --run-id 1 --private-key \"{keyPath}\"");

            var sbomPath = Directory.GetFiles(Path.Combine(artifactRoot, "release-metadata"), "*.spdx.json")
                .Single(path => path.Contains("linux-x64", StringComparison.Ordinal));
            var sbomJson = File.ReadAllText(sbomPath);

            Assert.Contains("System.CommandLine", sbomJson, StringComparison.Ordinal);
            Assert.Contains("DEPENDS_ON", sbomJson, StringComparison.Ordinal);
            Assert.Contains("relationships", sbomJson, StringComparison.Ordinal);

            var hashesPath = Path.Combine(artifactRoot, "release-metadata", "SHA256SUMS");
            var hashes = File.ReadAllText(hashesPath);
            Assert.Contains("native-metadata/winget-portable.yaml", hashes, StringComparison.Ordinal);

            var provenancePath = Path.Combine(artifactRoot, "release-metadata", "provenance.json");
            var provenance = File.ReadAllText(provenancePath);
            Assert.Contains("native-metadata/winget-portable.yaml", provenance, StringComparison.Ordinal);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    private static void RunProcess(string workingDirectory, string fileName, string arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{fileName} {arguments} failed: {stdout}\n{stderr}");
    }

    [Fact]
    public void Native_metadata_files_are_included_in_hashes_and_provenance_subjects()
    {
        var metadataGenerator = ReadRepoFile(
            "dotnet",
            "eng",
            "ReleaseMetadataGenerator",
            "Program.cs");

        Assert.Contains("WriteNativeMetadata", metadataGenerator, StringComparison.Ordinal);

        var callNativeIndex = metadataGenerator.IndexOf("WriteNativeMetadata(", StringComparison.Ordinal);
        var callHashesIndex = metadataGenerator.IndexOf("WriteHashes(", StringComparison.Ordinal);

        Assert.True(callNativeIndex >= 0);
        Assert.True(callHashesIndex >= 0);
        Assert.True(
            callNativeIndex < callHashesIndex,
            "native metadata must be generated before hashes/provenance are collected so it is included as a release asset");

        Assert.DoesNotContain(
            "GeneratedDirectories = [\"release-metadata\", \"native-metadata\"]",
            metadataGenerator,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_creates_github_release_and_uploads_assets_before_publish()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n      contents: write", workflow.Replace("\r\n", "\n"), StringComparison.Ordinal);

        AssertContainsBefore(workflow, "gh release create", "publish-nuget:");
        AssertContainsBefore(
            workflow,
            "needs: [build-and-smoke-test, sign-windows, sign-macos, test-and-pack]",
            "gh release create");
    }

    [Fact]
    public void Nuget_publish_expands_packages_and_fails_when_no_packages_exist()
    {
        var workflow = ReadRepoFile(".github", "workflows", "dotnet-release.yml");

        Assert.DoesNotContain(
            "dotnet nuget push \"dotnet/artifacts/final-artifacts/*.nupkg\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("shopt -s nullglob", workflow, StringComparison.Ordinal);
        Assert.Contains("packages=(dotnet/artifacts/final-artifacts/*.nupkg)", workflow, StringComparison.Ordinal);
        Assert.Contains("if (( ${#packages[@]} == 0 )); then", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget push \"$package\"", workflow, StringComparison.Ordinal);
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
