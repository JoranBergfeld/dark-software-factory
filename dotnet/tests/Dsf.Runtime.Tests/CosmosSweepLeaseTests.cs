using System.Text.Json;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Product-wide ownership must last until the worker ends, independently of
/// window boundaries, cadence changes and process restarts.
/// </summary>
public sealed class CosmosSweepLeaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:37Z");

    [Fact]
    public async Task FirstAttemptForAWindowAcquiresTheLease()
    {
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", new SweepLeaseTestGateway());

        await using var acquired = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.NotNull(acquired);
    }

    [Fact]
    public async Task SecondConcurrentAttemptForTheSameWindowLoses()
    {
        var gateway = new SweepLeaseTestGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        await using var first = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        await using var second = await lease.TryAcquireAsync("acme", Now.AddSeconds(5), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(gateway.Creates);
    }

    [Fact]
    public async Task ANewWindowCannotOverlapAnUnfinishedSweep()
    {
        var gateway = new SweepLeaseTestGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        await using var first = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        await using var second = await lease.TryAcquireAsync("acme", Now.AddSeconds(61), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(gateway.Creates);
    }

    [Fact]
    public async Task ARestartWithADifferentCadenceCannotOverlapAnUnfinishedSweep()
    {
        var gateway = new SweepLeaseTestGateway();
        var oldProcess = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);
        var restartedProcess = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        await using var first = await oldProcess.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        await using var second = await restartedProcess.TryAcquireAsync(
            "acme", Now.AddHours(1), TimeSpan.FromSeconds(7), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(gateway.Creates);
    }

    [Fact]
    public async Task DifferentProductsInTheSameWindowEachAcquireTheirOwnLease()
    {
        var gateway = new SweepLeaseTestGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        await using var acme = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        await using var globex = await lease.TryAcquireAsync("globex", Now, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.NotNull(acme);
        Assert.NotNull(globex);
    }

    [Fact]
    public async Task Completed_work_releases_ownership_and_old_disposal_cannot_release_a_new_worker()
    {
        var gateway = new SweepLeaseTestGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);
        var first = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.NotNull(first);

        await first.DisposeAsync();
        await using var second = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(7), CancellationToken.None);
        Assert.NotNull(second);
        await first.DisposeAsync();
        await using var overlapping = await lease.TryAcquireAsync(
            "acme", Now.AddDays(1), TimeSpan.FromSeconds(300), CancellationToken.None);

        Assert.Null(overlapping);
        Assert.Single(gateway.Deletes);
    }

    [Fact]
    public async Task Ownership_explicitly_disables_container_TTL_and_releases_after_caller_cancellation()
    {
        var gateway = new SweepLeaseTestGateway();
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);
        using var cancellation = new CancellationTokenSource();
        var acquired = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(1), cancellation.Token);
        Assert.NotNull(acquired);
        using var document = JsonDocument.Parse(Assert.Single(gateway.Creates).Json);

        Assert.Equal(-1, document.RootElement.GetProperty("ttl").GetInt32());
        cancellation.Cancel();
        await acquired.DisposeAsync();

        Assert.Single(gateway.Deletes);
        await using var next = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Failed_release_is_reported_and_does_not_unlock_unconfirmed_ownership()
    {
        var gateway = new SweepLeaseTestGateway { DeleteFailure = new HttpRequestException("503 unavailable") };
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);
        var acquired = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(acquired);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => acquired.DisposeAsync().AsTask());
        await using var overlapping = await lease.TryAcquireAsync(
            "acme", Now.AddDays(1), TimeSpan.FromSeconds(7), CancellationToken.None);

        Assert.Contains("503", exception.Message);
        Assert.Null(overlapping);
    }

    [Fact]
    public async Task Ambiguous_creation_failure_fails_closed_across_restarts()
    {
        var gateway = new SweepLeaseTestGateway { CreateFailureAfterCommit = new HttpRequestException("response lost") };
        var lease = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(1), CancellationToken.None));
        var restarted = new CosmosSweepLease("https://cosmos.example", "dsf", "runs", gateway);
        await using var overlapping = await restarted.TryAcquireAsync(
            "acme", Now.AddDays(1), TimeSpan.FromSeconds(300), CancellationToken.None);

        Assert.Null(overlapping);
    }

    [Fact]
    public async Task Acquisition_wait_is_bounded_even_if_a_gateway_ignores_cancellation()
    {
        var gateway = new SweepLeaseTestGateway { HangCreates = true };
        var lease = new CosmosSweepLease(
            "https://cosmos.example", "dsf", "runs", gateway, TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.TryAcquireAsync(
            "acme", Now, TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Cleanup_wait_is_bounded_without_releasing_unconfirmed_ownership()
    {
        var gateway = new SweepLeaseTestGateway { HangReads = true };
        var lease = new CosmosSweepLease(
            "https://cosmos.example", "dsf", "runs", gateway, TimeSpan.FromMilliseconds(25));
        var owner = await lease.TryAcquireAsync("acme", Now, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(owner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await using var overlapping = await lease.TryAcquireAsync(
            "acme", Now.AddDays(1), TimeSpan.FromSeconds(300), CancellationToken.None);

        Assert.Null(overlapping);
        Assert.Empty(gateway.Deletes);
    }

    [Theory]
    [InlineData(null, null, "owner-db", "owner-runs")]
    [InlineData("", "", "owner-db", "owner-runs")]
    [InlineData(" \t ", " \t ", "owner-db", "owner-runs")]
    [InlineData(" env-db ", " env-runs ", "env-db", "env-runs")]
    [InlineData(" env-db ", null, "env-db", "owner-runs")]
    [InlineData(null, " env-runs ", "owner-db", "env-runs")]
    public async Task Lease_composition_prefers_nonblank_environment_then_owner_index(
        string? databaseOverride, string? containerOverride, string expectedDatabase, string expectedContainer)
    {
        var settings = LeaseSettings() with
        {
            IntegrationSettings = new Dictionary<string, string?>
            {
                [RuntimeIntegrationSettings.CosmosDatabase] = " owner-db ",
                [RuntimeIntegrationSettings.CosmosContainer] = " owner-runs ",
            },
        };
        var env = new Dictionary<string, string?>
        {
            [RuntimeIntegrationSettings.CosmosDatabase] = databaseOverride,
            [RuntimeIntegrationSettings.CosmosContainer] = containerOverride,
        };
        var gateway = new SweepLeaseTestGateway();
        var lease = RuntimeVerbs.BuildSweepLease(settings, env, gateway);

        await using var owner = await lease.TryAcquireAsync(
            settings.Product, Now, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(owner);
        var request = Assert.Single(gateway.Creates);
        Assert.Equal("https://cosmos.example", request.Endpoint);
        Assert.Equal(settings.Product, request.PartitionKey);
        Assert.Equal(expectedDatabase, request.Database);
        Assert.Equal(expectedContainer, request.Container);
    }

    [Fact]
    public async Task Lease_composition_with_no_environment_uses_owner_index()
    {
        var settings = LeaseSettings() with
        {
            IntegrationSettings = new Dictionary<string, string?>
            {
                [RuntimeIntegrationSettings.CosmosDatabase] = "product",
                [RuntimeIntegrationSettings.CosmosContainer] = "indexed-runs",
            },
        };
        var gateway = new SweepLeaseTestGateway();
        var lease = RuntimeVerbs.BuildSweepLease(settings, null, gateway);

        await using var owner = await lease.TryAcquireAsync(
            settings.Product, Now, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(owner);
        var request = Assert.Single(gateway.Creates);
        Assert.Equal("product", request.Database);
        Assert.Equal("indexed-runs", request.Container);
    }

    [Fact]
    public async Task Lease_composition_without_either_override_preserves_runtime_defaults()
    {
        var gateway = new SweepLeaseTestGateway();
        var lease = RuntimeVerbs.BuildSweepLease(LeaseSettings(), null, gateway);

        await using var owner = await lease.TryAcquireAsync(
            "acme", Now, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(owner);
        var request = Assert.Single(gateway.Creates);
        Assert.Equal(RuntimeIntegrationSettings.DefaultCosmosDatabase, request.Database);
        Assert.Equal(RuntimeIntegrationSettings.DefaultCosmosContainer, request.Container);
    }

    private static RuntimeSettings LeaseSettings() => new(
        "acme", "https://appconfig.example", "", "", " https://cosmos.example ",
        "https://openai.example", "gpt", "embed", "", "", "", "");
}
