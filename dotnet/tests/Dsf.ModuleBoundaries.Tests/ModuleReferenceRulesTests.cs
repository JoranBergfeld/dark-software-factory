using System.Xml.Linq;
using Xunit;

namespace Dsf.ModuleBoundaries.Tests;

/// <summary>
/// Enforces the module-reference seams for the .NET solution skeleton:
/// shared core never references application modules, CLI/provisioning never
/// references the Feature Council implementation, application modules do not
/// casually reference each other, and no production project references the
/// testing-support module.
/// </summary>
public sealed class ModuleReferenceRulesTests
{
    private const string TestingProjectName = "Dsf.Testing";

    // Allowed ProjectReference targets (by project name, without extension) for each
    // production project under src/. Any reference outside this set is forbidden.
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["Dsf.Core"] = [],
        ["Dsf.FeatureCouncil"] = ["Dsf.Core"],
        ["Dsf.Cli"] = ["Dsf.Core"],
        ["Dsf.Runtime"] = ["Dsf.Core", "Dsf.FeatureCouncil"],
        ["Dsf.ControlCenter"] = ["Dsf.Core"],
        ["Dsf.AgentHost"] = ["Dsf.Core", "Dsf.FeatureCouncil"],
        ["Dsf.Testing"] = ["Dsf.Core"],
    };

    private static DirectoryInfo FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dsf.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate Dsf.sln above the test output directory.");
        }

        return dir;
    }

    private static IReadOnlyList<string> ReadProjectReferences(FileInfo csproj)
    {
        var doc = XDocument.Load(csproj.FullName);
        return doc.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
            .ToList()!;
    }

    /// <summary>
    /// Discovers every production *.csproj under a "src" directory, identified by its
    /// full file path (not just its filename). Path-based identity is what lets the
    /// remaining checks catch a rogue project that happens to share a filename with a
    /// legitimate one (e.g. a duplicate "Dsf.Core.csproj" hiding in another folder).
    /// </summary>
    private static IReadOnlyList<(string Name, FileInfo File)> DiscoverProductionProjects(DirectoryInfo srcRoot) =>
        srcRoot.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .Select(csproj => (Name: Path.GetFileNameWithoutExtension(csproj.Name), File: csproj))
            .Where(p => p.Name != TestingProjectName)
            .OrderBy(p => p.File.FullName, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<(string Name, FileInfo File)> DiscoverProductionProjects() =>
        DiscoverProductionProjects(new DirectoryInfo(Path.Combine(FindSolutionRoot().FullName, "src")));

    /// <summary>
    /// Finds project names that resolve to more than one on-disk .csproj file. Such
    /// duplicates defeat every name-based check in this file (coverage lookups, allowed-
    /// reference lookups, and xUnit's own duplicate-theory-case de-duplication), so their
    /// presence must be an explicit, loud failure rather than a silent pass.
    /// </summary>
    private static IReadOnlyList<IGrouping<string, (string Name, FileInfo File)>> FindDuplicateProjectNames(
        IEnumerable<(string Name, FileInfo File)> projects) =>
        projects.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();

    /// <summary>
    /// Path-based (not name-reconstructed) scan: reads every discovered project file
    /// directly and reports any that reference the testing-support module, regardless of
    /// whether its filename collides with another project's.
    /// </summary>
    private static IReadOnlyList<string> FindProductionReferencesToTesting(
        IEnumerable<(string Name, FileInfo File)> projects) =>
        projects
            .Where(p => ReadProjectReferences(p.File).Contains(TestingProjectName))
            .Select(p => p.File.FullName)
            .ToList();

    [Theory]
    [MemberData(nameof(ExpectedProjectNames))]
    public void Expected_production_project_exists(string projectName)
    {
        var root = FindSolutionRoot();
        var csproj = new FileInfo(Path.Combine(root.FullName, "src", projectName, $"{projectName}.csproj"));

        Assert.True(csproj.Exists, $"Expected project file to exist: {csproj.FullName}");
    }

    [Theory]
    [MemberData(nameof(ExpectedProjectNames))]
    public void Project_only_references_its_allowed_modules(string projectName)
    {
        var root = FindSolutionRoot();
        var csproj = new FileInfo(Path.Combine(root.FullName, "src", projectName, $"{projectName}.csproj"));
        Assert.True(csproj.Exists, $"Expected project file to exist: {csproj.FullName}");

        var actual = ReadProjectReferences(csproj);
        var allowed = AllowedReferences[projectName];

        var forbidden = actual.Except(allowed).ToList();
        Assert.True(
            forbidden.Count == 0,
            $"{projectName} has forbidden ProjectReference(s): {string.Join(", ", forbidden)}. " +
            $"Allowed: {string.Join(", ", allowed)}");
    }

    [Fact]
    public void No_duplicate_production_project_filenames_are_discovered()
    {
        var duplicates = FindDuplicateProjectNames(DiscoverProductionProjects());

        Assert.True(
            duplicates.Count == 0,
            "Duplicate production project filename(s) found; this defeats name-based module-boundary " +
            "policy checks (coverage lookup, allowed-reference lookup, and xUnit theory-case identity). " +
            "Rename the project(s) so every production .csproj filename is unique: " +
            string.Join("; ", duplicates.Select(g => $"{g.Key}: [{string.Join(", ", g.Select(p => p.File.FullName))}]")));
    }

    [Fact]
    public void Every_production_project_has_reference_policy_coverage()
    {
        var discovered = DiscoverProductionProjects().Select(p => p.Name).Distinct().ToList();

        var uncovered = discovered.Except(AllowedReferences.Keys).ToList();

        Assert.True(
            uncovered.Count == 0,
            "Production project(s) missing module-reference policy coverage: " +
            $"{string.Join(", ", uncovered)}. Add them to {nameof(AllowedReferences)}.");
    }

    [Theory]
    [MemberData(nameof(ProductionProjectNames))]
    public void Production_project_never_references_testing_support(string projectName)
    {
        var root = FindSolutionRoot();
        var csproj = new FileInfo(Path.Combine(root.FullName, "src", projectName, $"{projectName}.csproj"));
        Assert.True(csproj.Exists, $"Expected project file to exist: {csproj.FullName}");

        var actual = ReadProjectReferences(csproj);

        Assert.DoesNotContain(TestingProjectName, actual);
    }

    /// <summary>
    /// Path-based counterpart to <see cref="Production_project_never_references_testing_support"/>:
    /// scans every discovered .csproj by its actual file path, so a rogue project cannot evade
    /// detection by sharing a filename with an already-covered, legitimate project.
    /// </summary>
    [Fact]
    public void No_production_project_path_references_testing_support()
    {
        var offenders = FindProductionReferencesToTesting(DiscoverProductionProjects());

        Assert.True(
            offenders.Count == 0,
            "Production project(s) reference the testing-support module " +
            $"({TestingProjectName}): {string.Join(", ", offenders)}");
    }

    public static IEnumerable<object[]> ExpectedProjectNames() => AllowedReferences.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> ProductionProjectNames() =>
        DiscoverProductionProjects().Select(p => p.Name).Distinct().Select(k => new object[] { k });

    public sealed class DuplicateProjectDetectionTests
    {
        private static DirectoryInfo CreateTempSrcTree()
        {
            var tempRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "dsf-module-boundary-tests-" + Guid.NewGuid()));
            tempRoot.Create();
            return tempRoot;
        }

        private static void WriteProject(DirectoryInfo srcRoot, string folderName, string fileNameWithoutExtension, string? projectReferenceRelativePath)
        {
            var projectDir = new DirectoryInfo(Path.Combine(srcRoot.FullName, folderName));
            projectDir.Create();

            var references = projectReferenceRelativePath is null
                ? string.Empty
                : $"""

                      <ItemGroup>
                        <ProjectReference Include="{projectReferenceRelativePath}" />
                      </ItemGroup>
                  """;

            File.WriteAllText(
                Path.Combine(projectDir.FullName, $"{fileNameWithoutExtension}.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>{references}
                </Project>
                """);
        }

        [Fact]
        public void Duplicate_filename_referencing_testing_support_is_caught_by_path_based_scan()
        {
            var tempRoot = CreateTempSrcTree();
            try
            {
                // Legitimate project, matching the real repo's convention: src/Dsf.Core/Dsf.Core.csproj
                WriteProject(tempRoot, "Dsf.Core", "Dsf.Core", projectReferenceRelativePath: null);

                // Rogue duplicate: different folder, same filename, references Dsf.Testing.
                WriteProject(
                    tempRoot,
                    "UncoveredDuplicate",
                    "Dsf.Core",
                    projectReferenceRelativePath: "../Dsf.Testing/Dsf.Testing.csproj");

                var discovered = DiscoverProductionProjects(tempRoot);

                // Coverage-by-name alone would not catch this: both projects resolve to the
                // covered name "Dsf.Core".
                var uncoveredNames = discovered.Select(p => p.Name).Distinct()
                    .Except(new[] { "Dsf.Core" })
                    .ToList();
                Assert.Empty(uncoveredNames);

                // But duplicate-name rejection must fail loudly.
                var duplicates = FindDuplicateProjectNames(discovered);
                Assert.Single(duplicates);
                Assert.Equal("Dsf.Core", duplicates[0].Key);
                Assert.Equal(2, duplicates[0].Count());

                // And the path-based scan must catch the rogue reference regardless of the
                // filename collision.
                var offenders = FindProductionReferencesToTesting(discovered);
                Assert.Single(offenders);
                Assert.Contains("UncoveredDuplicate", offenders[0]);
            }
            finally
            {
                tempRoot.Delete(recursive: true);
            }
        }

        [Fact]
        public void Unique_filenames_with_no_testing_reference_pass_clean()
        {
            var tempRoot = CreateTempSrcTree();
            try
            {
                WriteProject(tempRoot, "Dsf.Core", "Dsf.Core", projectReferenceRelativePath: null);
                WriteProject(tempRoot, "Dsf.FeatureCouncil", "Dsf.FeatureCouncil", projectReferenceRelativePath: "../Dsf.Core/Dsf.Core.csproj");

                var discovered = DiscoverProductionProjects(tempRoot);

                Assert.Empty(FindDuplicateProjectNames(discovered));
                Assert.Empty(FindProductionReferencesToTesting(discovered));
            }
            finally
            {
                tempRoot.Delete(recursive: true);
            }
        }
    }
}
