using System.Text.RegularExpressions;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Enforces ADR 0014 (real-only <c>src/</c>) for the .NET runtime host: no fake,
/// in-memory, deterministic, no-op, or fixture fallback adapter types may live in
/// production runtime source. Deterministic test doubles belong in test projects
/// only (this file's own directory, or <c>Dsf.Testing</c>).
/// </summary>
public sealed class RealOnlyRuntimeSourceTests
{
    // Matches a type declaration whose name starts with a banned prefix, e.g.
    // "internal sealed class FakeModelClient" or "public record NoOpTracer".
    private static readonly Regex BannedTypeDeclaration = new(
        @"\b(class|record|struct|interface)\s+(Fake|InMemory|NoOp|Stub|Mock|Dummy)\w*",
        RegexOptions.Compiled);

    private static DirectoryInfo FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dsf.sln")))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException("Could not locate Dsf.sln above the test output directory.");
    }

    public static IEnumerable<object[]> RuntimeSourceDirectories()
    {
        var root = FindSolutionRoot();
        yield return [Path.Combine(root.FullName, "src", "Dsf.Runtime")];
        yield return [Path.Combine(root.FullName, "src", "Dsf.Core", "Runtime")];
    }

    [Theory]
    [MemberData(nameof(RuntimeSourceDirectories))]
    public void Production_runtime_source_declares_no_fake_or_no_op_adapter_types(string directory)
    {
        Assert.True(Directory.Exists(directory), $"Expected directory to exist: {directory}");

        var offenders = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => BannedTypeDeclaration.IsMatch(File.ReadAllText(path)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Found fake/no-op adapter type declaration(s) in production runtime source: " +
            string.Join(", ", offenders));
    }
}
