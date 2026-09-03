using System.Text.Json;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The production run store persists each checkpoint as a document in the
/// product's Cosmos account, partitioned by product, and reports a write it could
/// not make instead of losing the run's record.
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
    }

    private sealed class FailingCosmosGateway : ICosmosDocumentGateway
    {
        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("403 Forbidden");
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
}
