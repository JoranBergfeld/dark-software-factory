using System.Text.Json;

namespace Dsf.Runtime;

/// <summary>
/// Holds exclusive product-wide sweep ownership until the returned handle is
/// disposed, after the conveyor has actually stopped. A conflict returns null.
/// </summary>
public interface ISweepLease
{
    /// <summary>
    /// The attempt's time and cadence are diagnostic metadata, never ownership
    /// boundaries. A clock change, new interval or restart cannot steal a lease.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(
        string product, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken);
}

/// <summary>
/// A non-expiring Cosmos ownership document, acquired by atomic create and
/// released by owner-checked, ETag-conditional delete. Automatic expiry would let
/// another process run while an unresponsive worker still owns side effects.
/// After a crash, recovery must establish that the old worker has stopped before
/// removing its document; elapsed time alone is never proof.
/// </summary>
internal sealed class CosmosSweepLease(
    string endpoint, string database, string container, ICosmosDocumentGateway gateway,
    TimeSpan? operationTimeout = null) : ISweepLease
{
    private const string LeaseId = "sweep-lease";
    private readonly TimeSpan operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string product, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        cancellationToken.ThrowIfCancellationRequested();

        var ownerId = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(new
        {
            id = LeaseId,
            product,
            ownerId,
            acquiredAt = now,
            intervalSeconds = interval.TotalSeconds,
            ttl = -1,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout);
        var acquired = await gateway.CreateIfAbsentAsync(
            endpoint, database, container, product, LeaseId, json, timeout.Token).WaitAsync(timeout.Token);
        return acquired ? new Ownership(this, product, ownerId) : null;
    }

    private async Task ReleaseAsync(string product, string ownerId)
    {
        // Cleanup is independent of shutdown cancellation, but network waits are bounded.
        using var timeout = new CancellationTokenSource(operationTimeout);
        var json = await gateway.ReadAsync(
            endpoint, database, container, product, LeaseId, timeout.Token).WaitAsync(timeout.Token);
        if (json is null)
        {
            throw new InvalidOperationException($"Sweep lease for '{product}' disappeared before release.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("ownerId", out var owner) || owner.GetString() != ownerId
            || !root.TryGetProperty("_etag", out var etag) || string.IsNullOrWhiteSpace(etag.GetString()))
        {
            throw new InvalidOperationException(
                $"Cannot confirm ownership of the sweep lease for '{product}'; refusing to release it.");
        }

        if (!await gateway.DeleteIfMatchAsync(
                endpoint, database, container, product, LeaseId, etag.GetString()!, timeout.Token)
            .WaitAsync(timeout.Token))
        {
            throw new InvalidOperationException(
                $"Sweep lease for '{product}' changed during release; refusing to remove another owner's lease.");
        }
    }

    private sealed class Ownership(CosmosSweepLease lease, string product, string ownerId) : IAsyncDisposable
    {
        private readonly Lazy<Task> release = new(() => lease.ReleaseAsync(product, ownerId));

        public ValueTask DisposeAsync() => new(release.Value);
    }
}
