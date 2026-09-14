using Dsf.Core.Runtime;
using System.Runtime.CompilerServices;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Entrypoint tests for the operator sweep control subcommands: <c>dsf sweep
/// pause</c>/<c>resume</c>/<c>interval &lt;n&gt;</c>/<c>status</c> read and write
/// the <see cref="ISweepControlStore"/> the in-process sweep loop consults on
/// every tick, using the same settings-composition gate every other verb applies.
/// </summary>
public sealed class SweepControlCliTests
{
    private static readonly IReadOnlyDictionary<string, string?> FullEnvironment = new Dictionary<string, string?>
    {
        ["DSF_PRODUCT"] = "acme",
        ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
        ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example",
        ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
        ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-deploy",
        ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embed-deploy",
        [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true",
    };

    private static async Task<(int ExitCode, string Stdout, string Stderr, ScriptedSweepControlStore Store)> InvokeAsync(
        params string[] args)
    {
        var store = new ScriptedSweepControlStore();
        var dependencies = TestDependencies.Build(sweepControlStoreFactory: _ => store);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await RuntimeCliApplication.InvokeAsync(
            args, FullEnvironment, stdout, stderr, dependencies, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString(), store);
    }

    [Fact]
    public void Sweep_grammar_exposes_the_control_subcommands()
    {
        var root = RuntimeCliApplication.BuildRootCommand();
        var sweep = root.Subcommands.Single(c => c.Name == "sweep");

        var names = sweep.Subcommands.Select(c => c.Name).ToList();
        Assert.Contains("pause", names);
        Assert.Contains("resume", names);
        Assert.Contains("interval", names);
        Assert.Contains("status", names);
    }

    [Fact]
    public async Task Pause_sets_the_paused_flag_the_loop_reads_on_its_next_tick()
    {
        var (exitCode, stdout, _, store) = await InvokeAsync("sweep", "pause");

        Assert.Equal(0, exitCode);
        Assert.True(store.State.Paused);
        Assert.Contains("paused", stdout);
    }

    [Fact]
    public async Task Resume_clears_the_paused_flag()
    {
        var store = new ScriptedSweepControlStore(new SweepControlState(Paused: true, IntervalSeconds: 0));
        var dependencies = TestDependencies.Build(sweepControlStoreFactory: _ => store);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await RuntimeCliApplication.InvokeAsync(
            ["sweep", "resume"], FullEnvironment, stdout, stderr, dependencies, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(store.State.Paused);
        Assert.Contains("resumed", stdout.ToString());
    }

    [Fact]
    public async Task Interval_changes_the_effective_tick_cadence_the_loop_reads()
    {
        var (exitCode, stdout, _, store) = await InvokeAsync("sweep", "interval", "90");

        Assert.Equal(0, exitCode);
        Assert.Equal(90, store.State.IntervalSeconds);
        Assert.Contains("90", stdout);
    }

    [Fact]
    public async Task Interval_rejects_a_non_positive_value()
    {
        var (exitCode, _, stderr, _) = await InvokeAsync("sweep", "interval", "0");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("at least 1 second", stderr);
    }

    [Fact]
    public async Task Status_reports_the_current_paused_and_interval_state()
    {
        var store = new ScriptedSweepControlStore(new SweepControlState(Paused: true, IntervalSeconds: 120));
        var dependencies = TestDependencies.Build(sweepControlStoreFactory: _ => store);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await RuntimeCliApplication.InvokeAsync(
            ["sweep", "status"], FullEnvironment, stdout, stderr, dependencies, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("paused", stdout.ToString());
        Assert.Contains("120", stdout.ToString());
    }

    [Fact]
    public async Task Status_reports_the_default_interval_when_none_has_been_set()
    {
        var (exitCode, stdout, _, _) = await InvokeAsync("sweep", "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("running", stdout);
        Assert.Contains($"{PeriodicSweepService.DefaultIntervalSeconds}s (default)", stdout);
    }

    [Fact]
    public async Task Pause_reports_an_unreachable_store_and_exits_non_zero()
    {
        var dependencies = TestDependencies.Build(
            sweepControlStoreFactory: _ => new UnreachableSweepControlStore("403 Forbidden"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await RuntimeCliApplication.InvokeAsync(
            ["sweep", "pause"], FullEnvironment, stdout, stderr, dependencies, CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("403 Forbidden", stderr.ToString());
    }

    [Fact]
    public async Task All_controls_round_trip_through_the_real_App_Configuration_store()
    {
        var gateway = new SweepConfigurationGateway();
        var dependencies = TestDependencies.Build(
            sweepControlStoreFactory: settings => new AzureAppConfigurationSweepControlStore(gateway, settings));

        async Task<string> Invoke(params string[] args)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await RuntimeCliApplication.InvokeAsync(
                args, FullEnvironment, stdout, stderr, dependencies, CancellationToken.None);
            Assert.Equal("", stderr.ToString());
            Assert.Equal(0, exitCode);
            return stdout.ToString();
        }

        await Invoke("sweep", "pause");
        Assert.Equal("true", gateway.Values[ProductConfigurationKeys.SweepPaused]);
        Assert.Contains("paused", await Invoke("sweep", "status"));

        await Invoke("sweep", "interval", "42");
        Assert.Equal("42", gateway.Values[ProductConfigurationKeys.SweepIntervalSeconds]);
        var pausedStatus = await Invoke("sweep", "status");
        Assert.Contains("paused", pausedStatus);
        Assert.Contains("interval=42s", pausedStatus);

        await Invoke("sweep", "resume");
        Assert.Equal("false", gateway.Values[ProductConfigurationKeys.SweepPaused]);
        var resumedStatus = await Invoke("sweep", "status");
        Assert.Contains("running", resumedStatus);
        Assert.Contains("interval=42s", resumedStatus);
        Assert.All(gateway.Endpoints, endpoint => Assert.Equal("https://appconfig.example", endpoint));
        Assert.All(gateway.ReadLabels, label => Assert.Equal(ProductConfigurationKeys.NoLabel, label));
        Assert.All(gateway.WriteLabels, Assert.Null);
    }

    [Fact]
    public async Task Status_reports_invalid_stored_controls_as_an_operator_error()
    {
        var gateway = new SweepConfigurationGateway();
        gateway.Values[ProductConfigurationKeys.SweepPaused] = "broken";
        var dependencies = TestDependencies.Build(
            sweepControlStoreFactory: settings => new AzureAppConfigurationSweepControlStore(gateway, settings));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await RuntimeCliApplication.InvokeAsync(
            ["sweep", "status"], FullEnvironment, stdout, stderr, dependencies, CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Equal("", stdout.ToString());
        Assert.Contains(ProductConfigurationKeys.SweepPaused, stderr.ToString());
    }

    private sealed class SweepConfigurationGateway : IConfigurationSettingsGateway
    {
        public Dictionary<string, string> Values { get; } = [];
        public List<string> Endpoints { get; } = [];
        public List<string> ReadLabels { get; } = [];
        public List<string?> WriteLabels { get; } = [];

        public async IAsyncEnumerable<(string Key, string Value)> ListAsync(
            string endpoint, string label, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Endpoints.Add(endpoint);
            ReadLabels.Add(label);
            foreach (var (key, value) in Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return (key, value);
            }

            await Task.CompletedTask;
        }

        public Task SetAsync(string endpoint, string key, string value, string? label, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Endpoints.Add(endpoint);
            WriteLabels.Add(label);
            Values[key] = value;
            return Task.CompletedTask;
        }
    }
}
