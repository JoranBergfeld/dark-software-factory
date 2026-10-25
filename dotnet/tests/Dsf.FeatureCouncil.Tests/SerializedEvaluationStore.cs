using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.FeatureCouncil.Tests;

internal sealed class SerializedEvaluationStore : IRunStore
{
    private readonly Dictionary<string, string> documents;
    internal SerializedEvaluationStore(string? serialized = null) => documents = serialized is null ? []
        : JsonSerializer.Deserialize<Dictionary<string, string>>(serialized)!;
    internal string Export() => JsonSerializer.Serialize(documents);
    internal Action<ConveyorRun, string>? AfterSave { get; set; }

    public Task SaveAsync(ConveyorRun run, string station, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        documents[run.Id] = JsonSerializer.Serialize(run);
        AfterSave?.Invoke(run, station);
        return Task.CompletedTask;
    }

    public Task<ConveyorRun?> LoadAsync(string runId, CancellationToken token) =>
        Task.FromResult(documents.TryGetValue(runId, out var serialized) ? JsonSerializer.Deserialize<ConveyorRun>(serialized) : null);
}
