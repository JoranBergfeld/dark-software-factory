using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

/// <summary>
/// The dry-run path through the conveyor: a signal traverses S1..S7 in order,
/// every station checkpoints through the run store, a station that already
/// checkpointed is not re-driven, a terminal run is never re-driven at all, a
/// per-station failure becomes an audited terminal error, and the filing station
/// previews what it would file without touching anything outside the process.
/// </summary>
public sealed class ConveyorLineTests
{
    private static readonly EvidenceItem SentryEvidence =
        new("sentry", "SENTRY-1", "checkout 500s spiked after release 4.2");

    private static ConveyorRun ScopedRun(bool dryRun = true) => new()
    {
        ProductHints = ["acme"],
        SourceKinds = ["sentry"],
        DryRun = dryRun,
    };

    [Fact]
    public async Task A_dry_run_traverses_every_station_in_order_and_ends_previewed()
    {
        var store = new RecordingRunStore();
        var services = ConveyorDoubles.Services(
            gatherers: [new CountingEvidenceGatherer("sentry", SentryEvidence)], runStore: store);

        var run = await ConveyorLine.RunAsync(ScopedRun(), services, CancellationToken.None);

        Assert.Equal(RunStatus.Previewed, run.Status);
        Assert.Equal(ConveyorLine.StationNames, run.Checkpoints);
        Assert.Equal(ConveyorLine.StationNames, store.Saved.Select(saved => saved.Station).ToArray());
        Assert.All(store.Saved, saved => Assert.Equal(run.Id, saved.RunId));
        Assert.Null(run.FailureReason);
    }

    [Fact]
    public async Task Each_station_is_checkpointed_before_the_next_station_runs()
    {
        var store = new RecordingRunStore();
        var services = ConveyorDoubles.Services(
            gatherers: [new CountingEvidenceGatherer("sentry", SentryEvidence)], runStore: store);

        await ConveyorLine.RunAsync(ScopedRun(), services, CancellationToken.None);

        for (var index = 0; index < store.Saved.Count; index++)
        {
            Assert.Equal(
                ConveyorLine.StationNames.Take(index + 1).ToArray(),
                store.Saved[index].Checkpoints);
        }
    }

    [Fact]
    public async Task A_station_that_already_checkpointed_is_not_re_driven()
    {
        var store = new RecordingRunStore();
        var gatherer = new CountingEvidenceGatherer("sentry", SentryEvidence);
        var services = ConveyorDoubles.Services(gatherers: [gatherer], runStore: store);
        var run = ScopedRun();
        run.Checkpoints.AddRange([S1Triage.StationName, S2Investigation.StationName]);
        run.Evidence.Add(SentryEvidence);

        var resumed = await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(0, gatherer.Calls);
        Assert.Equal(
            ConveyorLine.StationNames.Skip(2).ToArray(),
            store.Saved.Select(saved => saved.Station).ToArray());
        Assert.Equal(ConveyorLine.StationNames, resumed.Checkpoints);
        Assert.Single(resumed.Evidence);
        Assert.Equal(RunStatus.Previewed, resumed.Status);
    }

    [Theory]
    [InlineData(RunStatus.Killed)]
    [InlineData(RunStatus.Filed)]
    [InlineData(RunStatus.Error)]
    [InlineData(RunStatus.Previewed)]
    public async Task A_terminal_run_is_not_re_driven(RunStatus terminal)
    {
        var store = new RecordingRunStore();
        var tracer = new RecordingTracer();
        var gatherer = new CountingEvidenceGatherer("sentry", SentryEvidence);
        var services = ConveyorDoubles.Services(gatherers: [gatherer], runStore: store, tracer: tracer);
        var run = ScopedRun();
        run.Status = terminal;

        var returned = await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(terminal, returned.Status);
        Assert.Empty(returned.Checkpoints);
        Assert.Empty(store.Saved);
        Assert.Empty(tracer.Traced);
        Assert.Equal(0, gatherer.Calls);
    }

