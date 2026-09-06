using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The typed <c>foundryiq</c> integration reads real evidence from a FoundryIQ
/// knowledge base -- not the generic JSON-shape-guessing fallback -- and
/// reports missing configuration by the exact setting name, never with
/// silently empty evidence.
/// </summary>
public sealed class FoundryIqIntegrationTests
{
    private static readonly Dictionary<string, string?> FullyConfigured = new()
    {
        ["DSF_FOUNDRYIQ_PROJECT_ENDPOINT"] = "https://acme-foundry.services.ai.azure.com/api/projects/acme",
        ["DSF_FOUNDRYIQ_KNOWLEDGE_BASE"] = "roadmap",
        ["DSF_FOUNDRYIQ_QUERY"] = "checkout latency regressions",
    };

    [Fact]
    public async Task Gather_maps_knowledge_base_results_onto_evidence()
    {
        var gateway = new ScriptedFoundryIqKnowledgeGateway(
            new FoundryIqResult("FIQ-1", "checkout 500s spiked", null),
            new FoundryIqResult("FIQ-2", null, "same trace, second event"));
        var integration = new FoundryIqIntegration(FullyConfigured, gateway);

        var evidence = await integration.GatherAsync("foundryiq", "acme", CancellationToken.None);

        Assert.Equal(2, evidence.Count);
        Assert.Equal("FIQ-1", evidence[0].Reference);
        Assert.Equal("checkout 500s spiked", evidence[0].Summary);
        Assert.Equal("foundryiq", evidence[0].SourceKind);
        // A result with no title falls back to its content.
        Assert.Equal("same trace, second event", evidence[1].Summary);
        Assert.Equal("https://acme-foundry.services.ai.azure.com/api/projects/acme", gateway.RequestedProjectEndpoint);
        Assert.Equal("roadmap", gateway.RequestedKnowledgeBase);
        Assert.Equal("checkout latency regressions", gateway.RequestedQuery);
    }

    [Fact]
    public async Task Gather_drops_results_with_no_id()
    {
        var gateway = new ScriptedFoundryIqKnowledgeGateway(new FoundryIqResult(string.Empty, "no id here", null));
        var integration = new FoundryIqIntegration(FullyConfigured, gateway);

        var evidence = await integration.GatherAsync("foundryiq", "acme", CancellationToken.None);

        Assert.Empty(evidence);
    }

    [Fact]
    public async Task Gather_without_a_configured_project_endpoint_names_the_unset_setting()
    {
        var env = new Dictionary<string, string?>(FullyConfigured) { ["DSF_FOUNDRYIQ_PROJECT_ENDPOINT"] = "" };
        var integration = new FoundryIqIntegration(env, new ScriptedFoundryIqKnowledgeGateway());

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => integration.GatherAsync("foundryiq", "acme", CancellationToken.None));

        Assert.Contains("DSF_FOUNDRYIQ_PROJECT_ENDPOINT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_without_a_configured_knowledge_base_names_the_unset_setting()
    {
        var env = new Dictionary<string, string?>(FullyConfigured) { ["DSF_FOUNDRYIQ_KNOWLEDGE_BASE"] = "" };
        var integration = new FoundryIqIntegration(env, new ScriptedFoundryIqKnowledgeGateway());

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => integration.GatherAsync("foundryiq", "acme", CancellationToken.None));

        Assert.Contains("DSF_FOUNDRYIQ_KNOWLEDGE_BASE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_without_a_configured_query_names_the_unset_setting()
    {
        var env = new Dictionary<string, string?>(FullyConfigured) { ["DSF_FOUNDRYIQ_QUERY"] = "" };
        var integration = new FoundryIqIntegration(env, new ScriptedFoundryIqKnowledgeGateway());

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => integration.GatherAsync("foundryiq", "acme", CancellationToken.None));

        Assert.Contains("DSF_FOUNDRYIQ_QUERY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_reports_an_unreachable_project_instead_of_empty_evidence()
    {
        var integration = new FoundryIqIntegration(
            FullyConfigured, new UnreachableFoundryIqKnowledgeGateway("project not found"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => integration.GatherAsync("foundryiq", "acme", CancellationToken.None));

        Assert.Contains("project not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_dependencies_resolve_foundryiq_to_the_typed_integration()
    {
        var registry = RuntimeDependencies.Production(new Dictionary<string, string?>()).SourceIntegrationRegistry;

        Assert.IsType<FoundryIqIntegration>(registry.Resolve("foundryiq"));
        Assert.IsType<FoundryIqIntegration>(registry.Resolve("FOUNDRYIQ"));
        Assert.IsType<HttpSourceIntegration>(registry.Resolve("webiq"));
    }
}
