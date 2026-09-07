using System.Collections.Concurrent;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The sweep lease buckets a tick to its interval-wide window and delegates
/// straight to <see cref="ICosmosDocumentGateway.CreateIfAbsentAsync"/> -- the
/// same atomic-create primitive the run store already uses -- so two attempts
/// racing to sweep the very same window can never both proceed: exactly one
/// creates the lease document and wins.
/// </summary>
public sealed class CosmosSweepLeaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:37Z");

    [Fact]
    public async Task FirstAttemptForAWindowAcquiresTheLease()
    {
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", new AtomicCosmosGateway());

        var acquired = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(acquired);
    }

    [Fact]
    public async Task SecondConcurrentAttemptForTheSameWindowLoses()
    {
        var gateway = new AtomicCosmosGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        var first = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        var second = await lease.TryAcquireAsync("acme", Now.AddSeconds(5), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Single(gateway.Creates);
    }

    [Fact]
    public async Task ANewWindowAcquiresANewLease()
    {
        var gateway = new AtomicCosmosGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        var first = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        var second = await lease.TryAcquireAsync("acme", Now.AddSeconds(61), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, gateway.Creates.Count);
    }

    [Fact]
    public async Task DifferentProductsInTheSameWindowEachAcquireTheirOwnLease()
    {
        var gateway = new AtomicCosmosGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        var acme = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        var globex = await lease.TryAcquireAsync("globex", Now, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(acme);
        Assert.True(globex);
    }

    /// <summary>
    /// A gateway whose <see cref="ICosmosDocumentGateway.CreateIfAbsentAsync"/> is
    /// truly atomic, exactly like a real Cosmos unconditional create racing on the
    /// same document id: only the first of any concurrent attempt ever succeeds.
    /// </summary>
    private sealed class AtomicCosmosGateway : ICosmosDocumentGateway
    {
        private readonly ConcurrentDictionary<(string PartitionKey, string Id), string> documents = new();

        public List<(string PartitionKey, string Id, string Json)> Creates { get; } = [];

        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the sweep lease must never call Upsert: it must create-if-absent.");

        public Task<string?> ReadAsync(
            string endpoint, string database, string container, string partitionKey, string id,
            CancellationToken cancellationToken) =>
            Task.FromResult(documents.TryGetValue((partitionKey, id), out var json) ? json : null);

        public Task<bool> CreateIfAbsentAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken)
        {
            var created = documents.TryAdd((partitionKey, id), json);
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
}
