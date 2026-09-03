using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The production run store persists each checkpoint as a document in the
/// product's Cosmos account, partitioned by product, and reports a write it could
/// not make instead of losing the run's record. It also reads that same document
/// back by run id so a resumed run can find and continue what a prior process
/// already persisted, rather than starting over blind to its own checkpoints.
/// </summary>
public sealed class CosmosRunStoreTests
{
    private sealed class RecordingCosmosGateway : ICosmosDocumentGateway
    {
        public List<(string Endpoint, string Database, string Container, string PartitionKey, string Id, string Json)>
            Upserts { get; } = [];

        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken)
        {
            Upserts.Add((endpoint, database, container, partitionKey, id, json));
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(
            string endpoint, string database, string container, string partitionKey, string id,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
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

    /// <summary>A gateway that answers reads from a fixed document store, keyed by id.</summary>
    private sealed class ScriptedReadGateway(IReadOnlyDictionary<string, string> documents) : ICosmosDocumentGateway
    {
        public List<(string Endpoint, string Database, string Container, string PartitionKey, string Id)> Reads { get; } = [];

        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not expected to upsert in a read test");

        public Task<string?> ReadAsync(
            string endpoint, string database, string container, string partitionKey, string id,
            CancellationToken cancellationToken)
        {
            Reads.Add((endpoint, database, container, partitionKey, id));
            return Task.FromResult(documents.TryGetValue(id, out var json) ? json : null);
        }
    }

    [Fact]
    public async Task Saving_a_checkpoint_upserts_the_run_document_partitioned_by_product()
    {
        var gateway = new RecordingCosmosGateway();
        var store = new CosmosRunStore("https://cosmos.example", "dsf", "runs", "acme", gateway);
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"] };
        run.Record("s1_triage", "triaged");

        await store.SaveAsync(run, "s1_triage", CancellationToken.None);

        var upsert = Assert.Single(gateway.Upserts);
        Assert.Equal("https://cosmos.example", upsert.Endpoint);
        Assert.Equal("acme", upsert.PartitionKey);
        Assert.Equal(run.Id, upsert.Id);
        using var document = JsonDocument.Parse(upsert.Json);
        Assert.Equal("s1_triage", document.RootElement.GetProperty("station").GetString());
        Assert.Equal("acme", document.RootElement.GetProperty("product").GetString());
        Assert.Equal("open", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_rejected_write_is_reported_with_the_store_it_could_not_write_to()
    {
        var store = new CosmosRunStore("https://cosmos.example", "dsf", "runs", "acme", new FailingCosmosGateway());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(new ConveyorRun(), "s1_triage", CancellationToken.None));

        Assert.Contains("https://cosmos.example", exception.Message);
        Assert.Contains("403 Forbidden", exception.Message);
    }

    [Fact]
    public async Task Loading_a_run_reads_the_document_by_id_and_partition_key()
    {
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"], DryRun = true };
        run.Checkpoints.AddRange(["s1_triage", "s2_investigation"]);
        run.Evidence.Add(new EvidenceItem("sentry", "SENTRY-1", "checkout 500s spiked"));
        run.Fingerprint = "abc123";
        run.Record("s2_investigation", "gathered 1 item");
        var gateway = new RecordingCosmosGateway();
        var writeStore = new CosmosRunStore("https://cosmos.example", "dsf", "runs", "acme", gateway);
        await writeStore.SaveAsync(run, "s2_investigation", CancellationToken.None);
        var document = gateway.Upserts[^1].Json;
        var readGateway = new ScriptedReadGateway(new Dictionary<string, string> { [run.Id] = document });
        var store = new CosmosRunStore("https://cosmos.example", "dsf", "runs", "acme", readGateway);

        var loaded = await store.LoadAsync(run.Id, CancellationToken.None);

        var read = Assert.Single(readGateway.Reads);
        Assert.Equal("https://cosmos.example", read.Endpoint);
        Assert.Equal("dsf", read.Database);
        Assert.Equal("runs", read.Container);
        Assert.Equal("acme", read.PartitionKey);
        Assert.Equal(run.Id, read.Id);
        Assert.NotNull(loaded);
        Assert.Equal(run.Id, loaded!.Id);
        Assert.Equal(TriggerKind.Signal, loaded.Trigger);
        Assert.True(loaded.DryRun);
        Assert.Equal(["acme"], loaded.ProductHints);
        Assert.Equal(["sentry"], loaded.SourceKinds);
        Assert.Equal("abc123", loaded.Fingerprint);
        Assert.Equal(["s1_triage", "s2_investigation"], loaded.Checkpoints);
        var evidence = Assert.Single(loaded.Evidence);
        Assert.Equal("sentry", evidence.SourceKind);
        Assert.Equal("SENTRY-1", evidence.Reference);
        Assert.Equal("checkout 500s spiked", evidence.Summary);
        Assert.Contains(loaded.Audit, record => record.Station == "s2_investigation" && record.Message == "gathered 1 item");
    }

    [Fact]
    public async Task Loading_a_run_that_was_never_persisted_returns_null()
    {
        var store = new CosmosRunStore(
            "https://cosmos.example", "dsf", "runs", "acme", new ScriptedReadGateway(new Dictionary<string, string>()));

        var loaded = await store.LoadAsync("no-such-run", CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task A_rejected_read_is_reported_with_the_store_it_could_not_read_from()
    {
        var store = new CosmosRunStore("https://cosmos.example", "dsf", "runs", "acme", new FailingCosmosGateway());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.LoadAsync("some-run", CancellationToken.None));

        Assert.Contains("https://cosmos.example", exception.Message);
        Assert.Contains("403 Forbidden", exception.Message);
    }
}
