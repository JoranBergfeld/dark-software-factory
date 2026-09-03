using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The production learning store persists one audited record per (intent key,
/// verdict) pair, partitioned by product, and is idempotent: recording the same
/// pair again is recognized from the document already there and skipped rather
/// than duplicated, exactly like the run store's checkpoint semantics.
/// </summary>
public sealed class CosmosLearningStoreTests
{
    private sealed class RecordingCosmosGateway : ICosmosDocumentGateway
    {
        private readonly Dictionary<string, string> documents = [];

        public List<(string PartitionKey, string Id, string Json)> Upserts { get; } = [];

        public List<string> Reads { get; } = [];

        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken)
        {
            Upserts.Add((partitionKey, id, json));
            documents[id] = json;
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(
            string endpoint, string database, string container, string partitionKey, string id,
            CancellationToken cancellationToken)
        {
            Reads.Add(id);
            return Task.FromResult(documents.TryGetValue(id, out var json) ? json : null);
        }
    }

    private sealed class FailingCosmosGateway : ICosmosDocumentGateway
    {
        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("403 Forbidden");

        public Task<string?> ReadAsync(
            string endpoint, string database, string container, string partitionKey, string id,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("403 Forbidden");
    }

    private static LearningRecord Record(string intentKey = "fingerprint-1:sentry", string verdict = "dsf-outcome:approved") =>
        new(intentKey, verdict, "https://github.com/acme/acme/issues/9", "[sentry] checkout 500s spiked", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Recording_a_new_outcome_upserts_a_document_and_reports_it_was_newly_recorded()
    {
        var gateway = new RecordingCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);

        var recorded = await store.RecordAsync(Record(), CancellationToken.None);

        Assert.True(recorded);
        var upsert = Assert.Single(gateway.Upserts);
        Assert.Equal("acme", upsert.PartitionKey);
        using var document = JsonDocument.Parse(upsert.Json);
        Assert.Equal("fingerprint-1:sentry", document.RootElement.GetProperty("intentKey").GetString());
        Assert.Equal("dsf-outcome:approved", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("acme", document.RootElement.GetProperty("product").GetString());
    }

    [Fact]
    public async Task Recording_the_same_intent_and_verdict_again_is_a_no_op_reported_as_already_recorded()
    {
        var gateway = new RecordingCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);
        await store.RecordAsync(Record(), CancellationToken.None);

        var recordedAgain = await store.RecordAsync(Record(), CancellationToken.None);

        Assert.False(recordedAgain);
        Assert.Single(gateway.Upserts);
    }

    [Fact]
    public async Task A_different_verdict_for_the_same_intent_is_recorded_as_a_separate_document()
    {
        var gateway = new RecordingCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);
        await store.RecordAsync(Record(verdict: OutcomeLabels.Approved), CancellationToken.None);

        var recorded = await store.RecordAsync(Record(verdict: OutcomeLabels.Rejected), CancellationToken.None);

        Assert.True(recorded);
        Assert.Equal(2, gateway.Upserts.Count);
    }

    [Fact]
    public async Task A_rejected_write_is_reported_with_the_store_it_could_not_write_to()
    {
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", new FailingCosmosGateway());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RecordAsync(Record(), CancellationToken.None));

        Assert.Contains("https://cosmos.example", exception.Message);
        Assert.Contains("403 Forbidden", exception.Message);
    }
}
