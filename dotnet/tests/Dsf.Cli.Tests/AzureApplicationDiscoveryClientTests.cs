using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

/// <summary>
/// The discovery adapter must stay read-only and must not turn missing tag metadata into
/// an "unclaimed" resource. These tests verify the exact <c>az</c> argument shape.
/// </summary>
public sealed class AzureApplicationDiscoveryClientTests
{
    private const string Account = """
        {"tenantId":"tenant-1","id":"sub-1","name":"shop-production","user":{"name":"operator@example.com"}}
        """;

    [Fact]
    public async Task Signed_in_context_reads_the_operator_azure_cli_identity()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, Account, ""));
        var client = new AzureCliApplicationDiscoveryClient(runner);

        var identity = await client.GetSignedInContextAsync(CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("tenant-1", identity.TenantId);
        Assert.Equal("sub-1", identity.SubscriptionId);
        Assert.Equal("operator@example.com", identity.User);
        Assert.Equal(["account", "show", "-o", "json"], Assert.Single(runner.Invocations));
    }

    [Fact]
    public async Task Signed_out_azure_cli_yields_no_identity_instead_of_a_fabricated_one()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(1, "", "Please run 'az login'"));
        var client = new AzureCliApplicationDiscoveryClient(runner);

        Assert.Null(await client.GetSignedInContextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Discovery_lists_resources_and_backends_without_any_mutating_invocation()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(
            0,
            """
            [
              {"id":"/subscriptions/sub-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/api",
               "name":"api","type":"Microsoft.Web/sites","resourceGroup":"rg-a",
               "tags":{"environment":"production"}},
              {"id":"/subscriptions/sub-1/resourceGroups/rg-b/providers/Microsoft.Web/sites/shared",
               "name":"shared","type":"Microsoft.Web/sites","resourceGroup":"rg-b",
               "tags":{"environment":"production","dsf-shared":"true","dsf-product":"other"}},
              {"id":"/subscriptions/sub-1/resourceGroups/rg-c/providers/Microsoft.OperationalInsights/workspaces/law",
               "name":"law","type":"microsoft.operationalinsights/workspaces","resourceGroup":"rg-c",
               "tags":{"environment":"production"}},
              {"id":"/subscriptions/sub-1/resourceGroups/rg-a/providers/Microsoft.Web/sites/opaque",
               "name":"opaque","type":"Microsoft.Web/sites","resourceGroup":"rg-a"}
            ]
            """,
            ""));
        var client = new AzureCliApplicationDiscoveryClient(runner);

        var inventory = await client.DiscoverAsync("tenant-1", "sub-1", CancellationToken.None);

        Assert.Equal(
            ["resource", "list", "--subscription", "sub-1", "-o", "json"],
            Assert.Single(runner.Invocations));
        Assert.Equal(3, inventory.Resources.Count);
        var shared = inventory.Resources.Single(resource => resource.Name == "shared");
        Assert.True(shared.Shared);
        Assert.Equal("other", shared.ExistingClaimSignal);

        // No tag collection at all is unreadable metadata, not proof of an unclaimed resource.
        Assert.True(inventory.Resources.Single(resource => resource.Name == "opaque").MetadataUnreadable);
        Assert.False(inventory.Resources.Single(resource => resource.Name == "api").MetadataUnreadable);

        var backend = Assert.Single(inventory.EvidenceBackends);
        Assert.Equal("law", backend.Name);
        Assert.Equal("loganalytics", backend.Kind);
        Assert.Equal("production", backend.Environment);
    }

    [Fact]
    public async Task Discovery_failure_fails_loudly_with_the_provider_error()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(1, "", "AuthorizationFailed"));
        var client = new AzureCliApplicationDiscoveryClient(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DiscoverAsync("tenant-1", "sub-1", CancellationToken.None));

        Assert.Contains("AuthorizationFailed", error.Message, StringComparison.Ordinal);
    }
}
