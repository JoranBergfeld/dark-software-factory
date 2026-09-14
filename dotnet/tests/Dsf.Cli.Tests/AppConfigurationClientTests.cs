using Dsf.Cli;
using Dsf.Core.Products;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class AppConfigurationClientTests
{
    [Fact]
    public async Task ResolveProduct_reads_repository_and_product_app_config_endpoint_from_owner_index()
    {
        var runner = new RecordingAzureCliRunner(
            new AzureCliInvocationResult(
                0,
                """
                [
                  {"key":"GITHUB_REPOSITORY","value":"acme/demo","label":"demo"},
                  {"key":"AZURE_APPCONFIG_ENDPOINT","value":"https://demo.azconfig.io","label":"demo"}
                ]
                """,
                ""));
        var client = new AzureCliAppConfigurationClient(runner);

        var product = await client.ResolveProductAsync(
            "https://owner.azconfig.io", "demo", CancellationToken.None);

        Assert.Equal("demo", product.Key);
        Assert.Equal("acme/demo", product.GitHubRepository);
        Assert.Equal("https://demo.azconfig.io", product.AppConfigEndpoint);
        Assert.Equal(
            ["appconfig", "kv", "list", "--endpoint", "https://owner.azconfig.io", "--auth-mode", "login", "--label", "demo", "-o", "json"],
            Assert.Single(runner.Invocations));
    }

    [Fact]
    public async Task ResolveProduct_fails_loudly_when_owner_index_has_no_product()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, "[]", ""));
        var client = new AzureCliAppConfigurationClient(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResolveProductAsync("https://owner.azconfig.io", "missing", CancellationToken.None));

        Assert.Contains("missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("owner", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeedProductRecord_writes_canonical_product_keys_to_the_product_store()
    {
        var runner = new RecordingAzureCliRunner();
        var client = new AzureCliAppConfigurationClient(runner);
        var record = new ProductRecord(
            "demo",
            "acme/demo",
            new Dictionary<string, IReadOnlyList<string>> { ["type"] = ["feature"] },
            "project:demo",
            ["demo-api"],
            ["demo-dashboard"],
            "/subscriptions/demo",
            0.7);

        await client.SeedProductRecordAsync(
            "https://demo.azconfig.io", record, CancellationToken.None);

        Assert.Contains(
            runner.Invocations,
            invocation => invocation.SequenceEqual(
                ["appconfig", "kv", "set", "--endpoint", "https://demo.azconfig.io", "--auth-mode", "login", "--key",
                    ProductConfigurationKeys.GitHubRepository, "--value", "\"acme/demo\"", "--yes"]));
        Assert.Contains(
            runner.Invocations,
            invocation => invocation.SequenceEqual(
                ["appconfig", "kv", "set", "--endpoint", "https://demo.azconfig.io", "--auth-mode", "login", "--key",
                    ProductConfigurationKeys.Threshold("demo"), "--value", "0.7", "--yes"]));
    }

    [Fact]
    public void Product_configuration_key_semantics_are_owned_by_core()
    {
        Assert.Equal("product.github_repo", ProductConfigurationKeys.GitHubRepository);
        Assert.Equal("product.label_taxonomy", ProductConfigurationKeys.LabelTaxonomy);
        Assert.Equal("agents.webiq.enabled", ProductConfigurationKeys.AgentEnabled(" WEBIQ "));
        Assert.Equal("threshold.demo", ProductConfigurationKeys.Threshold("demo"));
        Assert.Equal("\0", ProductConfigurationKeys.NoLabel);
        Assert.Equal("*", ProductConfigurationKeys.AnyLabel);
    }

    [Fact]
    public void Owner_bootstrap_status_key_is_stable_per_app()
    {
        Assert.Equal(
            "dsf/owner/bootstrap/dsf-sbx-20260907/status",
            ProductConfigurationKeys.OwnerBootstrapStatus("dsf-sbx-20260907"));
    }

    [Fact]
    public async Task Source_roster_seed_enables_only_provisioned_kinds_for_the_product()
    {
        var runner = new RecordingAzureCliRunner();
        var client = new AzureCliAppConfigurationClient(runner);

        await client.SeedSourceAgentRosterAsync("https://demo.azconfig.io", "demo", ["azuremonitor"], CancellationToken.None);

        Assert.Equal(3, runner.Invocations.Count);
        foreach (var kind in SourceAgentKinds.Known)
        {
            Assert.Contains(runner.Invocations, invocation =>
                invocation.Contains(ProductConfigurationKeys.AgentEnabled(kind))
                && invocation.Contains(kind == "azuremonitor" ? "true" : "false")
                && invocation.Contains("--label")
                && invocation.Contains("demo"));
        }
    }
}
