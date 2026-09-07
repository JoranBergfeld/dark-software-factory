using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Proves the Decide phase's <c>creation:ready</c> acceptance gate end-to-end
/// (#183): a manually-triggered <c>dsf sweep</c> reaches S7 filing with no
/// human or mock standing in for evidence, judgment, or scheduling. WebIQ is
/// enabled through its real typed adapter (<see cref="WebIqIntegration"/>),
/// served by a real source-agent host (<see
/// cref="RuntimeVerbs.BuildSourceAgentHost"/>) that the orchestrator reaches
/// over a genuine HTTP A2A hop (<see cref="SourceAgentEvidenceGatherer"/>);
/// only the innermost outbound call each external boundary would otherwise
/// make -- the live WebIQ HTTP search and the live GitHub issue-filing REST
/// call -- is scripted. Every station in between (S2 gather, S3's real
/// <c>LexicalSimilarityEvidenceClusterer</c>, S4 grounding, S5's real
/// five-lens deliberation and three-juror validation panel, S6 routing, S7
/// filing) runs unmodified production code.
/// </summary>
public sealed class EndToEndAcceptanceGateTests
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
        GitHubAppId: "app-id",
        GitHubInstallationId: "install-id",
        GitHubAppPrivateKeySecret: "secret",
        GitHubRepository: "acme/acme");

    private static string BaseAddress(WebApplication app) =>
        app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);

    [Fact]
    public async Task A_manually_triggered_sweep_files_a_creation_ready_issue_through_the_real_webiq_adapter_and_five_lens_three_juror_council()
    {
        var env = new Dictionary<string, string?>
        {
            [RuntimeIntegrationSettings.WebIqQuery] = "checkout latency incidents",
            [RuntimeIntegrationSettings.WebIqApiKey] = "test-webiq-key",
            [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true",
        };

        // The one seam that would otherwise require a live outbound call: the
        // WebIQ web-search HTTP endpoint itself. WebIqIntegration -- the real
        // typed adapter -- runs unmodified against this scripted gateway.
        var gateway = new ScriptedWebIqSearchGateway(
            new WebIqResult(
                "Checkout 500s spiking after release 4.2",
                "https://status.example/incidents/42",
                "checkout errors spiked 12x after release 4.2"),
            new WebIqResult(
                "Checkout 500 errors after release 4.2",
                "https://status.example/incidents/43",
                "same release correlates with checkout 500s"));
        var webIqIntegration = new WebIqIntegration(env, gateway);

        // A real served source-agent host for the 'webiq' kind -- exactly what
        // `dsf serve-agent --kind webiq` would run -- fronting that integration.
        var agentDependencies = TestDependencies.Build(
            sourceIntegrationsByKind: new Dictionary<string, ISourceIntegration> { ["webiq"] = webIqIntegration });
        var agent = RuntimeVerbs.BuildSourceAgentHost(Settings, "webiq", agentDependencies, "127.0.0.1", 0);
        await using var agentHost = agent;
        await agent.StartAsync();
        try
        {
            // The orchestrator's real A2A client, pointed at the served agent
            // over genuine HTTP -- not an in-process call to the integration.
            var gatherer = new SourceAgentEvidenceGatherer("webiq", new Uri(BaseAddress(agent)), new HttpClient());
            var filer = new RecordingIssueFiler();
            var runStore = new RecordingRunStore();
            var model = new RecordingModelClient();

            var dependencies = TestDependencies.Build(
                sourceAgentRosterReader: new RosterReader(["webiq"]),
                evidenceGatherers: [gatherer],
                issueFiler: filer,
                runStore: runStore,
                modelClient: model);

            var run = await RuntimeVerbs.SweepAsync(Settings, dryRun: false, dependencies, CancellationToken.None, env);

            Assert.Equal(RunStatus.Filed, run.Status);
            Assert.Equal(2, run.Evidence.Count);
            Assert.All(run.Evidence, item => Assert.Equal("webiq", item.SourceKind));
            Assert.Contains(run.Evidence, item => item.Reference == "https://status.example/incidents/42");

            var filed = Assert.Single(filer.Filed);
            Assert.Equal(ProposalVerdict.Proceed, filed.Verdict);
            Assert.Contains(S6Routing.ReadyForAgentLabel, filed.Labels);
            Assert.Contains("source:webiq", filed.Labels);
            Assert.False(string.IsNullOrEmpty(filed.IntentKey));

            // Real five-lens deliberation and real three-juror validation both
            // ran -- not a mock standing in for S5's judgment -- and reached an
            // explicit Proceed outcome, all recorded to the run's audit trail.
            foreach (var lensName in new[] { "value", "cost", "feasibility", "security", "strategic-fit" })
            {
                Assert.Contains(
                    run.Audit, record => record.Message.Contains($"lens '{lensName}'", StringComparison.Ordinal));
            }

            foreach (var jurorName in new[] { "juror-primary", "juror-adversarial", "juror-operational" })
            {
                Assert.Contains(
                    run.Audit, record => record.Message.Contains($"juror '{jurorName}'", StringComparison.Ordinal));
            }

            Assert.Contains(
                run.Audit, record => record.Message.Contains("verdict=Proceed", StringComparison.Ordinal));
        }
        finally
        {
            await agent.StopAsync();
        }
    }

    [Fact]
    public async Task The_in_process_lease_guarded_sweep_loop_ticks_autonomously_without_a_manual_trigger()
    {
        var env = new Dictionary<string, string?>
        {
            [RuntimeIntegrationSettings.WebIqQuery] = "checkout latency incidents",
            [RuntimeIntegrationSettings.WebIqApiKey] = "test-webiq-key",
            [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true",
        };
        var gateway = new ScriptedWebIqSearchGateway(
            new WebIqResult(
                "Checkout 500s spiking after release 4.2",
                "https://status.example/incidents/42",
                "checkout errors spiked 12x after release 4.2"));
        var webIqIntegration = new WebIqIntegration(env, gateway);
        var agentDependencies = TestDependencies.Build(
            sourceIntegrationsByKind: new Dictionary<string, ISourceIntegration> { ["webiq"] = webIqIntegration });
        var agent = RuntimeVerbs.BuildSourceAgentHost(Settings, "webiq", agentDependencies, "127.0.0.1", 0);
        await using var agentHost = agent;
        await agent.StartAsync();
        try
        {
            var gatherer = new SourceAgentEvidenceGatherer("webiq", new Uri(BaseAddress(agent)), new HttpClient());
            var runStore = new RecordingRunStore();
            var dependencies = TestDependencies.Build(
                sourceAgentRosterReader: new RosterReader(["webiq"]),
                evidenceGatherers: [gatherer],
                issueFiler: new RecordingIssueFiler(),
                runStore: runStore,
                modelClient: new RecordingModelClient());

            var sweepLease = new ScriptedSweepLease();
            var loop = new PeriodicSweepService(
                Settings,
                dependencies,
                TimeSpan.FromMilliseconds(20),
                env,
                new ScriptedSweepControlStore(),
                sweepLease,
                NullLogger<PeriodicSweepService>.Instance);

            // No manual `dsf sweep` invocation here: the loop is the only thing
            // driving RuntimeVerbs.SweepAsync -- it must tick on its own. Wait
            // for a *terminal* checkpoint rather than just any checkpoint: the
            // line saves once per station (s1_triage first), so stopping as
            // soon as the very first checkpoint appears would race ahead of
            // filing and fail intermittently.
            await loop.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!runStore.Saved.Any(saved => saved.Status == RunStatus.Filed) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            await loop.StopAsync(CancellationToken.None);

            Assert.True(sweepLease.Requests.Count > 0, "the loop never attempted to acquire the sweep lease.");
            Assert.True(runStore.Saved.Count > 0, "the loop never drove an autonomous sweep tick through the line.");
            Assert.Contains(runStore.Saved, saved => saved.Status == RunStatus.Filed);
        }
        finally
        {
            await agent.StopAsync();
        }
    }
}
