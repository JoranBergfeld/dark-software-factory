using System.Text.RegularExpressions;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class LivingDocumentationTests
{
    // Living documentation is discovered, not listed, so a new operator/contributor doc is
    // covered the moment it is added. Historical records (ADRs, superpowers plans/specs) and
    // the frozen Python parity reference are deliberately excluded: they describe the past.
    private static readonly string[] CurrentDocumentationRoots =
    [
        "docs/site",
        "docs/agents",
    ];

    private static readonly string[] CurrentDocumentationExtraFiles =
    [
        "README.md",
        "AGENTS.md",
        "CLAUDE.md",
        "infra/README.md",
        "dotnet/README.md",
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
        var documents = CurrentDocumentationFiles();

        // Coverage guard: the scan must actually reach the operator guides, so a moved or
        // renamed docs root fails loudly instead of silently checking nothing.
        Assert.Contains("docs/site/get-started/quickstart.md", documents);
        Assert.Contains("docs/site/get-started/operate.md", documents);
        Assert.Contains("docs/site/get-started/verify-release.md", documents);
        Assert.True(documents.Count >= 15, $"Living documentation scan found only {documents.Count} files.");

        var offenders = new List<string>();

        foreach (var relativePath in documents)
        {
            var content = ReadRepoFile(relativePath);
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

    // These packages are the pre-cutover Python implementation (evals gate + source
    // agents), retained solely as the #149 parity-reference baseline. They are not
    // current/living docs, so they still describe Python/uv/pytest/fixtures internals --
    // but they must not read as operator or contributor instructions telling readers to
    // build, run, or test the product with that Python toolchain.
    private static readonly string[] FrozenPythonParityReadmeFiles =
    [
        "feature-council/src/dsf/evals/README.md",
        "feature-council/src/dsf/agents/sentry/README.md",
        "feature-council/src/dsf/agents/grafana/README.md",
        "feature-council/src/dsf/agents/foundryiq/README.md",
        "feature-council/src/dsf/agents/webiq/README.md",
    ];

    private static readonly (string Label, Regex Pattern)[] OperatorInstructionTerms =
    [
        ("python -m", new Regex(@"python\s+-m", RegexOptions.IgnoreCase)),
        ("uv run", new Regex(@"uv\s+run", RegexOptions.IgnoreCase)),
        ("pytest invocation", new Regex(@"(?<![A-Za-z0-9])pytest\s", RegexOptions.IgnoreCase)),
        ("dsf serve-agent", new Regex(@"dsf\s+serve-agent", RegexOptions.IgnoreCase)),
        ("DSF_MODE", new Regex(@"DSF_MODE", RegexOptions.None)),
        ("tests/fixtures", new Regex(@"tests/fixtures", RegexOptions.None)),
    ];

    [Fact]
    public void Frozen_python_parity_reference_docs_disclose_frozen_status_and_carry_no_operator_instructions()
    {
        var offenders = new List<string>();

        foreach (var relativePath in FrozenPythonParityReadmeFiles)
        {
            var content = ReadRepoFile(relativePath);

            if (!content.Contains("frozen", StringComparison.OrdinalIgnoreCase)
                || !content.Contains("parity reference", StringComparison.OrdinalIgnoreCase)
                || !content.Contains("#149", StringComparison.Ordinal))
            {
                offenders.Add($"{relativePath}: missing frozen/parity-reference/#149 disclaimer");
            }

            foreach (var (label, pattern) in OperatorInstructionTerms)
            {
                if (pattern.IsMatch(content))
                {
                    offenders.Add($"{relativePath}: {label}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Frozen Python parity-reference docs must disclose frozen status and contain no runnable operator/contributor instructions:\n"
                + string.Join("\n", offenders));
    }

    [Fact]
    public void Operate_doc_does_not_claim_an_unshipped_observability_bundle_artifact()
    {
        var content = ReadRepoFile("docs/site/get-started/operate.md");

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
    public void Readme_command_examples_match_the_documented_cli_surface()
    {
        var readme = ReadRepoFile("README.md");

        // `dsf bootstrap` is not implemented (see bootstrap.md); the README must say so
        // wherever it shows the verb.
        if (readme.Contains("dsf bootstrap", StringComparison.Ordinal))
        {
            Assert.Contains("not implemented", readme, StringComparison.OrdinalIgnoreCase);
        }

        // Runtime verbs are forwarded to `dsf-runtime`; the README must not present them as
        // plain packaged-CLI usage without naming that dependency.
        var runtimeVerbs = new[] { "dsf run ", "dsf sweep ", "dsf serve-orchestrator", "dsf serve-agent" };
        if (runtimeVerbs.Any(verb => readme.Contains(verb, StringComparison.Ordinal)))
        {
            Assert.Contains("dsf-runtime", readme, StringComparison.Ordinal);
            Assert.Contains("DSF_RUNTIME_HOST", readme, StringComparison.Ordinal);
        }
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

        // The quickstart is packaged-install only. It may name the .NET SDK, because
        // `dotnet tool install` needs it, but it must not teach the contributor
        // build/test/checkout workflow.
        Assert.Contains(".NET SDK", quickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("## Verify a checkout", quickstart, StringComparison.Ordinal);
        foreach (var contributorCommand in new[] { "dotnet restore", "dotnet build", "dotnet test", "Dsf.sln" })
        {
            Assert.DoesNotContain(contributorCommand, quickstart, StringComparison.Ordinal);
        }

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

    [Fact]
    public void Release_verification_guide_covers_code_signing_checks_the_release_workflow_applies()
    {
        var content = ReadRepoFile("docs/site/get-started/verify-release.md");

        // The release workflow signs the NuGet package, Authenticode-signs Windows
        // executables, and Developer ID signs + notarizes macOS executables. Each signature
        // needs a documented consumer-side check.
        Assert.Contains("dotnet nuget verify", content, StringComparison.Ordinal);
        Assert.Contains("Authenticode", content, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", content, StringComparison.Ordinal);
        Assert.Contains("Developer ID", content, StringComparison.Ordinal);
        Assert.Contains("codesign --verify", content, StringComparison.Ordinal);
        Assert.Contains("spctl", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_verification_guide_reads_as_per_release_procedure_not_a_current_availability_claim()
    {
        var content = ReadRepoFile("docs/site/get-started/verify-release.md");

        Assert.Contains("each published release", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The GitHub release publishes", content, StringComparison.Ordinal);

        // No concrete version or tag may be asserted as currently downloadable.
        Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z0-9.])v?\d+\.\d+\.\d+(?![A-Za-z0-9.])"), content);
    }

    private static List<string> CurrentDocumentationFiles()
    {
        var root = FindRepoRoot().FullName;
        var documents = new List<string>(CurrentDocumentationExtraFiles);

        foreach (var docRoot in CurrentDocumentationRoots)
        {
            var absolute = Path.Combine(root, docRoot.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(absolute), $"Documentation root '{docRoot}' does not exist.");

            foreach (var file in Directory.EnumerateFiles(absolute, "*.md", SearchOption.AllDirectories))
            {
                documents.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        documents.Sort(StringComparer.Ordinal);
        return documents;
    }

    private static string ReadRepoFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(FindRepoRoot().FullName, relativePath))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
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
