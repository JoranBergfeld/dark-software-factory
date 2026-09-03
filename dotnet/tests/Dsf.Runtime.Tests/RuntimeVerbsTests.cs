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
        var dependencies = TestDependencies.Build(evidenceGatherers:
        [
            new ScriptedEvidenceGatherer("sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked")),
        ]);
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings,
                path,
                dryRun: true,
                dependencies,
                CancellationToken.None);

            Assert.Equal(RunStatus.Previewed, run.Status);
            Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
            Assert.True(run.DryRun);
            Assert.Equal(["sentry"], run.SourceKinds);
            Assert.Contains(run.Audit, a => a.Station == "s2_investigation" && a.Message.Contains("sentry"));
            Assert.Single(run.Evidence);
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
            Assert.Contains("GITHUB_APP_ID", run.Audit[^1].Message);
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
            TestDependencies.Build(
                sourceAgentRosterReader: roster,
                evidenceGatherers:
                [
                    new ScriptedEvidenceGatherer("grafana"),
                    new ScriptedEvidenceGatherer("sentry"),
                ]),
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
        var dependencies = TestDependencies.Build(evidenceGatherers:
        [
            new ScriptedEvidenceGatherer("sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked")),
        ]);
        var app = RuntimeVerbs.BuildOrchestratorHost(Settings, dependencies, "127.0.0.1", 0);
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
    public async Task Orchestrator_host_reports_a_station_error_as_a_non_2xx_response()
    {
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            modelClient: new ThrowingModelClient("model deployment unreachable"));
        var app = RuntimeVerbs.BuildOrchestratorHost(Settings, dependencies, "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var response = await client.PostAsync(
                "/run",
                new StringContent(
                    """{"product_hints": "acme", "source_kinds": ["sentry"]}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("error", summary.GetProperty("status").GetString());
            Assert.Contains("model deployment unreachable", summary.GetProperty("failureReason").GetString());
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
    public async Task Agent_host_publishes_its_card_and_gathers_from_its_integration()
    {
        var dependencies = TestDependencies.Build(sourceIntegration: new ScriptedSourceIntegration(
            new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked")));
        var app = RuntimeVerbs.BuildSourceAgentHost(Settings, "SENTRY", dependencies, "127.0.0.1", 0);
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
            Assert.Equal(HttpStatusCode.OK, gather.StatusCode);
            var payload = await gather.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "SENTRY-1",
                payload.GetProperty("evidence").EnumerateArray().Single().GetProperty("reference").GetString());
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
            () => RuntimeVerbs.BuildSourceAgentHost(Settings, "bogus", TestDependencies.Empty));

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
        var app = RuntimeVerbs.BuildSourceAgentHost(Settings, kind, TestDependencies.Empty, "127.0.0.1", 0);

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

    [Fact]
    public async Task Run_calls_the_model_client_during_synthesis_and_council_for_every_proposal()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        var model = new RecordingModelClient();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            modelClient: model);
        try
        {
            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Previewed, run.Status);
            // One synthesis completion and one council completion per proposal.
            Assert.Equal(2, model.Prompts.Count);
            Assert.Contains(model.Prompts, prompt => prompt.Contains("sentry", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_fails_with_an_audited_error_when_the_model_client_call_fails()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            modelClient: new ThrowingModelClient("model deployment unreachable"));
        try
        {
            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Error, run.Status);
            Assert.Contains(run.Audit, a => a.Message.Contains("model deployment unreachable"));
            // Synthesis (where the model is first called) never checkpointed.
            Assert.DoesNotContain("s3_synthesis", run.Checkpoints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_traces_every_station_boundary_plus_the_run_start_and_completion()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        var tracer = new RecordingTracer();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            tracer: tracer);
        try
        {
            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Previewed, run.Status);
            Assert.Contains(tracer.Traced, e => e.Name == "run.start");
            Assert.Contains(tracer.Traced, e => e.Name == "run.complete");
            foreach (var station in ConveyorLine.StationNames)
            {
                Assert.Contains(tracer.Traced, e => e.Name == "station.start" && e.Properties["station"] == station);
                Assert.Contains(tracer.Traced, e => e.Name == "station.complete" && e.Properties["station"] == station);
            }

            // This is a dry run: every traced event must carry the mode so an
            // external tracer (Application Insights) can gate its own emission.
            Assert.All(tracer.Traced, e => Assert.Equal("True", e.Properties["dryRun"]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_survives_an_unreachable_tracer_and_still_completes_the_line()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            tracer: new UnreachableTracer("telemetry ingestion refused the event"));
        try
        {
            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Previewed, run.Status);
            Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
            Assert.Contains(run.Audit, a => a.Message.Contains("telemetry ingestion refused the event"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string BaseAddress(WebApplication app) =>
        app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);
}

/// <summary>
/// Behavioural tests for <c>poll-outcomes</c>: it must poll and record human
/// outcomes idempotently, must require exactly one of <c>--dry-run</c>/<c>--live</c>,
/// and a live (recording) poll must refuse to run without an explicit manual
/// confirmation gate.
/// </summary>
public sealed class PollOutcomesVerbTests
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

    private static readonly OutcomeSignal Signal =
        new("fingerprint-1:sentry", OutcomeLabels.Approved, "https://github.com/acme/acme/issues/9", "[sentry] checkout 500s spiked");

    private static readonly Dictionary<string, string?> NoConfirmEnv = [];

    private static readonly Dictionary<string, string?> ConfirmedEnv = new()
    {
        [RuntimeIntegrationSettings.ConfirmLiveOutcomes] = "true",
    };

    [Fact]
    public async Task Neither_dry_run_nor_live_is_refused_loudly()
    {
        var dependencies = TestDependencies.Build();

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: false, live: false, NoConfirmEnv, dependencies, CancellationToken.None));

        Assert.Contains("--dry-run", exception.Message);
        Assert.Contains("--live", exception.Message);
    }

    [Fact]
    public async Task Both_dry_run_and_live_is_refused_loudly()
    {
        var dependencies = TestDependencies.Build();

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: true, live: true, ConfirmedEnv, dependencies, CancellationToken.None));

        Assert.Contains("mutually exclusive", exception.Message);
    }

    [Fact]
    public async Task Dry_run_polls_and_previews_without_recording_anything()
    {
        var outcomeSource = new RecordingOutcomeSource(Signal);
        var learningStore = new RecordingLearningStore();
        var dependencies = TestDependencies.Build(
            learningComposer: new ScriptedLearningComposer(outcomeSource, learningStore));

        var result = await RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: true, live: false, NoConfirmEnv, dependencies, CancellationToken.None);

        Assert.Equal(1, outcomeSource.PollCount);
        Assert.Empty(learningStore.Recorded);
        Assert.True(result.DryRun);
        Assert.Equal(1, result.Polled);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal("fingerprint-1:sentry", outcome.IntentKey);
        Assert.False(outcome.Recorded);
    }

    [Fact]
    public async Task Live_without_the_manual_confirmation_gate_is_refused_and_records_nothing()
    {
        var outcomeSource = new RecordingOutcomeSource(Signal);
        var learningStore = new RecordingLearningStore();
        var dependencies = TestDependencies.Build(
            learningComposer: new ScriptedLearningComposer(outcomeSource, learningStore));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: false, live: true, NoConfirmEnv, dependencies, CancellationToken.None));

        Assert.Contains(RuntimeIntegrationSettings.ConfirmLiveOutcomes, exception.Message);
        Assert.Empty(learningStore.Recorded);
    }

    [Fact]
    public async Task Live_with_the_manual_confirmation_gate_records_every_polled_outcome()
    {
        var outcomeSource = new RecordingOutcomeSource(Signal);
        var learningStore = new RecordingLearningStore();
        var dependencies = TestDependencies.Build(
            learningComposer: new ScriptedLearningComposer(outcomeSource, learningStore));

        var result = await RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: false, live: true, ConfirmedEnv, dependencies, CancellationToken.None);

        Assert.False(result.DryRun);
        var recorded = Assert.Single(learningStore.Recorded);
        Assert.Equal("fingerprint-1:sentry", recorded.IntentKey);
        var outcome = Assert.Single(result.Outcomes);
        Assert.True(outcome.Recorded);
    }

    [Fact]
    public async Task Recording_an_outcome_already_recorded_is_idempotent_and_reported_as_such()
    {
        var outcomeSource = new RecordingOutcomeSource(Signal);
        var learningStore = new RecordingLearningStore();
        await learningStore.RecordAsync(
            new LearningRecord(Signal.IntentKey, Signal.Verdict, Signal.IssueUrl, Signal.Title, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var dependencies = TestDependencies.Build(
            learningComposer: new ScriptedLearningComposer(outcomeSource, learningStore));

        var result = await RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: false, live: true, ConfirmedEnv, dependencies, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.False(outcome.Recorded);
        Assert.Equal(0, result.Recorded);
    }

    [Fact]
    public async Task An_incomplete_learning_composition_is_reported_by_the_settings_that_are_unset()
    {
        var dependencies = TestDependencies.Build(
            learningComposer: new UnconfiguredLearningComposer(
                "no repository is configured to poll outcomes from (set GITHUB_REPOSITORY)",
                [RuntimeSettingsComposer.GitHubRepository]));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: true, live: false, NoConfirmEnv, dependencies, CancellationToken.None));

        Assert.Contains("GITHUB_REPOSITORY", exception.Message);
    }

    [Fact]
    public async Task A_poll_failure_is_reported_naming_the_product()
    {
        var dependencies = TestDependencies.Build(
            learningComposer: new ScriptedLearningComposer(
                new UnreachableOutcomeSource("search refused"), new RecordingLearningStore()));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: true, live: false, NoConfirmEnv, dependencies, CancellationToken.None));

        Assert.Contains("acme", exception.Message);
        Assert.Contains("search refused", exception.Message);
    }

    [Fact]
    public async Task A_recording_failure_is_reported_naming_the_intent_and_verdict()
    {
        var dependencies = TestDependencies.Build(
            learningComposer: new ScriptedLearningComposer(
                new RecordingOutcomeSource(Signal), new UnreachableLearningStore("write refused")));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.PollOutcomesAsync(
            Settings, dryRun: false, live: true, ConfirmedEnv, dependencies, CancellationToken.None));

        Assert.Contains("fingerprint-1:sentry", exception.Message);
        Assert.Contains("write refused", exception.Message);
    }
}
