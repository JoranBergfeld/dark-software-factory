using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The conveyor's state must survive the process that produced it: every station
/// checkpoint is written through the run store port, and a store that cannot be
/// written to fails the run loudly instead of leaving the run's only record in
/// process memory. A run whose requested source kinds have no gatherer must fail
/// too -- an empty, successful run is indistinguishable from a working one.
/// </summary>
public sealed class ConveyorPersistenceTests
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

    private static async Task<string> WriteSignalAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsf-signal-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task Every_station_checkpoint_is_persisted_through_the_run_store()
    {
        var store = new RecordingRunStore();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            runStore: store);
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(ConveyorLine.StationNames, store.Saved.Select(saved => saved.Station).ToArray());
            Assert.All(store.Saved, saved => Assert.Equal(run.Id, saved.RunId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_run_store_that_cannot_persist_fails_the_run_instead_of_reporting_success()
    {
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer(
                "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"))],
            runStore: new UnreachableRunStore("cosmos endpoint returned 403"));
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(RunStatus.Error, run.Status);
            Assert.Contains("cosmos endpoint returned 403", run.Audit[^1].Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_requested_source_kind_with_no_gatherer_fails_the_run_by_name()
    {
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: true, TestDependencies.Empty, CancellationToken.None);

            Assert.Equal(RunStatus.Error, run.Status);
            Assert.Contains("sentry", run.Audit[^1].Message);
            Assert.Contains("DSF_SOURCE_AGENT_ENDPOINT_SENTRY", run.Audit[^1].Message);
            Assert.Empty(run.FiledIssues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_sweep_over_kinds_with_no_gatherers_never_reports_a_filed_empty_run()
    {
        var dependencies = TestDependencies.Build(sourceAgentRosterReader: new RosterReader(["sentry"]));

        var run = await RuntimeVerbs.SweepAsync(
            Settings, dryRun: false, dependencies, CancellationToken.None, ConfirmedLiveFiling);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.NotEqual(RunStatus.Filed, run.Status);
        Assert.Empty(run.FiledIssues);
    }

    [Fact]
    public async Task A_terminal_sweep_result_does_not_suppress_a_later_sweep()
    {
        var store = new RecordingRunStore();
        var gatherer = new ScriptedEvidenceGatherer(
            "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [gatherer],
            runStore: store,
            sourceAgentRosterReader: new RosterReader(["sentry"]));

        var first = await RuntimeVerbs.SweepAsync(Settings, dryRun: true, dependencies, CancellationToken.None);
        Assert.Equal(RunStatus.Previewed, first.Status);

        var second = await RuntimeVerbs.SweepAsync(Settings, dryRun: true, dependencies, CancellationToken.None);

        // A later sweep over the exact same product and roster must still be
        // driven through every station -- the first sweep's terminal status must
        // never be reloaded and returned in place of processing the new sweep.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(RunStatus.Previewed, second.Status);
        Assert.Equal(ConveyorLine.StationNames, second.Checkpoints);
        Assert.Single(second.Evidence);
    }


    [Fact]
    public async Task A_run_rehydrates_a_persisted_checkpointed_run_and_skips_completed_stations()
    {
        var store = new RecordingRunStore();
        var gatherer = new ScriptedEvidenceGatherer(
            "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        var dependencies = TestDependencies.Build(evidenceGatherers: [gatherer], runStore: store);
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var runId = RunIdentity.Compute(TriggerKind.Signal, ["acme"], ["sentry"]);
            var priorRun = new ConveyorRun
            {
                Id = runId,
                Trigger = TriggerKind.Signal,
                ProductHints = ["acme"],
                SourceKinds = ["sentry"],
                DryRun = true,
            };
            priorRun.Checkpoints.AddRange(["s1_triage", "s2_investigation"]);
            priorRun.Evidence.Add(new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
            store.Seed(priorRun);

            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(runId, run.Id);
            Assert.DoesNotContain(store.Saved, saved => saved.Station is "s1_triage" or "s2_investigation");
            Assert.Equal(
                ConveyorLine.StationNames.Skip(2).ToArray(),
                store.Saved.Select(saved => saved.Station).ToArray());
            Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
            Assert.Equal(RunStatus.Previewed, run.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_terminal_persisted_run_is_not_re_driven_by_a_new_invocation()
    {
        var store = new RecordingRunStore();
        var gatherer = new ScriptedEvidenceGatherer(
            "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        var dependencies = TestDependencies.Build(evidenceGatherers: [gatherer], runStore: store);
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var runId = RunIdentity.Compute(TriggerKind.Signal, ["acme"], ["sentry"]);
            var priorRun = new ConveyorRun
            {
                Id = runId,
                Trigger = TriggerKind.Signal,
                ProductHints = ["acme"],
                SourceKinds = ["sentry"],
                DryRun = false,
            };
            priorRun.Checkpoints.AddRange(ConveyorLine.StationNames);
            priorRun.Status = RunStatus.Filed;
            priorRun.FiledIssues.Add("https://github.com/acme/acme/issues/1");
            store.Seed(priorRun);

            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(runId, run.Id);
            Assert.Equal(RunStatus.Filed, run.Status);
            Assert.Empty(store.Saved);
            Assert.Equal(["https://github.com/acme/acme/issues/1"], run.FiledIssues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_persisted_non_dry_run_checkpointed_through_s6_resumed_with_dry_run_does_not_file()
    {
        var store = new RecordingRunStore();
        var filer = new RecordingIssueFiler();
        var gatherer = new ScriptedEvidenceGatherer(
            "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        var dependencies = TestDependencies.Build(evidenceGatherers: [gatherer], runStore: store, issueFiler: filer);
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var runId = RunIdentity.Compute(TriggerKind.Signal, ["acme"], ["sentry"]);
            var priorRun = new ConveyorRun
            {
                Id = runId,
                Trigger = TriggerKind.Signal,
                ProductHints = ["acme"],
                SourceKinds = ["sentry"],
                DryRun = false,
            };
            // Simulates a crashed non-dry-run process: S1..S6 checkpointed, one
            // accepted proposal routed and ready for S7, but the process died
            // before filing it.
            priorRun.Checkpoints.AddRange(ConveyorLine.StationNames.Take(6));
            var proposal = new Proposal("p1", "Investigate checkout 500s", "sentry", ["SENTRY-1"])
            {
                Accepted = true,
                IntentKey = "intent-1",
            };
            proposal.Labels.Add("bug");
            priorRun.Proposals.Add(proposal);
            store.Seed(priorRun);

            var run = await RuntimeVerbs.RunAsync(Settings, path, dryRun: true, dependencies, CancellationToken.None);

            Assert.Equal(runId, run.Id);
            Assert.Equal(RunStatus.Previewed, run.Status);
            Assert.Empty(filer.Filed);
            Assert.Empty(run.FiledIssues);
            Assert.Single(run.PreviewedIssues);
            Assert.Equal("Investigate checkout 500s", run.PreviewedIssues[0].Title);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_persisted_dry_run_checkpointed_through_s6_resumed_without_dry_run_files_for_real()
    {
        var store = new RecordingRunStore();
        var filer = new RecordingIssueFiler();
        var gatherer = new ScriptedEvidenceGatherer(
            "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        var dependencies = TestDependencies.Build(evidenceGatherers: [gatherer], runStore: store, issueFiler: filer);
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var runId = RunIdentity.Compute(TriggerKind.Signal, ["acme"], ["sentry"]);
            var priorRun = new ConveyorRun
            {
                Id = runId,
                Trigger = TriggerKind.Signal,
                ProductHints = ["acme"],
                SourceKinds = ["sentry"],
                DryRun = true,
            };
            // Simulates a crashed dry-run process: S1..S6 checkpointed, one
            // accepted proposal routed and ready for S7, but the process died
            // before previewing it. A later non-dry-run invocation resumes the
            // same open run and must be allowed to file it for real -- the fix
            // must clear the stale DryRun=true it inherited, not just ever
            // force DryRun=true.
            priorRun.Checkpoints.AddRange(ConveyorLine.StationNames.Take(6));
            var proposal = new Proposal("p1", "Investigate checkout 500s", "sentry", ["SENTRY-1"])
            {
                Accepted = true,
                IntentKey = "intent-1",
            };
            proposal.Labels.Add("bug");
            priorRun.Proposals.Add(proposal);
            store.Seed(priorRun);

            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: false, dependencies, CancellationToken.None, ConfirmedLiveFiling);

            Assert.Equal(runId, run.Id);
            Assert.Equal(RunStatus.Filed, run.Status);
            Assert.Single(filer.Filed);
            Assert.Single(run.FiledIssues);
            Assert.Empty(run.PreviewedIssues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_terminal_previewed_run_resumed_without_dry_run_is_not_re_driven_or_filed()
    {
        var store = new RecordingRunStore();
        var filer = new RecordingIssueFiler();
        var gatherer = new ScriptedEvidenceGatherer(
            "sentry", new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        var dependencies = TestDependencies.Build(evidenceGatherers: [gatherer], runStore: store, issueFiler: filer);
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var runId = RunIdentity.Compute(TriggerKind.Signal, ["acme"], ["sentry"]);
            var priorRun = new ConveyorRun
            {
                Id = runId,
                Trigger = TriggerKind.Signal,
                ProductHints = ["acme"],
                SourceKinds = ["sentry"],
                DryRun = true,
            };
            priorRun.Checkpoints.AddRange(ConveyorLine.StationNames);
            priorRun.Status = RunStatus.Previewed;
            priorRun.PreviewedIssues.Add(new IssuePreview("Investigate checkout 500s", "intent-1", ["bug"]));
            store.Seed(priorRun);

            var run = await RuntimeVerbs.RunAsync(
                Settings, path, dryRun: false, dependencies, CancellationToken.None, ConfirmedLiveFiling);

            Assert.Equal(runId, run.Id);
            Assert.Equal(RunStatus.Previewed, run.Status);
            Assert.True(run.DryRun);
            Assert.Empty(filer.Filed);
            Assert.Empty(run.FiledIssues);
            Assert.Empty(store.Saved);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
