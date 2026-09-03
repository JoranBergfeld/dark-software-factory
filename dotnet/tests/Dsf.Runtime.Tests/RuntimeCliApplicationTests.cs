using Dsf.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Entrypoint-parity tests for the .NET runtime host: <c>run</c>, <c>sweep</c>,
/// <c>serve-orchestrator</c>, and <c>serve-agent --kind</c> must exist, must reuse
/// the existing env var names when composing settings, must name every unset
/// required setting and exit non-zero, and must never claim success for behavior
/// that isn't implemented yet (the station pipeline ships in #142/#143).
/// </summary>
public sealed class RuntimeCliApplicationTests
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyEnvironment =
        new Dictionary<string, string?>();

    private static readonly IReadOnlyDictionary<string, string?> FullEnvironment = new Dictionary<string, string?>
    {
        ["DSF_PRODUCT"] = "acme",
        ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
        ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example",
        ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
        ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-deploy",
        ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embed-deploy",
    };

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        IReadOnlyDictionary<string, string?> env,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await RuntimeCliApplication.InvokeAsync(args, env, stdout, stderr, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Root_grammar_exposes_the_agreed_runtime_verbs()
    {
        var root = RuntimeCliApplication.BuildRootCommand();

        var names = root.Subcommands.Select(c => c.Name).ToList();
        Assert.Contains("run", names);
        Assert.Contains("sweep", names);
        Assert.Contains("serve-orchestrator", names);
        Assert.Contains("serve-agent", names);
    }

    [Fact]
    public async Task Run_without_any_settings_names_the_missing_product_and_exits_non_zero()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(EmptyEnvironment, "run", "--signal", "signal.json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(
            "DSF_PRODUCT is required to scope the factory runtime (set DSF_PRODUCT=<product>).", stderr);
    }

    [Fact]
    public async Task Sweep_with_product_but_missing_endpoints_names_every_unset_endpoint()
    {
        var env = new Dictionary<string, string?> { ["DSF_PRODUCT"] = "acme" };

        var (exitCode, _, stderr) = await InvokeAsync(env, "sweep");

        Assert.Equal(1, exitCode);
        Assert.Contains("AZURE_APPCONFIG_ENDPOINT", stderr);
        Assert.Contains("AZURE_COSMOS_ENDPOINT", stderr);
        Assert.Contains("AZURE_OPENAI_ENDPOINT", stderr);
        Assert.Contains("AZURE_OPENAI_DEPLOYMENT", stderr);
        Assert.Contains("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", stderr);
    }

    [Fact]
    public async Task Run_command_product_option_overrides_the_environment()
    {
        var (exitCode, _, stderr) = await InvokeAsync(
            EmptyEnvironment, "run", "--product", "acme", "--signal", "signal.json");

        // Product is now supplied; the failure must move on to the endpoint
        // requirements instead of repeating the DSF_PRODUCT failure.
        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("DSF_PRODUCT is required", stderr);
        Assert.Contains("AZURE_APPCONFIG_ENDPOINT", stderr);
    }

    [Theory]
    [InlineData("run", "--signal", "signal.json")]
    [InlineData("sweep")]
    [InlineData("serve-orchestrator")]
    public async Task Fully_configured_verbs_fail_loudly_instead_of_pretending_success(params string[] args)
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(FullEnvironment, args);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("not yet implemented", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Serve_agent_does_not_require_azure_runtime_settings()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(EmptyEnvironment, "serve-agent", "--kind", "sentry");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("not yet implemented", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSF_PRODUCT", stderr);
    }
}
