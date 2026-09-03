using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;

namespace Dsf.FeatureCouncil.Tests;

/// <summary>
/// Deterministic doubles for the conveyor's ports. Production source ships only
/// real implementations (ADR 0014), so every stand-in the line's tests need lives
/// here, in the test project.
/// </summary>
internal static class ConveyorDoubles
{
    public const string Product = "acme";

    public static ConveyorServices Services(
        IReadOnlyList<IEvidenceGatherer>? gatherers = null,
        IIssueFiler? issueFiler = null,
        IRunStore? runStore = null,
        IModelClient? modelClient = null,
        ITracer? tracer = null,
        IConfidenceThresholdReader? confidenceThresholdReader = null,
        ILearningStore? learningStore = null) =>
        new(
            Product,
            gatherers ?? [],
            issueFiler,
            runStore ?? new RecordingRunStore(),
            modelClient ?? new RecordingModelClient(),
            tracer ?? new RecordingTracer(),
            confidenceThresholdReader ?? new FixedConfidenceThresholdReader(S5Council.DefaultThreshold),
            learningStore);
}

/// <summary>A confidence threshold reader that always answers a fixed value.</summary>
internal sealed class FixedConfidenceThresholdReader(double threshold) : IConfidenceThresholdReader
{
    public Task<double> ReadThresholdAsync(CancellationToken cancellationToken) => Task.FromResult(threshold);
}

/// <summary>A confidence threshold reader whose backing store cannot be reached.</summary>
internal sealed class UnreachableConfidenceThresholdReader(string reason) : IConfidenceThresholdReader
{
    public Task<double> ReadThresholdAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>
/// A run store that records every persisted checkpoint in order and keeps the
/// last-saved document per run id.
/// </summary>
internal sealed class RecordingRunStore : IRunStore
{
    private readonly Dictionary<string, ConveyorRun> documents = [];

    public List<(string RunId, string Station, RunStatus Status, string[] Checkpoints)> Saved { get; } = [];

    public Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken)
    {
        Saved.Add((run.Id, station, run.Status, [.. run.Checkpoints]));
        documents[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<ConveyorRun?> LoadAsync(string runId, CancellationToken cancellationToken) =>
        Task.FromResult(documents.TryGetValue(runId, out var run) ? run : null);
}

/// <summary>A run store whose backing store cannot be reached.</summary>
internal sealed class UnreachableRunStore(string reason) : IRunStore
{
    public Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);

    public Task<ConveyorRun?> LoadAsync(string runId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>A tracer that records every event it was asked to send.</summary>
internal sealed class RecordingTracer : ITracer
{
    public List<(string Name, IReadOnlyDictionary<string, string?> Properties)> Traced { get; } = [];

    public Task TraceAsync(
        string name, IReadOnlyDictionary<string, string?> properties, CancellationToken cancellationToken)
    {
        Traced.Add((name, properties));
        return Task.CompletedTask;
    }
}

/// <summary>A tracer whose telemetry backend cannot be reached.</summary>
internal sealed class UnreachableTracer(string reason) : ITracer
{
    public Task TraceAsync(
        string name, IReadOnlyDictionary<string, string?> properties, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>A model client that answers a fixed completion and records its prompts.</summary>
internal sealed class RecordingModelClient(string response = "deterministic test completion") : IModelClient
{
    public List<string> Prompts { get; } = [];

    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        return Task.FromResult(response);
    }
}

/// <summary>
/// A learning store seeded with the lessons a prior run recorded, and that
/// records every retrieval and recording it is asked to do -- so a test can
/// assert both what S3 synthesis consulted and that a recorded outcome remains
/// idempotent by (intent key, verdict).
/// </summary>
internal sealed class RecordingLearningStore(params LearningRecord[] seeded) : ILearningStore
{
    private readonly List<LearningRecord> records = [.. seeded];

    public List<string> Retrieved { get; } = [];

    public List<LearningRecord> Recorded { get; } = [];

    public Task<bool> RecordAsync(LearningRecord record, CancellationToken cancellationToken)
    {
        var alreadyRecorded = this.records.Any(
            existing => existing.IntentKey == record.IntentKey && existing.Verdict == record.Verdict);
        if (alreadyRecorded)
        {
            return Task.FromResult(false);
        }

        this.records.Add(record);
        this.Recorded.Add(record);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<LearningRecord>> RetrieveAsync(string intentKey, CancellationToken cancellationToken)
    {
        this.Retrieved.Add(intentKey);
        IReadOnlyList<LearningRecord> found =
            [.. this.records.Where(record => record.IntentKey == intentKey)];
        return Task.FromResult(found);
    }
}

/// <summary>A learning store whose backing store cannot be reached.</summary>
internal sealed class UnreachableLearningStore(string reason) : ILearningStore
{
    public Task<bool> RecordAsync(LearningRecord record, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);

    public Task<IReadOnlyList<LearningRecord>> RetrieveAsync(string intentKey, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>An evidence gatherer that yields fixed evidence and counts its calls.</summary>
internal sealed class CountingEvidenceGatherer(string sourceKind, params EvidenceItem[] evidence)
    : IEvidenceGatherer
{
    public string SourceKind { get; } = sourceKind;

    public int Calls { get; private set; }

    public Task<IReadOnlyList<EvidenceItem>> GatherAsync(ConveyorRun run, CancellationToken cancellationToken)
    {
        this.Calls++;
        return Task.FromResult<IReadOnlyList<EvidenceItem>>(evidence);
    }
}

/// <summary>An evidence gatherer whose upstream source fails.</summary>
internal sealed class ThrowingEvidenceGatherer(string sourceKind, string reason) : IEvidenceGatherer
{
    public string SourceKind { get; } = sourceKind;

    public Task<IReadOnlyList<EvidenceItem>> GatherAsync(ConveyorRun run, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>An issue filer that records everything it was asked to file.</summary>
internal sealed class RecordingIssueFiler : IIssueFiler
{
    public List<Proposal> Filed { get; } = [];

    public Task<string> FileAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        this.Filed.Add(proposal);
        return Task.FromResult($"https://github.com/acme/acme/issues/{this.Filed.Count}");
    }
}
