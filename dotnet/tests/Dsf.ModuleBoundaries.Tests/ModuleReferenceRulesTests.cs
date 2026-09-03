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
    public void Every_production_project_has_reference_policy_coverage()
    {
        var discovered = DiscoverProductionProjectNames().ToList();

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

    public static IEnumerable<object[]> ExpectedProjectNames() => AllowedReferences.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> ProductionProjectNames() =>
        DiscoverProductionProjectNames().Select(k => new object[] { k });

    private static IEnumerable<string> DiscoverProductionProjectNames()
    {
        var root = FindSolutionRoot();
        var src = new DirectoryInfo(Path.Combine(root.FullName, "src"));

        return src.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .Select(csproj => Path.GetFileNameWithoutExtension(csproj.Name))
            .Where(projectName => projectName != TestingProjectName)
            .Order()
            .ToList();
    }
}
