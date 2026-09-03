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
        yield return [Path.Combine(root.FullName, "src", "Dsf.FeatureCouncil")];
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

    // The exact stub text every runtime verb (run/sweep/serve-orchestrator/
    // serve-agent) used to emit unconditionally, regardless of any real
    // per-invocation condition. It must never reappear literally in a runtime
    // verb's source: every verb's failure must instead be conditioned on real
    // work (settings validation, signal parsing, source agent kind lookup).
    private const string BannedUnconditionalStubText = "is not yet implemented in the .NET runtime host";

    public static IEnumerable<object[]> RuntimeVerbSourceDirectories()
    {
        var root = FindSolutionRoot();
        yield return [Path.Combine(root.FullName, "src", "Dsf.Runtime")];
        yield return [Path.Combine(root.FullName, "src", "Dsf.Core", "Runtime")];
        yield return [Path.Combine(root.FullName, "src", "Dsf.FeatureCouncil")];
        yield return [Path.Combine(root.FullName, "src", "Dsf.Cli")];
    }

    [Fact]
    public void Runtime_owner_index_reader_never_shells_out_to_the_az_cli()
    {
        var root = FindSolutionRoot();
        var directory = Path.Combine(root.FullName, "src", "Dsf.Runtime");
        Assert.True(Directory.Exists(directory), $"Expected directory to exist: {directory}");

        // ACA/container managed-identity deployments have no az CLI or `az login`;
        // the production owner runtime index reader must use the managed-identity-
        // capable Azure SDK (DefaultAzureCredential), never shell out to `az`.
        var offenders = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("ProcessStartInfo(\"az\")", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Found az CLI process invocation in the .NET runtime host source: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(RuntimeVerbSourceDirectories))]
    public void Production_runtime_verb_source_never_emits_the_unconditional_stub_text(string directory)
    {
        Assert.True(Directory.Exists(directory), $"Expected directory to exist: {directory}");

        var offenders = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(BannedUnconditionalStubText, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Found the unconditional runtime-verb stub text in production source: " +
            string.Join(", ", offenders));
    }
}
