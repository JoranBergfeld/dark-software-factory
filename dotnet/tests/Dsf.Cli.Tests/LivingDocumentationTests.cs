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
    public void Operate_doc_does_not_claim_an_unshipped_observability_bundle_artifact()
    {
        var content = ReadRepoFile("docs/site/get-started/operate.md");
        var metadataScript = ReadRepoFile("dotnet/eng/generate-release-metadata.py");

        // The release metadata generator is the single source of truth for what a release
        // bundle contains; it never creates an `observability/` directory or dashboard JSON,
        // so the operator doc must not claim one ships there.
        Assert.DoesNotContain("observability", metadataScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("release artifact bundle", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observability/", content, StringComparison.Ordinal);

        // The doc must instead describe the real telemetry the runtime emits.
        Assert.Contains("ApplicationInsightsTracer", content, StringComparison.Ordinal);
        Assert.Contains("run.start", content, StringComparison.Ordinal);
        Assert.Contains("station.complete", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_docs_do_not_claim_unimplemented_owner_provisioning()
    {
        var bootstrap = ReadRepoFile("docs/site/get-started/bootstrap.md");
        var quickstart = ReadRepoFile("docs/site/get-started/quickstart.md");
        var provisioning = ReadRepoFile("docs/site/get-started/provision-a-factory.md");

        Assert.Contains("not implemented", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not implemented", quickstart, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creates the DSF GitHub App", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("dsf bootstrap creates that once", quickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("created by [`dsf bootstrap`", provisioning, StringComparison.Ordinal);
    }

    [Fact]
    public void Operator_docs_describe_the_shipped_cli_and_its_external_dependencies()
    {
        var agents = ReadRepoFile("AGENTS.md");
        var bootstrap = ReadRepoFile("docs/site/get-started/bootstrap.md");
        var quickstart = ReadRepoFile("docs/site/get-started/quickstart.md");
        var operate = ReadRepoFile("docs/site/get-started/operate.md");
        var provisioning = ReadRepoFile("docs/site/get-started/provision-a-factory.md");
        var releaseVerification = ReadRepoFile("docs/site/get-started/verify-release.md");

        Assert.DoesNotContain("dsf bootstrap — create owner", agents, StringComparison.Ordinal);

        Assert.DoesNotContain(".NET SDK", quickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("## Verify a checkout", quickstart, StringComparison.Ordinal);

        Assert.Contains("does not retrieve", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seed owner-vault credentials", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not retrieve", quickstart, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seed credentials", quickstart, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GH_TOKEN", provisioning, StringComparison.Ordinal);
        Assert.Contains("does not retrieve credentials", provisioning, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Install `dsf-runtime` beside `dsf`", operate, StringComparison.Ordinal);
        Assert.Contains("source or service deployment", operate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DSF_RUNTIME_HOST", operate, StringComparison.Ordinal);

        var operatorDocs = new[]
        {
            ReadRepoFile("README.md"),
            bootstrap,
            quickstart,
            operate,
            provisioning,
            releaseVerification,
        };
        foreach (var document in operatorDocs)
        {
            Assert.DoesNotContain("tests/fixtures", document, StringComparison.Ordinal);
        }

        Assert.Contains("dsf-cli-linux-x64-tar-gz.spdx.json", releaseVerification, StringComparison.Ordinal);
        Assert.DoesNotContain("dsf-cli-linux-x64.tar.gz.spdx.json", releaseVerification, StringComparison.Ordinal);
    }

    [Fact]
    public void Operate_doc_describes_runtime_host_distribution_and_manual_charter_control()
    {
        var content = ReadRepoFile("docs/site/get-started/operate.md");

        Assert.Contains("dsf-runtime", content, StringComparison.Ordinal);
        Assert.Contains("DSF_RUNTIME_HOST", content, StringComparison.Ordinal);
        Assert.Contains("not synchronized by runtime sweeps", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runtime syncs it on every sweep", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sweeps may propose charter amendments", content, StringComparison.OrdinalIgnoreCase);
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
