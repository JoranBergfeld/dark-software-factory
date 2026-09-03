using Dsf.ControlCenter;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.ControlCenter.Tests;

/// <summary>
/// The Control Center governs through the same App Configuration authority
/// <c>dsf new</c> provisions and the runtime reads: the owner index lists the
/// products, and each product's own store carries its effective policy.
/// </summary>
public sealed class ProductPolicyAuthorityTests
{
    private const string OwnerEndpoint = "https://owner.azconfig.io";
    private const string ProductEndpoint = "https://wayfinder.azconfig.io";

    private static ScriptedConfigurationStore SeededStore()
    {
        var store = new ScriptedConfigurationStore();
        store.Seed(OwnerEndpoint, "GITHUB_REPOSITORY", "acme/wayfinder", "wayfinder");
        store.Seed(OwnerEndpoint, "AZURE_APPCONFIG_ENDPOINT", ProductEndpoint, "wayfinder");
        store.Seed(OwnerEndpoint, "GITHUB_REPOSITORY", "acme/atlas", "atlas");
        store.Seed(OwnerEndpoint, "AZURE_APPCONFIG_ENDPOINT", "https://atlas.azconfig.io", "atlas");
        return store;
    }

    private static AppConfigurationProductPolicyAuthority Authority(ScriptedConfigurationStore store) =>
        new(store, OwnerEndpoint);

    [Fact]
    public async Task Lists_every_product_in_the_owner_index()
    {
        var products = await Authority(SeededStore()).ListProductsAsync(CancellationToken.None);

        Assert.Equal(["atlas", "wayfinder"], products.Select(p => p.Key));
        Assert.Equal("acme/wayfinder", products.Single(p => p.Key == "wayfinder").GitHubRepository);
        Assert.Equal(ProductEndpoint, products.Single(p => p.Key == "wayfinder").AppConfigEndpoint);
    }

    [Fact]
    public async Task Incomplete_owner_index_entries_fail_loudly()
    {
        var store = new ScriptedConfigurationStore();
        store.Seed(OwnerEndpoint, "GITHUB_REPOSITORY", "acme/wayfinder", "wayfinder");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Authority(store).ListProductsAsync(CancellationToken.None));

        Assert.Contains("AZURE_APPCONFIG_ENDPOINT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_authority_fails_loudly_naming_the_store()
    {
        var store = SeededStore();
        store.ListFailure = new InvalidOperationException("network down");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Authority(store).ListProductsAsync(CancellationToken.None));

        Assert.Contains(OwnerEndpoint, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_effective_policy_with_product_labels_overriding_defaults()
    {
        var store = SeededStore();
        store.Seed(ProductEndpoint, "agents.sentry.enabled", "true");
        store.Seed(ProductEndpoint, "agents.grafana.enabled", "true");
        store.Seed(ProductEndpoint, "agents.grafana.enabled", "false", "wayfinder");
        store.Seed(ProductEndpoint, "threshold.wayfinder", "0.72");

        var policy = await Authority(store).ReadPolicyAsync("wayfinder", CancellationToken.None);

        Assert.Equal("acme/wayfinder", policy.GitHubRepository);
        Assert.True(policy.AgentEnablement["sentry"]);
        Assert.False(policy.AgentEnablement["grafana"]);
        Assert.Equal(0.72d, policy.ConfidenceThreshold);
        Assert.Equal(SourceAgentKinds.Known.Order(StringComparer.Ordinal), policy.AgentEnablement.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Unset_agents_and_threshold_read_as_the_documented_defaults()
    {
        var policy = await Authority(SeededStore()).ReadPolicyAsync("wayfinder", CancellationToken.None);

        Assert.All(policy.AgentEnablement.Values, Assert.False);
        Assert.Equal(0.6d, policy.ConfidenceThreshold);
    }

    [Fact]
    public async Task Unknown_product_fails_loudly()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Authority(SeededStore()).ReadPolicyAsync("ghost", CancellationToken.None));

        Assert.Contains("ghost", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_writes_land_on_the_product_label_in_the_product_store()
    {
        var store = SeededStore();

        await Authority(store).SetAgentEnabledAsync("wayfinder", "sentry", true, CancellationToken.None);

        var write = Assert.Single(store.Writes);
        Assert.Equal((ProductEndpoint, "agents.sentry.enabled", "true", "wayfinder"), write);
    }

    [Fact]
    public async Task Threshold_writes_use_the_product_record_key_and_invariant_text()
    {
        var store = SeededStore();

        await Authority(store).SetConfidenceThresholdAsync("wayfinder", 0.85d, CancellationToken.None);

        var write = Assert.Single(store.Writes);
        Assert.Equal((ProductEndpoint, "threshold.wayfinder", "0.85", (string?)null), write);
    }

    [Fact]
    public async Task Unknown_agent_kinds_are_rejected_before_any_write()
    {
        var store = SeededStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Authority(store).SetAgentEnabledAsync("wayfinder", "pagerduty", true, CancellationToken.None));

        Assert.Empty(store.Writes);
    }
}
