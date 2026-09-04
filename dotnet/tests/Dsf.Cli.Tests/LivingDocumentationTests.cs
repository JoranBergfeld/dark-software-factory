using System.Text.RegularExpressions;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class LivingDocumentationTests
{
    private static readonly string[] CurrentDocumentationFiles =
    [
        "README.md",
        "AGENTS.md",
        "CLAUDE.md",
        "infra/README.md",
        "dotnet/README.md",
        "docs/agents/domain.md",
        "docs/agents/issue-tracker.md",
        "docs/agents/triage-labels.md",
        "docs/site/index.md",
        "docs/site/get-started/bootstrap.md",
        "docs/site/get-started/operate.md",
        "docs/site/get-started/provision-a-factory.md",
        "docs/site/get-started/quickstart.md",
        "docs/site/get-started/verify-release.md",
        "docs/site/concept/creation.md",
        "docs/site/concept/feature-council.md",
        "docs/site/concept/product-charter.md",
        "docs/site/concept/sre-agent.md",
        "docs/site/concept/the-harness.md",
        "docs/site/concept/the-loop.md",
    ];

    private static readonly (string Label, Regex Pattern)[] PythonEraTerms =
    [
        ("uv", new Regex(@"(?<![A-Za-z0-9])uv(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("python", new Regex(@"(?<![A-Za-z0-9])python(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("pytest", new Regex(@"(?<![A-Za-z0-9])pytest(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("pip", new Regex(@"(?<![A-Za-z0-9])pip(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("ruff", new Regex(@"(?<![A-Za-z0-9])ruff(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("pyproject", new Regex(@"(?<![A-Za-z0-9])pyproject(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("PEP 420", new Regex(@"PEP 420", RegexOptions.IgnoreCase)),
        (".py", new Regex(@"\.py(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
    ];

    [Fact]
    public void Current_living_documentation_describes_dotnet_workflows_without_python_era_terms()
    {
        var offenders = new List<string>();

        foreach (var relativePath in CurrentDocumentationFiles)
        {
            var content = ReadRepoFile(relativePath).Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach (var (label, pattern) in PythonEraTerms)
            {
                if (pattern.IsMatch(content))
                {
                    offenders.Add($"{relativePath}: {label}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Python-era terms remain in current docs:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Release_verification_guide_covers_public_artifact_verification()
    {
        var content = ReadRepoFile("docs/site/get-started/verify-release.md");

        Assert.Contains("SHA256SUMS", content, StringComparison.Ordinal);
        Assert.Contains("Ed25519", content, StringComparison.Ordinal);
        Assert.Contains("signature", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SPDX", content, StringComparison.Ordinal);
        Assert.Contains("SBOM", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release-verification-key.pem", content, StringComparison.Ordinal);
        Assert.Contains("provenance", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native package", content, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepoFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(FindRepoRoot().FullName, relativePath));
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
}
