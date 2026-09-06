using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The typed <c>azuremonitor</c> integration reads real evidence from a Log
/// Analytics workspace via a KQL query -- not the generic JSON-shape-guessing
/// fallback -- and reports missing configuration by the exact setting name,
/// never with silently empty evidence.
/// </summary>
public sealed class AzureMonitorIntegrationTests
{
    private static Dictionary<string, string> Row(string reference, string summary) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Reference"] = reference, ["Summary"] = summary };

    [Fact]
    public async Task Gather_maps_workspace_rows_onto_evidence()
    {
        var gateway = new ScriptedAzureMonitorLogsGateway(
            Row("AM-1", "checkout 500s spiked"), Row("AM-2", "same trace, second event"));
        var env = new Dictionary<string, string?>
        {
            ["DSF_AZUREMONITOR_WORKSPACE_ID"] = "workspace-1",
            ["DSF_AZUREMONITOR_QUERY"] = "AppExceptions | take 10",
        };
        var integration = new AzureMonitorIntegration(env, gateway);

        var evidence = await integration.GatherAsync("azuremonitor", "acme", CancellationToken.None);

        Assert.Equal(2, evidence.Count);
        Assert.Equal("AM-1", evidence[0].Reference);
        Assert.Equal("checkout 500s spiked", evidence[0].Summary);
        Assert.Equal("azuremonitor", evidence[0].SourceKind);
        Assert.Equal("workspace-1", gateway.RequestedWorkspaceId);
        Assert.Equal("AppExceptions | take 10", gateway.RequestedQuery);
    }

    [Fact]
    public async Task Gather_maps_alternate_column_names()
    {
        var gateway = new ScriptedAzureMonitorLogsGateway(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["_ItemId"] = "AM-9",
                ["OperationName"] = "queue backed up",
            });
        var env = new Dictionary<string, string?>
        {
            ["DSF_AZUREMONITOR_WORKSPACE_ID"] = "workspace-1",
            ["DSF_AZUREMONITOR_QUERY"] = "AppTraces | take 10",
        };
        var integration = new AzureMonitorIntegration(env, gateway);

        var evidence = await integration.GatherAsync("azuremonitor", "acme", CancellationToken.None);

        var item = Assert.Single(evidence);
        Assert.Equal("AM-9", item.Reference);
        Assert.Equal("queue backed up", item.Summary);
    }

    [Fact]
    public async Task Gather_drops_rows_with_no_reference_column()
    {
        var gateway = new ScriptedAzureMonitorLogsGateway(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Summary"] = "no id here" });
        var env = new Dictionary<string, string?>
        {
            ["DSF_AZUREMONITOR_WORKSPACE_ID"] = "workspace-1",
            ["DSF_AZUREMONITOR_QUERY"] = "AppTraces | take 10",
        };
        var integration = new AzureMonitorIntegration(env, gateway);

        var evidence = await integration.GatherAsync("azuremonitor", "acme", CancellationToken.None);

        Assert.Empty(evidence);
    }

    [Fact]
    public async Task Gather_without_a_configured_workspace_names_the_unset_setting()
    {
        var integration = new AzureMonitorIntegration(
            new Dictionary<string, string?> { ["DSF_AZUREMONITOR_QUERY"] = "AppTraces | take 10" },
            new ScriptedAzureMonitorLogsGateway());

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => integration.GatherAsync("azuremonitor", "acme", CancellationToken.None));

        Assert.Contains("DSF_AZUREMONITOR_WORKSPACE_ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_without_a_configured_query_names_the_unset_setting()
    {
        var integration = new AzureMonitorIntegration(
            new Dictionary<string, string?> { ["DSF_AZUREMONITOR_WORKSPACE_ID"] = "workspace-1" },
            new ScriptedAzureMonitorLogsGateway());

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => integration.GatherAsync("azuremonitor", "acme", CancellationToken.None));

        Assert.Contains("DSF_AZUREMONITOR_QUERY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_reports_an_unreachable_workspace_instead_of_empty_evidence()
    {
        var integration = new AzureMonitorIntegration(
            new Dictionary<string, string?>
            {
                ["DSF_AZUREMONITOR_WORKSPACE_ID"] = "workspace-1",
                ["DSF_AZUREMONITOR_QUERY"] = "AppTraces | take 10",
            },
            new UnreachableAzureMonitorLogsGateway("workspace not found"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => integration.GatherAsync("azuremonitor", "acme", CancellationToken.None));

        Assert.Contains("workspace not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_dependencies_resolve_azuremonitor_to_the_typed_integration()
    {
        var registry = RuntimeDependencies.Production(new Dictionary<string, string?>()).SourceIntegrationRegistry;

        Assert.IsType<AzureMonitorIntegration>(registry.Resolve("azuremonitor"));
        Assert.IsType<AzureMonitorIntegration>(registry.Resolve("AZUREMONITOR"));
        Assert.IsType<HttpSourceIntegration>(registry.Resolve("foundryiq"));
    }
}
