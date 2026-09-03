using Dsf.Core.Runtime;
using Dsf.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Entrypoint-parity tests for the .NET runtime host: <c>run</c>, <c>sweep</c>,
/// <c>serve-orchestrator</c>, and <c>serve-agent --kind</c> must exist, must reuse
/// the existing env var names when composing settings, must name every unset
/// required setting and exit non-zero, and must never claim success for behavior
/// that isn't implemented yet (the station pipeline ships in #142/#143). Every verb
/// -- including <c>serve-agent</c> -- must validate required runtime config the
/// same way before its loud not-implemented failure, and every verb must be able to
/// resolve settings it doesn't have locally from the owner App Configuration
/// runtime index when <c>DSF_OWNER_APPCONFIG_ENDPOINT</c> is configured.
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        IReadOnlyDictionary<string, string?> env,
        IOwnerRuntimeIndexReader ownerRuntimeIndexReader,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await RuntimeCliApplication.InvokeAsync(
            args, env, stdout, stderr, ownerRuntimeIndexReader, CancellationToken.None);
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
    [InlineData("sweep")]
    [InlineData("serve-orchestrator")]
    public async Task Fully_configured_sweep_and_orchestrator_report_pending_source_agent_runners(params string[] args)
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(FullEnvironment, args);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.DoesNotContain("not yet implemented", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no source agent runners are wired", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#144", stderr);
    }

    [Fact]
    public async Task Run_with_full_settings_and_a_missing_signal_file_reports_a_real_error()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(FullEnvironment, "run", "--signal", "does-not-exist.json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.DoesNotContain("not yet implemented", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signal file not found: does-not-exist.json", stderr);
    }

    [Fact]
    public async Task Run_with_full_settings_and_invalid_json_reports_a_real_error()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "not json");

            var (exitCode, stdout, stderr) = await InvokeAsync(FullEnvironment, "run", "--signal", path);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("is not valid JSON", stderr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_with_full_settings_and_a_valid_signal_parses_it_and_reports_the_pending_conveyor()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path, """{"product_hints": "acme", "source_kinds": ["sentry", "bogus"]}""");

            var (exitCode, stdout, stderr) = await InvokeAsync(FullEnvironment, "run", "--signal", path);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.DoesNotContain("not yet implemented", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("product_hints=[acme]", stderr);
            // "bogus" is not a recognized source kind and must be dropped, mirroring
            // the Python signal_to_run's unknown-kind handling.
            Assert.Contains("source_kinds=[sentry]", stderr);
            Assert.Contains("conveyor station pipeline is not wired yet", stderr);
            Assert.Contains("#142", stderr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Serve_agent_validates_runtime_settings_before_validating_kind()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(EmptyEnvironment, "serve-agent", "--kind", "sentry");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(
            "DSF_PRODUCT is required to scope the factory runtime (set DSF_PRODUCT=<product>).", stderr);
        Assert.DoesNotContain("not yet implemented", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent host", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Serve_agent_with_full_settings_and_an_unknown_kind_reports_a_real_error()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(FullEnvironment, "serve-agent", "--kind", "bogus");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unknown source agent kind 'bogus'", stderr);
        Assert.Contains("sentry", stderr);
    }

    [Fact]
    public async Task Serve_agent_with_full_settings_and_a_known_kind_reports_the_pending_agent_host()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(FullEnvironment, "serve-agent", "--kind", "sentry");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.DoesNotContain("not yet implemented", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source agent kind 'sentry' is recognized", stderr);
        Assert.Contains("#144", stderr);
    }

    [Theory]
    [InlineData("sweep")]
    [InlineData("serve-orchestrator")]
    public async Task Sweep_and_orchestrator_resolve_missing_endpoints_from_the_owner_runtime_index_when_configured(params string[] args)
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "acme",
            ["DSF_OWNER_APPCONFIG_ENDPOINT"] = "https://owner-appconfig.example",
        };
        var reader = new StubOwnerRuntimeIndexReader(new Dictionary<string, string>
        {
            ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
            ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example",
            ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-deploy",
            ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embed-deploy",
        });

        var (exitCode, stdout, stderr) = await InvokeAsync(env, reader, args);

        // Settings resolved from the owner index; the failure moves past config
        // validation entirely to the (expected) pending source-agent runners.
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("#144", stderr);
        Assert.DoesNotContain("AZURE_APPCONFIG_ENDPOINT", stderr);
        Assert.Equal("acme", reader.RequestedProduct);
    }

    [Fact]
    public async Task Run_fails_loudly_when_the_owner_runtime_index_lookup_fails()
    {
        var env = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "acme",
            ["DSF_OWNER_APPCONFIG_ENDPOINT"] = "https://owner-appconfig.example",
        };
        var reader = new ThrowingOwnerRuntimeIndexReader();

        var (exitCode, stdout, stderr) = await InvokeAsync(env, reader, "run", "--signal", "signal.json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("acme", stderr);
        Assert.Contains("https://owner-appconfig.example", stderr);
    }

    private sealed class StubOwnerRuntimeIndexReader(IReadOnlyDictionary<string, string> values) : IOwnerRuntimeIndexReader
    {
        public string? RequestedProduct { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> ReadAsync(
            string ownerAppConfigEndpoint,
            string product,
            CancellationToken cancellationToken)
        {
            RequestedProduct = product;
            return Task.FromResult(values);
        }
    }

    private sealed class ThrowingOwnerRuntimeIndexReader : IOwnerRuntimeIndexReader
    {
        public Task<IReadOnlyDictionary<string, string>> ReadAsync(
            string ownerAppConfigEndpoint,
            string product,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"product '{product}' is absent from the owner runtime index at '{ownerAppConfigEndpoint}'.");
    }
}
