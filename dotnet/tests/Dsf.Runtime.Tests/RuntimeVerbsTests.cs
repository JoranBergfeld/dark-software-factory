using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Behavioural tests for the runtime verbs at their operator-facing seam: a valid
/// invocation must perform observable real work (drive conveyor stations, record
/// checkpoints, resolve a configured source-agent roster, build a startable host)
/// instead of throwing unconditionally once its inputs validate.
/// </summary>
public sealed class RuntimeVerbsTests
{
    private static readonly RuntimeSettings Settings = new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: "",
        AppInsightsConnectionString: "",
        CosmosEndpoint: "https://cosmos.example",
        OpenAiEndpoint: "https://openai.example",
        OpenAiDeployment: "gpt-deploy",
        OpenAiEmbeddingDeployment: "embed-deploy",
        GitHubAppId: "",
        GitHubInstallationId: "",
        GitHubAppPrivateKeySecret: "",
        GitHubRepository: "acme/acme");

    private static async Task<string> WriteSignalAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsf-signal-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task Run_dry_run_drives_every_station_and_records_checkpoints()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings,
                path,
                dryRun: true,
                TestDependencies.Empty,
                CancellationToken.None);

            Assert.Equal(RunStatus.Previewed, run.Status);
            Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
            Assert.True(run.DryRun);
            Assert.Equal(["sentry"], run.SourceKinds);
            // No source agent gatherers are wired yet (#144): the investigation
            // station must say so out loud rather than silently reporting evidence.
            Assert.Contains(run.Audit, a => a.Station == "s2_investigation" && a.Message.Contains("sentry"));
            Assert.Empty(run.Evidence);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_with_an_unscoped_signal_is_killed_at_triage()
    {
        var path = await WriteSignalAsync("""{}""");
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: true, TestDependencies.Empty, CancellationToken.None);

            Assert.Equal(RunStatus.Killed, run.Status);
            Assert.Equal(["s1_triage"], run.Checkpoints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_without_dry_run_fails_at_the_filing_boundary_only_after_the_line_has_run()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        var dependencies = TestDependencies.Build(evidenceGatherers:
        [
            new ScriptedEvidenceGatherer(
                "sentry",
                new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"),
                new EvidenceItem("sentry", "SENTRY-2", "same trace, second event")),
        ]);
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: false, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Error, run.Status);
            // S1..S6 did their work and checkpointed; only the filing station failed.
            Assert.Equal(ConveyorLine.StationNames.Take(6), run.Checkpoints);
            Assert.Equal(2, run.Evidence.Count);
            Assert.Single(run.Proposals);
            Assert.True(run.Proposals[0].Accepted);
            Assert.Contains("no GitHub issue filer is wired", run.Audit[^1].Message);
            Assert.Contains("#143", run.Audit[^1].Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_without_dry_run_files_accepted_proposals_when_a_filer_is_wired()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        var filer = new RecordingIssueFiler();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            issueFiler: filer);
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: false, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Filed, run.Status);
            Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
            var filed = Assert.Single(filer.Filed);
            Assert.Contains("ready-for-agent", filed.Labels);
            Assert.Single(run.FiledIssues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_without_a_signal_path_reports_the_missing_option()
    {
        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(
            () => RuntimeVerbs.RunAsync(Settings, null, dryRun: true, TestDependencies.Empty, CancellationToken.None));

        Assert.Contains("--signal <path> is required", exception.Message);
    }

    [Fact]
    public async Task Sweep_scopes_the_run_to_the_roster_it_read_for_the_product()
    {
        var roster = new RosterReader(["grafana", "sentry"]);

        var run = await RuntimeVerbs.SweepAsync(
            Settings,
            dryRun: true,
            TestDependencies.Build(sourceAgentRosterReader: roster),
            CancellationToken.None);

        Assert.Equal("acme", roster.RequestedSettings?.Product);
        Assert.Equal(["grafana", "sentry"], run.SourceKinds);
        Assert.Equal(TriggerKind.Scheduled, run.Trigger);
        Assert.Equal(RunStatus.Previewed, run.Status);
        Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
    }

    [Fact]
    public async Task Sweep_reports_an_unreadable_roster_instead_of_sweeping_nothing()
    {
        var dependencies = TestDependencies.Build(
            sourceAgentRosterReader: new UnreachableRosterReader("403 Forbidden"));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(
            () => RuntimeVerbs.SweepAsync(Settings, dryRun: true, dependencies, CancellationToken.None));

        Assert.Contains("403 Forbidden", exception.Message);
    }

    [Fact]
    public async Task Orchestrator_host_serves_health_and_previews_a_posted_signal()
    {
        var app = RuntimeVerbs.BuildOrchestratorHost(Settings, TestDependencies.Empty, "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var health = await client.GetFromJsonAsync<JsonElement>("/healthz");
            Assert.Equal("ok", health.GetProperty("status").GetString());
            Assert.Equal("acme", health.GetProperty("product").GetString());

            var response = await client.PostAsync(
                "/run",
                new StringContent(
                    """{"product_hints": "acme", "source_kinds": ["sentry"]}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("previewed", summary.GetProperty("status").GetString());
            Assert.True(summary.GetProperty("dryRun").GetBoolean());
            Assert.Equal(
                ConveyorLine.StationNames,
                summary.GetProperty("checkpoints").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Orchestrator_host_rejects_an_unparseable_signal_payload()
    {
        var app = RuntimeVerbs.BuildOrchestratorHost(Settings, TestDependencies.Empty, "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var response = await client.PostAsync(
                "/run", new StringContent("not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Agent_host_publishes_its_card_and_refuses_to_gather_until_the_connector_lands()
    {
        var app = RuntimeVerbs.BuildSourceAgentHost(Settings, "SENTRY", "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var card = await client.GetFromJsonAsync<JsonElement>("/.well-known/agent-card.json");
            Assert.Equal("sentry", card.GetProperty("kind").GetString());
            Assert.Equal("dsf-sentry-agent", card.GetProperty("name").GetString());
            Assert.Equal("acme", card.GetProperty("product").GetString());

            var gather = await client.PostAsync("/gather", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotImplemented, gather.StatusCode);
            Assert.Contains("#144", await gather.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void Agent_host_rejects_an_unknown_kind_by_name()
    {
        var exception = Assert.Throws<RuntimeVerbException>(
            () => RuntimeVerbs.BuildSourceAgentHost(Settings, "bogus"));

        Assert.Contains("unknown source agent kind 'bogus'", exception.Message);
        Assert.Contains("sentry", exception.Message);
    }

    [Theory]
    [InlineData("sentry")]
    [InlineData("grafana")]
    [InlineData("foundryiq")]
    [InlineData("webiq")]
    [InlineData("incidents")]
    [InlineData("azuremonitor")]
    public void Agent_host_builds_for_every_known_source_kind(string kind)
    {
        var app = RuntimeVerbs.BuildSourceAgentHost(Settings, kind, "127.0.0.1", 0);

        Assert.NotNull(app);
    }

    [Fact]
    public void Sweep_interval_prefers_the_explicit_option_then_the_env_var_then_the_default()
    {
        var env = new Dictionary<string, string?> { ["DSF_SWEEP_INTERVAL"] = "42" };

        Assert.Equal(TimeSpan.FromSeconds(7), PeriodicSweepService.ResolveInterval(7, env));
        Assert.Equal(TimeSpan.FromSeconds(42), PeriodicSweepService.ResolveInterval(null, env));
        Assert.Equal(
            TimeSpan.FromSeconds(PeriodicSweepService.DefaultIntervalSeconds),
            PeriodicSweepService.ResolveInterval(null, new Dictionary<string, string?>()));
        Assert.Equal(TimeSpan.FromSeconds(1), PeriodicSweepService.ResolveInterval(0, env));
    }

    private static string BaseAddress(WebApplication app) =>
        app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);
}
