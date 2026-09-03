using System.Collections.Concurrent;
using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The production learning store persists one audited record per (intent key,
/// verdict) pair, partitioned by product, atomically: recording delegates
/// straight to the gateway's create-if-absent semantics (no separate
/// read-then-write from the store itself), so two concurrent polls racing to
/// record the same outcome can never both succeed and duplicate the audit trail
/// -- exactly one is recorded, the other is reported as already recorded.
/// </summary>
public sealed class CosmosLearningStoreTests
{
    /// <summary>
    /// A gateway whose <see cref="ICosmosDocumentGateway.CreateIfAbsentAsync"/> is
    /// truly atomic (backed by <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>),
    /// exactly like a real Cosmos unconditional create racing on the same document
    /// id: only the first of any concurrent attempt ever succeeds.
    /// </summary>
    private sealed class AtomicCosmosGateway : ICosmosDocumentGateway
    {
        private readonly ConcurrentDictionary<string, string> documents = new();

        public List<(string PartitionKey, string Id, string Json)> Creates { get; } = [];

        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the learning store must never call Upsert: it must create-if-absent.");

        public Task<string?> ReadAsync(
            string endpoint, string database, string container, string partitionKey, string id,
            CancellationToken cancellationToken) =>
            Task.FromResult(documents.TryGetValue(id, out var json) ? json : null);

        public Task<bool> CreateIfAbsentAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken)
        {
            var created = documents.TryAdd(id, json);
            if (created)
            {
                lock (Creates)
                {
                    Creates.Add((partitionKey, id, json));
                }
            }

            return Task.FromResult(created);
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

        public Task<bool> CreateIfAbsentAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("403 Forbidden");
    }

    private static LearningRecord Record(string intentKey = "fingerprint-1:sentry", string verdict = "dsf-outcome:approved") =>
        new(intentKey, verdict, "https://github.com/acme/acme/issues/9", "[sentry] checkout 500s spiked", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Recording_a_new_outcome_creates_a_document_atomically_and_reports_it_was_newly_recorded()
    {
        var gateway = new AtomicCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);

        var recorded = await store.RecordAsync(Record(), CancellationToken.None);

        Assert.True(recorded);
        var create = Assert.Single(gateway.Creates);
        Assert.Equal("acme", create.PartitionKey);
        using var document = JsonDocument.Parse(create.Json);
        Assert.Equal("fingerprint-1:sentry", document.RootElement.GetProperty("intentKey").GetString());
        Assert.Equal("dsf-outcome:approved", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("acme", document.RootElement.GetProperty("product").GetString());
    }

    [Fact]
    public async Task Recording_the_same_intent_and_verdict_again_is_a_no_op_reported_as_already_recorded()
    {
        var gateway = new AtomicCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);
        await store.RecordAsync(Record(), CancellationToken.None);

        var recordedAgain = await store.RecordAsync(Record(), CancellationToken.None);

        Assert.False(recordedAgain);
        Assert.Single(gateway.Creates);
    }

    [Fact]
    public async Task A_different_verdict_for_the_same_intent_is_recorded_as_a_separate_document()
    {
        var gateway = new AtomicCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);
        await store.RecordAsync(Record(verdict: OutcomeLabels.Approved), CancellationToken.None);

        var recorded = await store.RecordAsync(Record(verdict: OutcomeLabels.Rejected), CancellationToken.None);

        Assert.True(recorded);
        Assert.Equal(2, gateway.Creates.Count);
    }

    [Fact]
    public async Task Two_concurrent_polls_racing_to_record_the_same_outcome_never_both_succeed()
    {
        var gateway = new AtomicCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);

        var results = await Task.WhenAll(
            store.RecordAsync(Record(), CancellationToken.None),
            store.RecordAsync(Record(), CancellationToken.None));

        // Exactly one of the two racing polls recorded the outcome; the other
        // is told it was already recorded rather than writing a duplicate.
        Assert.Single(results, recorded => recorded);
        Assert.Single(results, recorded => !recorded);
        Assert.Single(gateway.Creates);
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

    [Fact]
    public async Task Retrieving_lessons_for_an_intent_with_no_recorded_outcome_returns_no_lessons()
    {
        var gateway = new AtomicCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);

        var lessons = await store.RetrieveAsync("fingerprint-9:sentry", CancellationToken.None);

        Assert.Empty(lessons);
    }

    [Fact]
    public async Task Retrieving_lessons_for_an_intent_returns_every_verdict_recorded_for_it()
    {
        var gateway = new AtomicCosmosGateway();
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", gateway);
        await store.RecordAsync(Record(intentKey: "fingerprint-1:sentry", verdict: OutcomeLabels.Rejected), CancellationToken.None);
        // A different intent's outcome must never leak into this intent's lessons.
        await store.RecordAsync(Record(intentKey: "fingerprint-2:sentry", verdict: OutcomeLabels.Approved), CancellationToken.None);

        var lessons = await store.RetrieveAsync("fingerprint-1:sentry", CancellationToken.None);

        var lesson = Assert.Single(lessons);
        Assert.Equal("fingerprint-1:sentry", lesson.IntentKey);
        Assert.Equal(OutcomeLabels.Rejected, lesson.Verdict);
        Assert.Equal("https://github.com/acme/acme/issues/9", lesson.IssueUrl);
        Assert.Equal("[sentry] checkout 500s spiked", lesson.Title);
    }

    [Fact]
    public async Task A_rejected_read_for_lessons_is_reported_with_the_store_it_could_not_read_from()
    {
        var store = new CosmosLearningStore("https://cosmos.example", "dsf", "learning", "acme", new FailingCosmosGateway());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RetrieveAsync("fingerprint-1:sentry", CancellationToken.None));

        Assert.Contains("https://cosmos.example", exception.Message);
        Assert.Contains("403 Forbidden", exception.Message);
    }
}
