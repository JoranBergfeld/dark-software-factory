using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class SweepRuntimeLeaseTests
{
    private static readonly RuntimeSettings Settings = new(
        "acme", "https://appconfig.example", "", "", "https://cosmos.example",
        "https://openai.example", "gpt", "embed", "", "", "", "");

    [Fact]
    public async Task Overlapping_manual_sweeps_drive_only_one_conveyor_then_release_for_later_work()
    {
        var gateway = new SweepLeaseTestGateway();
        var gatherer = new BlockingGatherer();
        var dependencies = TestDependencies.Build(
            sourceAgentRosterReader: new RosterReader(["azuremonitor"]), evidenceGatherers: [gatherer]);
        var first = RuntimeVerbs.SweepAsync(
            Settings, true, dependencies, CancellationToken.None, sweepLease: Lease(gateway));
        try
        {
            await gatherer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = await RuntimeVerbs.SweepAsync(
                Settings, true, dependencies, CancellationToken.None, sweepLease: Lease(gateway));

            Assert.Null(second);
            Assert.Equal(1, gatherer.Calls);
        }
        finally
        {
            gatherer.Finish.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var later = await RuntimeVerbs.SweepAsync(
            Settings, true, dependencies, CancellationToken.None, sweepLease: Lease(gateway));
        Assert.NotNull(later);
        Assert.Equal(2, gatherer.Calls);
        Assert.Equal(2, gateway.Deletes.Count);
    }

    [Fact]
    public async Task Cancellation_cannot_release_ownership_while_uncooperative_work_still_runs()
    {
        var gateway = new SweepLeaseTestGateway();
        var gatherer = new BlockingGatherer();
        var dependencies = TestDependencies.Build(
            sourceAgentRosterReader: new RosterReader(["azuremonitor"]), evidenceGatherers: [gatherer]);
        using var cancellation = new CancellationTokenSource();
        var first = RuntimeVerbs.SweepAsync(
            Settings, true, dependencies, cancellation.Token, sweepLease: Lease(gateway));
        try
        {
            await gatherer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            var second = await RuntimeVerbs.SweepAsync(
                Settings, true, dependencies, CancellationToken.None, sweepLease: Lease(gateway));

            Assert.Null(second);
            Assert.False(first.IsCompleted);
            Assert.Empty(gateway.Deletes);
        }
        finally
        {
            gatherer.Finish.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        Assert.Single(gateway.Deletes);
    }

    [Fact]
    public async Task Failed_roster_read_releases_the_lease_and_preserves_the_operator_error()
    {
        var gateway = new SweepLeaseTestGateway();
        var dependencies = TestDependencies.Build(sourceAgentRosterReader: new UnreachableRosterReader("403 denied"));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.SweepAsync(
            Settings, true, dependencies, CancellationToken.None, sweepLease: Lease(gateway)));

        Assert.Contains("403 denied", exception.Message);
        Assert.Single(gateway.Deletes);
        await using var next = await Lease(gateway).TryAcquireAsync(
            Settings.Product, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task A_deployed_loop_cannot_overlap_a_manual_sweep()
    {
        var gateway = new SweepLeaseTestGateway();
        var gatherer = new BlockingGatherer();
        var dependencies = TestDependencies.Build(
            sourceAgentRosterReader: new RosterReader(["azuremonitor"]), evidenceGatherers: [gatherer]);
        using var loop = Loop(dependencies, gateway, TimeSpan.FromMilliseconds(25));
        var manual = RuntimeVerbs.SweepAsync(
            Settings, true, dependencies, CancellationToken.None, sweepLease: Lease(gateway));
        try
        {
            await gatherer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await loop.StartAsync(CancellationToken.None);
            await gateway.Conflict.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, gatherer.Calls);
            Assert.Empty(gateway.Deletes);
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
            gatherer.Finish.TrySetResult();
            await manual.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task A_restarted_loop_with_a_different_interval_cannot_overlap_the_old_worker()
    {
        var gateway = new SweepLeaseTestGateway();
        var gatherer = new BlockingGatherer();
        var dependencies = TestDependencies.Build(
            sourceAgentRosterReader: new RosterReader(["azuremonitor"]), evidenceGatherers: [gatherer]);
        using var oldLoop = Loop(dependencies, gateway, TimeSpan.FromMilliseconds(20));
        using var restartedLoop = Loop(dependencies, gateway, TimeSpan.FromMilliseconds(45));
        await oldLoop.StartAsync(CancellationToken.None);
        try
        {
            await gatherer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await restartedLoop.StartAsync(CancellationToken.None);
            await gateway.Conflict.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, gatherer.Calls);
            Assert.Empty(gateway.Deletes);
        }
        finally
        {
            await restartedLoop.StopAsync(CancellationToken.None);
            var stopped = oldLoop.StopAsync(CancellationToken.None);
            gatherer.Finish.TrySetResult();
            await stopped.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Manual_CLI_conflict_is_an_explicit_successful_skip_without_a_run_summary()
    {
        var gateway = new SweepLeaseTestGateway();
        await using var owner = await Lease(gateway).TryAcquireAsync(
            Settings.Product, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(owner);
        var dependencies = TestDependencies.Build() with { SweepLeaseFactory = _ => Lease(gateway) };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var env = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = Settings.Product,
            ["AZURE_APPCONFIG_ENDPOINT"] = Settings.AppConfigEndpoint,
            ["AZURE_COSMOS_ENDPOINT"] = Settings.CosmosEndpoint,
            ["AZURE_OPENAI_ENDPOINT"] = Settings.OpenAiEndpoint,
            ["AZURE_OPENAI_DEPLOYMENT"] = Settings.OpenAiDeployment,
            ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = Settings.OpenAiEmbeddingDeployment,
        };

        var exitCode = await RuntimeCliApplication.InvokeAsync(
            ["sweep", "--dry-run"], env, stdout, stderr, dependencies, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr.ToString());
        Assert.Contains("skipped", stdout.ToString());
        Assert.DoesNotContain("checkpoints", stdout.ToString());
        Assert.Empty(gateway.Deletes);
    }

    [Fact]
    public async Task A_lease_timeout_reports_an_operator_error_not_caller_cancellation()
    {
        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.SweepAsync(
            Settings, true, TestDependencies.Empty, CancellationToken.None, sweepLease: new TimedOutLease()));

        Assert.Contains("could not acquire the sweep lease", exception.Message);
    }

    [Fact]
    public async Task Failed_work_and_failed_cleanup_are_both_reported()
    {
        var gateway = new SweepLeaseTestGateway { DeleteFailure = new HttpRequestException("503 unavailable") };
        var dependencies = TestDependencies.Build(sourceAgentRosterReader: new UnreachableRosterReader("403 denied"));

        var exception = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.SweepAsync(
            Settings, true, dependencies, CancellationToken.None, sweepLease: Lease(gateway)));

        Assert.Contains("403 denied", exception.Message);
        Assert.Contains("503 unavailable", exception.Message);
    }

    private sealed class TimedOutLease : ISweepLease
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(
            string product, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken) =>
            Task.FromException<IAsyncDisposable?>(new OperationCanceledException("lease request timed out"));
    }

    private static PeriodicSweepService Loop(
        RuntimeDependencies dependencies, SweepLeaseTestGateway gateway, TimeSpan interval) =>
        new(Settings, dependencies, interval,
            new Dictionary<string, string?> { [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true" },
            new ScriptedSweepControlStore(), Lease(gateway), NullLogger<PeriodicSweepService>.Instance);

    private static CosmosSweepLease Lease(SweepLeaseTestGateway gateway) =>
        new("https://cosmos.example", "dsf", "runs", gateway);

    private sealed class BlockingGatherer : IEvidenceGatherer
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public string SourceKind => "azuremonitor";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<EvidenceItem>> GatherAsync(
            ConveyorRun run, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Entered.TrySetResult();
                await Finish.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }
    }
}
