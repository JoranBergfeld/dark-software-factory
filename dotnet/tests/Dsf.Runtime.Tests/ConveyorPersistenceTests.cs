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

        var run = await RuntimeVerbs.SweepAsync(Settings, dryRun: false, dependencies, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.NotEqual(RunStatus.Filed, run.Status);
        Assert.Empty(run.FiledIssues);
    }
}
