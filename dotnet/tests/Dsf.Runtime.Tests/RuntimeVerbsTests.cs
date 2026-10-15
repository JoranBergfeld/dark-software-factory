using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dsf.Core.Onboarding;
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

    /// <summary>The manual live-filing gate, confirmed -- for tests that must reach real filing.</summary>
    private static readonly IReadOnlyDictionary<string, string?> ConfirmedLiveFiling = new Dictionary<string, string?>
    {
        [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true",
    };

    [Fact]
    public async Task Greenfield_configuration_sharing_only_a_name_prefix_keeps_existing_live_behavior()
    {
        var filer = new RecordingIssueFiler();
        var settings = Settings with { AppConfigEndpoint = "https://dsfo-existing.azconfig.io" };
        var dependencies = TestDependencies.Build(issueFiler: filer, evidenceGatherers:
            [new ScriptedEvidenceGatherer("azuremonitor", new EvidenceItem("azuremonitor", "incident-1", "checkout failures"))]);

        var run = await RuntimeVerbs.RunSignalAsync(settings,
            SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", false),
            dependencies, CancellationToken.None, ConfirmedLiveFiling);

        Assert.Equal(RunStatus.Filed, run.Status);
        Assert.Single(filer.Filed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Onboarding_profile_pin_cannot_be_bypassed_by_manual_live_confirmation_or_dry_run(bool dryRun)
    {
        var settings = Settings with
        {
            IntegrationSettings = new Dictionary<string, string?> { ["DSF_FACTORY_PROFILE_PIN"] = "invalid-or-corrupt" },
        };
        var filer = new RecordingIssueFiler();
        var models = new RecordingModelClient();
        var dependencies = TestDependencies.Build(issueFiler: filer, modelClient: models);
        var error = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(
            settings, SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", dryRun),
            dependencies, CancellationToken.None, ConfirmedLiveFiling));
        Assert.Contains("onboarding", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(filer.Filed);
        Assert.Empty(models.Prompts);
    }

    [Theory]
    [InlineData("sweep")]
    [InlineData("outcomes")]
    [InlineData("loop")]
    [InlineData("http")]
    [InlineData("agent")]
    public async Task Inactive_onboarding_blocks_every_entry_even_after_the_profile_pin_is_removed(string entry)
    {
        var settings = Settings with { AppConfigEndpoint = "https://dsfo-11111111111111111111111111111111.azconfig.io" };
        var runs = new RecordingRunStore();
        var filer = new RecordingIssueFiler();
        var lease = new ScriptedSweepLease();
        var outcomes = new RecordingOutcomeSource();
        var learning = new RecordingLearningStore();
        var models = new RecordingModelClient();
        var dependencies = TestDependencies.Build(runStore: runs, issueFiler: filer, modelClient: models,
            learningComposer: new ScriptedLearningComposer(outcomes, learning), sweepLeaseFactory: _ => lease);
        if (entry is "http" or "agent")
        {
            await using var host = entry == "http"
                ? RuntimeVerbs.BuildOrchestratorHost(settings, dependencies, "127.0.0.1", 0, env: ConfirmedLiveFiling)
                : RuntimeVerbs.BuildSourceAgentHost(settings, "azuremonitor", dependencies, "127.0.0.1", 0);
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(host)) };
            using var response = await client.PostAsync(entry == "http" ? "/run" : SourceAgentCard.GatherRoute,
                new StringContent("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("inactive", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            await host.StopAsync();
        }
        else
        {
            var error = await Assert.ThrowsAsync<RuntimeVerbException>(async () =>
            {
                if (entry == "sweep") await RuntimeVerbs.SweepAsync(settings, false, dependencies, CancellationToken.None, ConfirmedLiveFiling);
                else if (entry == "outcomes") await RuntimeVerbs.PollOutcomesAsync(settings, true, false, ConfirmedLiveFiling, dependencies, CancellationToken.None);
                else await RuntimeVerbs.ServeOrchestratorAsync(settings, dependencies, "127.0.0.1", 0,
                    TimeSpan.FromSeconds(1), CancellationToken.None, ConfirmedLiveFiling);
            });
            Assert.Contains("inactive", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(runs.Saved);
        Assert.Empty(filer.Filed);
        Assert.Empty(models.Prompts);
        Assert.Empty(lease.Requests);
        Assert.Equal(0, outcomes.PollCount);
        Assert.Empty(learning.Recorded);
    }

    [Fact]
    public async Task Onboarding_resume_cannot_upgrade_a_serialized_open_preview_into_live_work()
    {
        var persisted = new ConveyorRun
        {
            Id = RunIdentity.Compute(TriggerKind.Signal, ["acme"], ["azuremonitor"]),
            ProductHints = ["acme"],
            SourceKinds = ["azuremonitor"],
            DryRun = true,
        };
        persisted.Checkpoints.AddRange(ConveyorLine.StationNames.Take(6));
        var store = new SerializedAdmissionRunStore(JsonSerializer.Serialize(persisted));
        var original = store.Payload;
        var filer = new RecordingIssueFiler();
        var settings = Settings with { AppConfigEndpoint = "https://dsfo-11111111111111111111111111111111.azconfig.io" };
        var dependencies = TestDependencies.Build(runStore: store, issueFiler: filer);

        await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(settings,
            SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", false),
            dependencies, CancellationToken.None, ConfirmedLiveFiling));

        Assert.Equal(original, store.Payload);
        Assert.Equal(0, store.Writes);
        Assert.Empty(filer.Filed);
    }

    [Fact]
    public async Task A_running_scheduled_host_cannot_bypass_onboarding_admission_on_later_ticks()
    {
        var configuration = RuntimeAdmissionTests.Onboarding();
        var controls = new ObservedAdmissionControls();
        var lease = new ScriptedSweepLease();
        var store = new RecordingRunStore();
        var filer = new RecordingIssueFiler();
        var settings = Settings with { AppConfigEndpoint = "https://dsfo-11111111111111111111111111111111.azconfig.io." };
        var dependencies = TestDependencies.Build(runStore: store, issueFiler: filer,
            configurationSettingsGateway: configuration,
            sweepControlStoreFactory: _ => controls, sweepLeaseFactory: _ => lease,
            sourceAgentRosterReader: new RosterReader(["azuremonitor"]),
            evidenceGatherers: [new ScriptedEvidenceGatherer("azuremonitor", new EvidenceItem("azuremonitor", "incident-1", "checkout failures"))]);
        await using var host = RuntimeVerbs.BuildOrchestratorHost(settings, dependencies, "127.0.0.1", 0,
            TimeSpan.FromMilliseconds(10), ConfirmedLiveFiling);
        await host.StartAsync();
        try
        {
            await controls.RepeatedReads.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(lease.Requests);
            Assert.Empty(store.Saved);
            Assert.Empty(filer.Filed);
        }
        finally { await host.StopAsync(); }
        Assert.NotEmpty(configuration.Reads);
        Assert.Empty(configuration.Writes);
    }

    private static async Task<string> WriteSignalAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsf-signal-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task Run_dry_run_drives_every_station_and_records_checkpoints()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var dependencies = TestDependencies.Build(evidenceGatherers:
        [
            new ScriptedEvidenceGatherer("azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked")),
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
            Assert.Equal(["azuremonitor"], run.SourceKinds);
            Assert.Contains(run.Audit, a => a.Station == "s2_investigation" && a.Message.Contains("azuremonitor"));
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
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var dependencies = TestDependencies.Build(evidenceGatherers:
        [
            new ScriptedEvidenceGatherer(
                "azuremonitor",
                new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked after release"),
                new EvidenceItem("azuremonitor", "AZUREMONITOR-2", "checkout 500s spiked again, same trace")),
        ]);
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: false, dependencies, CancellationToken.None, ConfirmedLiveFiling);

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
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var filer = new RecordingIssueFiler();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked"))],
            issueFiler: filer);
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: false, dependencies, CancellationToken.None, ConfirmedLiveFiling);

            Assert.Equal(RunStatus.Filed, run.Status);
            Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
            var filed = Assert.Single(filer.Filed);
            Assert.Contains("creation:ready", filed.Labels);
            Assert.Single(run.FiledIssues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_without_dry_run_and_without_the_manual_live_filing_gate_is_refused_before_the_line_runs()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var dependencies = TestDependencies.Build(evidenceGatherers:
        [
            new ScriptedEvidenceGatherer("azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked")),
        ]);
        try
        {
            var exception = await Assert.ThrowsAsync<RuntimeVerbException>(
                () => RuntimeVerbs.RunAsync(Settings, path, dryRun: false, dependencies, CancellationToken.None));

            Assert.Contains(RuntimeIntegrationSettings.ConfirmLiveFiling, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sweep_without_dry_run_and_without_the_manual_live_filing_gate_is_refused_before_the_line_runs()
    {
        var dependencies = TestDependencies.Build(sourceAgentRosterReader: new RosterReader(["azuremonitor"]));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(
            () => RuntimeVerbs.SweepAsync(Settings, dryRun: false, dependencies, CancellationToken.None));

        Assert.Contains(RuntimeIntegrationSettings.ConfirmLiveFiling, exception.Message);
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
        var roster = new RosterReader(["foundryiq", "azuremonitor"]);

        var run = await RuntimeVerbs.SweepAsync(
            Settings,
            dryRun: true,
            TestDependencies.Build(
                sourceAgentRosterReader: roster,
                evidenceGatherers:
                [
                    new ScriptedEvidenceGatherer("foundryiq"),
                    new ScriptedEvidenceGatherer("azuremonitor"),
                ]),
            CancellationToken.None);

        Assert.Equal("acme", roster.RequestedSettings?.Product);
        Assert.NotNull(run);
        Assert.Equal(["foundryiq", "azuremonitor"], run.SourceKinds);
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
    public async Task Uncertain_onboarding_sweep_resumes_the_same_durable_invocation_on_the_next_tick()
    {
        var configuration = PromotedOnboarding();
        var decisions = new ScriptedOnboardingDecisionRunner();
        decisions.Failures.Enqueue(new RuntimeVerbException("decision outcome unconfirmed"));
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration) with
        {
            OnboardingDecisionRunner = decisions,
        };

        await Assert.ThrowsAsync<RuntimeVerbException>(() =>
            RuntimeVerbs.SweepAsync(Settings with
            {
                AppConfigEndpoint = "https://dsfo-11111111111111111111111111111111.azconfig.io",
            }, true, dependencies, CancellationToken.None));
        var resumed = await RuntimeVerbs.SweepAsync(Settings with
        {
            AppConfigEndpoint = "https://dsfo-11111111111111111111111111111111.azconfig.io",
        }, false, dependencies, CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Equal(2, decisions.Requests.Count);
        Assert.NotEqual(Guid.Empty, decisions.Requests[0].InvocationId);
        Assert.Equal(decisions.Requests[0].InvocationId, decisions.Requests[1].InvocationId);
        Assert.True(decisions.Requests[1].DryRun);
    }

    [Fact]
    public async Task Orchestrator_host_serves_health_and_previews_a_posted_signal()
    {
        var dependencies = TestDependencies.Build(evidenceGatherers:
        [
            new ScriptedEvidenceGatherer("azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked")),
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
                    """{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""", Encoding.UTF8, "application/json"));

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
                "azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked"))],
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
                    """{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""", Encoding.UTF8, "application/json"));

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
    public async Task Greenfield_http_transport_failure_preserves_server_error_without_onboarding_recovery_text()
    {
        await using var app = RuntimeVerbs.BuildOrchestratorHost(
            Settings, TestDependencies.Build(runStore: new UnconfirmedRunStore()), "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };
            using var response = await client.PostAsync("/run",
                new StringContent("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""",
                    Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("invocation_id", await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Onboarding_http_unknown_returns_conflict_with_original_invocation_recovery()
    {
        var invocation = Guid.NewGuid();
        var decisions = new ScriptedOnboardingDecisionRunner();
        decisions.Failures.Enqueue(new IOException("response lost"));
        var dependencies = TestDependencies.Build(configurationSettingsGateway: PromotedOnboarding()) with
        {
            OnboardingDecisionRunner = decisions,
        };
        await using var app = RuntimeVerbs.BuildOrchestratorHost(
            Settings with { AppConfigEndpoint = "https://dsfo-11111111111111111111111111111111.azconfig.io" },
            dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-token");
            using var response = await client.PostAsync("/run",
                new StringContent(JsonSerializer.Serialize(new
                {
                    product_hints = "acme",
                    source_kinds = Array.Empty<string>(),
                    invocation_id = invocation,
                }), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("invocation_id", await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(invocation, Assert.Single(decisions.Requests).InvocationId);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Agent_host_publishes_its_card_and_gathers_from_its_integration()
    {
        var dependencies = TestDependencies.Build(sourceIntegration: new ScriptedSourceIntegration(
            new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked")));
        var app = RuntimeVerbs.BuildSourceAgentHost(Settings, "AZUREMONITOR", dependencies, "127.0.0.1", 0);
        await using var host = app;
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

            var card = await client.GetFromJsonAsync<JsonElement>("/.well-known/agent-card.json");
            Assert.Equal("azuremonitor", card.GetProperty("kind").GetString());
            Assert.Equal("dsf-azuremonitor-agent", card.GetProperty("name").GetString());
            Assert.Equal("acme", card.GetProperty("product").GetString());

            var gather = await client.PostAsync("/gather", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, gather.StatusCode);
            var payload = await gather.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "AZUREMONITOR-1",
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
        Assert.Contains("azuremonitor", exception.Message);
    }

    [Theory]
    [InlineData("azuremonitor")]
    [InlineData("foundryiq")]
    [InlineData("webiq")]
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
    public async Task Run_uses_the_synthesis_model_for_lenses_and_independent_jurors_for_validation()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var model = new RecordingModelClient();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked"))],
            modelClient: model);
        try
        {
            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Previewed, run.Status);
            // Synthesis and two rounds of five lenses share this client; the jury does not.
            Assert.Equal(11, model.Prompts.Count);
            Assert.Contains(model.Prompts, prompt => prompt.Contains("azuremonitor", StringComparison.OrdinalIgnoreCase));
            var review = Assert.IsType<CouncilReview>(Assert.Single(run.Proposals).CouncilReview);
            Assert.Equal(
                ["gpt", "deepseek", "grok"],
                review.JuryVerdicts.Select(verdict => Assert.IsType<JurorModelSettings>(verdict.Model).Family));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_fails_with_an_audited_error_when_the_model_client_call_fails()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked"))],
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
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var tracer = new RecordingTracer();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked"))],
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
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["azuremonitor"]}""");
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "azuremonitor", new EvidenceItem("azuremonitor", "AZUREMONITOR-1", "checkout 500s spiked"))],
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

    private static TestDependencies.PersistedRuntimeConfiguration PromotedOnboarding()
    {
        var configuration = RuntimeAdmissionTests.Onboarding();
        var operation = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var seed = new AuditRecordIdentity(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AuditStoreKind.Governance, FactoryPromotion.SeedsKey(operation), "revision", new string('d', 64));
        var approval = new FactoryPromotionApproval(
            1, seed.FactoryId, operation, "55555555-5555-5555-5555-555555555555",
            DateTimeOffset.Parse("2026-09-15T23:00:00Z"), "", "authority", new string('e', 64),
            new string('f', 64), [])
        {
            NativeIndices = seed,
            Phase = FactoryPromotionPhase.Active,
        };
        approval = approval with { ReviewFingerprint = FactoryPromotion.Fingerprint(approval) };
        configuration.Values[FactoryPromotion.RootKey] =
            JsonSerializer.Serialize(approval, FactorySourceValidation.RecordJson);
        configuration.Values[FactoryPromotion.IntentKey] = "{}";
        configuration.Values[seed.Key] = "{}";
        configuration.Values[FactoryPromotion.CompletionKey(operation)] = "{}";
        return configuration;
    }

    private sealed class ScriptedOnboardingDecisionRunner : IOnboardingDecisionRunner
    {
        public Queue<Exception> Failures { get; } = [];
        public List<FactoryDecisionRequest> Requests { get; } = [];

        public Task<ConveyorRun> RunAsync(
            RuntimeSettings settings, RuntimeDependencies dependencies, FactoryManifest manifest,
            FactoryDecisionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failures.TryDequeue(out var failure)) return Task.FromException<ConveyorRun>(failure);
            return Task.FromResult(new ConveyorRun
            {
                Id = FactoryPromotion.DecisionId(manifest.Identity.FactoryId, request.InvocationId) + "-run",
                Profile = ConveyorProfile.OnboardingPreview,
                ProductHints = [manifest.Identity.ProductKey],
                DryRun = request.DryRun,
                Status = RunStatus.Previewed,
            });
        }

        public Task<PreviewHttpObservation> ObserveAsync(
            RuntimeSettings settings, RuntimeDependencies dependencies, FactoryManifest manifest,
            FactoryDecisionRequest request, string bearer, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failures.TryDequeue(out var failure)) return Task.FromException<PreviewHttpObservation>(failure);
            throw new NotSupportedException();
        }
    }

    private sealed class UnconfirmedRunStore : IRunStore
    {
        public Task<ConveyorRun?> LoadAsync(string runId, CancellationToken cancellationToken) =>
            Task.FromException<ConveyorRun?>(new OperationCanceledException("storage response unconfirmed"));

        public Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
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
        new("fingerprint-1:azuremonitor", OutcomeLabels.Approved, "https://github.com/acme/acme/issues/9", "[azuremonitor] checkout 500s spiked");

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
        Assert.Equal("fingerprint-1:azuremonitor", outcome.IntentKey);
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
        Assert.Equal("fingerprint-1:azuremonitor", recorded.IntentKey);
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

        Assert.Contains("fingerprint-1:azuremonitor", exception.Message);
        Assert.Contains("write refused", exception.Message);
    }
}
