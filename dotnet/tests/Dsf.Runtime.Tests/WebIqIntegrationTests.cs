using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The typed <c>webiq</c> integration reads real evidence from a Microsoft
/// WebIQ web search (ADR 0020) -- not the generic JSON-shape-guessing fallback
/// -- resolves its API key the same way the Python runtime does (direct env
/// override, else Key Vault), and reports missing configuration by the exact
/// setting name, never with silently empty evidence.
/// </summary>
public sealed class WebIqIntegrationTests
{
    private static WebIqResult Result(string title, string url, string? content = null) =>
        new(title, url, content);

    [Fact]
    public async Task Gather_maps_web_results_onto_evidence_using_the_direct_api_key()
    {
        var gateway = new ScriptedWebIqSearchGateway(
            Result("checkout 500s spiked", "https://example.com/a"),
            Result("same trace, second event", "https://example.com/b"));
        var env = new Dictionary<string, string?>
        {
            ["DSF_WEBIQ_QUERY"] = "acme checkout outage",
            ["WEBIQ_API_KEY"] = "direct-key",
        };
        var integration = new WebIqIntegration(env, gateway, new UnreachablePrivateKeySecretReader("must not be used"));

        var evidence = await integration.GatherAsync("webiq", "acme", CancellationToken.None);

        Assert.Equal(2, evidence.Count);
        Assert.Equal("https://example.com/a", evidence[0].Reference);
        Assert.Equal("checkout 500s spiked", evidence[0].Summary);
        Assert.Equal("webiq", evidence[0].SourceKind);
        Assert.Equal("direct-key", gateway.RequestedApiKey);
        Assert.Equal("acme checkout outage", gateway.RequestedQuery);
    }

    [Fact]
    public async Task Gather_falls_back_to_the_content_when_a_result_has_no_title()
    {
        var gateway = new ScriptedWebIqSearchGateway(
            Result(title: null!, url: "https://example.com/a", content: "raw page content"));
        var env = new Dictionary<string, string?>
        {
            ["DSF_WEBIQ_QUERY"] = "acme checkout outage",
            ["WEBIQ_API_KEY"] = "direct-key",
        };
        var integration = new WebIqIntegration(env, gateway);

        var evidence = await integration.GatherAsync("webiq", "acme", CancellationToken.None);

        var item = Assert.Single(evidence);
        Assert.Equal("raw page content", item.Summary);
    }

    [Fact]
    public async Task Gather_rejects_results_with_no_url()
    {
        var gateway = new ScriptedWebIqSearchGateway(Result("no url here", url: null!));
        var env = new Dictionary<string, string?>
        {
            ["DSF_WEBIQ_QUERY"] = "acme checkout outage",
            ["WEBIQ_API_KEY"] = "direct-key",
        };
        var integration = new WebIqIntegration(env, gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => integration.GatherAsync("webiq", "acme", CancellationToken.None));
    }

    [Fact]
    public async Task Gather_reads_the_api_key_from_key_vault_when_no_direct_key_is_set()
    {
        var gateway = new ScriptedWebIqSearchGateway(Result("a result", "https://example.com/a"));
        var secretReader = new ScriptedPrivateKeySecretReader("vault-key");
        var env = new Dictionary<string, string?>
        {
            ["DSF_WEBIQ_QUERY"] = "acme checkout outage",
            ["AZURE_KEYVAULT_URI"] = "https://acme-kv.vault.azure.net/",
            ["WEBIQ_API_KEY_SECRET"] = "webiq-api-key",
        };
        var integration = new WebIqIntegration(env, gateway, secretReader);

        await integration.GatherAsync("webiq", "acme", CancellationToken.None);

        Assert.Equal("vault-key", gateway.RequestedApiKey);
        Assert.Equal("webiq-api-key", secretReader.RequestedSecretName);
        Assert.Equal(new Uri("https://acme-kv.vault.azure.net/"), secretReader.RequestedVaultUri);
    }

    [Fact]
    public async Task Gather_without_a_configured_query_names_the_unset_setting()
    {
        var integration = new WebIqIntegration(
            new Dictionary<string, string?> { ["WEBIQ_API_KEY"] = "direct-key" },
            new ScriptedWebIqSearchGateway());

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => integration.GatherAsync("webiq", "acme", CancellationToken.None));

        Assert.Contains("DSF_WEBIQ_QUERY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_without_any_api_key_configuration_names_the_unset_settings()
    {
        var integration = new WebIqIntegration(
            new Dictionary<string, string?> { ["DSF_WEBIQ_QUERY"] = "acme checkout outage" },
            new ScriptedWebIqSearchGateway());

        var exception = await Assert.ThrowsAsync<RuntimeConfigurationException>(
            () => integration.GatherAsync("webiq", "acme", CancellationToken.None));

        Assert.Contains("AZURE_KEYVAULT_URI", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WEBIQ_API_KEY_SECRET", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_reports_an_unreachable_vault_instead_of_empty_evidence()
    {
        var integration = new WebIqIntegration(
            new Dictionary<string, string?>
            {
                ["DSF_WEBIQ_QUERY"] = "acme checkout outage",
                ["AZURE_KEYVAULT_URI"] = "https://acme-kv.vault.azure.net/",
                ["WEBIQ_API_KEY_SECRET"] = "webiq-api-key",
            },
            new ScriptedWebIqSearchGateway(),
            new UnreachablePrivateKeySecretReader("vault not found"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => integration.GatherAsync("webiq", "acme", CancellationToken.None));

        Assert.Contains("vault not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gather_reports_an_unreachable_webiq_endpoint_instead_of_empty_evidence()
    {
        var integration = new WebIqIntegration(
            new Dictionary<string, string?>
            {
                ["DSF_WEBIQ_QUERY"] = "acme checkout outage",
                ["WEBIQ_API_KEY"] = "direct-key",
            },
            new UnreachableWebIqSearchGateway("webiq unreachable"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => integration.GatherAsync("webiq", "acme", CancellationToken.None));

        Assert.Contains("webiq unreachable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_dependencies_resolve_webiq_to_the_typed_integration()
    {
        var registry = RuntimeDependencies.Production(new Dictionary<string, string?>()).SourceIntegrationRegistry;

        Assert.IsType<WebIqIntegration>(registry.Resolve("webiq"));
        Assert.IsType<WebIqIntegration>(registry.Resolve("WEBIQ"));
        var exception = Assert.Throws<RuntimeConfigurationException>(() => registry.Resolve("customsource"));
        Assert.Contains("customsource", exception.Message, StringComparison.Ordinal);
        Assert.Contains("registered source agent kind", exception.Message, StringComparison.Ordinal);
    }
}
