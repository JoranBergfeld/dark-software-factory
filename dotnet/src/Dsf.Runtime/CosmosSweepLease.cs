using System.Text.Json;

namespace Dsf.Runtime;

/// <summary>
/// Guards a scheduled sweep attempt against running the conveyor concurrently
/// with another attempt for the same product and the same tick window -- e.g. an
/// old and new process instance briefly overlapping during a rolling redeploy, or
/// a restart racing an in-flight tick. Exactly one attempt for a given window
/// acquires the lease and proceeds; every other attempt observes the conflict and
/// no-ops instead of starting a duplicate run.
/// </summary>
public interface ISweepLease
{
    /// <summary>
    /// Attempts to acquire the lease for the tick window <paramref name="now"/>
    /// falls in (windows are <paramref name="interval"/> wide, bucketed from the
    /// Unix epoch so two processes agree on window boundaries without
    /// coordinating clocks beyond ordinary time sync). Returns <c>true</c> only
    /// for the single attempt that wins the window.
    /// </summary>
    Task<bool> TryAcquireAsync(
        string product, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken);
}

/// <summary>
/// The real lease: built on <see cref="ICosmosDocumentGateway.CreateIfAbsentAsync"/>,
/// the same atomic-create primitive <see cref="CosmosRunStore"/> already uses for
/// idempotent run creation. The lease document's id encodes the product and the
/// bucketed window start, so two attempts racing for the very same window can
/// never both create it: exactly one succeeds, the other is told a document
/// already exists there.
/// </summary>
internal sealed class CosmosSweepLease(
    string endpoint, string database, string container, ICosmosDocumentGateway gateway) : ISweepLease
{
    public async Task<bool> TryAcquireAsync(
        string product, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(product);

        var bucketSeconds = Math.Max(1L, (long)interval.TotalSeconds);
        var windowStart = now.ToUnixTimeSeconds() / bucketSeconds * bucketSeconds;
        var id = $"sweep-lease-{windowStart}";
        var json = JsonSerializer.Serialize(new
        {
            id,
            product,
            windowStart,
            acquiredAt = DateTimeOffset.UtcNow,
        });

        return await gateway.CreateIfAbsentAsync(
            endpoint, database, container, partitionKey: product, id, json, cancellationToken);
    }
}