    [Fact]
    public async Task A_failing_station_becomes_an_audited_error_terminal_state()
    {
        var store = new RecordingRunStore();
        var services = ConveyorDoubles.Services(
            gatherers: [new ThrowingEvidenceGatherer("sentry", "sentry API returned 503")], runStore: store);

        var run = await ConveyorLine.RunAsync(ScopedRun(), services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal([S1Triage.StationName], run.Checkpoints);
        Assert.Empty(run.Proposals);
        Assert.Contains(
            run.Audit,
            record => record.Station == S2Investigation.StationName
                && record.Message.Contains("sentry API returned 503", StringComparison.Ordinal));
        var lastSave = store.Saved[^1];
        Assert.Equal(S2Investigation.StationName, lastSave.Station);
        Assert.Equal(RunStatus.Error, lastSave.Status);
    }

    [Fact]
    public async Task A_failing_station_records_the_failure_reason_the_runtime_reports()
    {
        var services = ConveyorDoubles.Services(
            gatherers: [new ThrowingEvidenceGatherer("sentry", "sentry API returned 503")],
            tracer: new UnreachableTracer("app insights ingestion refused the connection"));

        var run = await ConveyorLine.RunAsync(ScopedRun(), services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.NotNull(run.FailureReason);
        Assert.Contains(S2Investigation.StationName, run.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("sentry API returned 503", run.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("app insights", run.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_run_store_failure_keeps_the_station_failure_as_the_reported_cause()
    {
        var services = ConveyorDoubles.Services(
            gatherers: [new ThrowingEvidenceGatherer("sentry", "sentry API returned 503")],
            runStore: new UnreachableRunStore("cosmos endpoint returned 403"));

        var run = await ConveyorLine.RunAsync(ScopedRun(), services, CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains(S1Triage.StationName, run.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("cosmos endpoint returned 403", run.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_killed_at_triage_stops_the_line_without_driving_later_stations()
    {
        var store = new RecordingRunStore();
        var gatherer = new CountingEvidenceGatherer("sentry", SentryEvidence);
        var services = ConveyorDoubles.Services(gatherers: [gatherer], runStore: store);
        var unscoped = new ConveyorRun { DryRun = true };

        var run = await ConveyorLine.RunAsync(unscoped, services, CancellationToken.None);

        Assert.Equal(RunStatus.Killed, run.Status);
        Assert.Equal([S1Triage.StationName], run.Checkpoints);
        Assert.Equal(0, gatherer.Calls);
        Assert.Single(store.Saved);
        Assert.Empty(run.FiledIssues);
    }

    [Fact]
    public async Task A_dry_run_files_nothing_even_when_a_filer_is_wired()
    {
        var filer = new RecordingIssueFiler();
        var services = ConveyorDoubles.Services(
            gatherers: [new CountingEvidenceGatherer("sentry", SentryEvidence)], issueFiler: filer);

        var run = await ConveyorLine.RunAsync(ScopedRun(), services, CancellationToken.None);

        Assert.Empty(filer.Filed);
        Assert.Empty(run.FiledIssues);
        Assert.Equal(RunStatus.Previewed, run.Status);
        Assert.Contains(run.Proposals, proposal => proposal.Accepted);
    }

    [Fact]
    public async Task A_dry_run_previews_every_issue_it_would_have_filed()
    {
        var services = ConveyorDoubles.Services(
            gatherers: [new CountingEvidenceGatherer("sentry", SentryEvidence)]);

        var run = await ConveyorLine.RunAsync(ScopedRun(), services, CancellationToken.None);

        var accepted = run.Proposals.Single(proposal => proposal.Accepted);
        var preview = Assert.Single(run.PreviewedIssues);
        Assert.Equal(accepted.Title, preview.Title);
        Assert.Equal(accepted.IntentKey, preview.IntentKey);
        Assert.Equal(accepted.Labels, preview.Labels);
        Assert.Contains(
            run.Audit,
            record => record.Station == S7Filing.StationName
                && record.Message.Contains("would file", StringComparison.Ordinal)
                && record.Message.Contains(accepted.Title, StringComparison.Ordinal)
                && record.Message.Contains(S6Routing.ReadyForAgentLabel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dry_run_previews_nothing_for_a_proposal_the_council_rejected()
    {
        var services = ConveyorDoubles.Services(gatherers:
        [
            new CountingEvidenceGatherer("sentry", SentryEvidence),
            new CountingEvidenceGatherer(
                "grafana",
                new EvidenceItem("grafana", "GRAF-1", "p95 latency doubled"),
                new EvidenceItem("grafana", "GRAF-2", "error budget burn")),
        ]);
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry", "grafana"], DryRun = true };

        var finished = await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        var rejected = finished.Proposals.Where(proposal => !proposal.Accepted).ToList();
        Assert.NotEmpty(rejected);
        Assert.All(
            rejected,
            proposal => Assert.DoesNotContain(
                finished.PreviewedIssues, preview => preview.Title == proposal.Title));
        Assert.Equal(
            finished.Proposals.Count(proposal => proposal.Accepted), finished.PreviewedIssues.Count);
    }
}
