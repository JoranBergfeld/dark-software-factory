using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class ProvisioningAssetsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dry_run_resolves_shipped_templates_without_tooling(bool bootstrap)
    {
        using var assets = new ProvisioningAssetFixture();
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(1, "", "Azure CLI is unavailable"));
        var github = new RecordingGitHubProvisioningClient();
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        string[] arguments = bootstrap
            ? ["bootstrap", "--app-name", "asset-test", "--keyvault-name", "kv-test",
                "--appconfig-name", "cfg-test", "--dry-run"]
            : ["new", "--product", "asset-test", "--owner", "acme", "--dry-run",
                "--config-root", assets.OutputRoot];

        var exitCode = await CliApplication.InvokeAsync(
            arguments, CancellationToken.None, terminal, github, new AzureCliProvisioningClient(runner));

        Assert.Equal(0, exitCode);
        Assert.Empty(runner.Invocations);
        Assert.Empty(github.Requests);
        Assert.False(Directory.Exists(assets.OutputRoot));
        string[] entrypoints = bootstrap
            ? ["owner-keyvault.json", "owner-secrets.json"]
            : ["main.json", "sre-agent.json", "copy-owner-secret.json"];
        foreach (var entrypoint in entrypoints)
        {
            Assert.Contains(
                $"[dsf] provisioning template: {Path.Combine(AppContext.BaseDirectory, "assets", "infra", entrypoint)}",
                terminal.Output);
        }
        Assert.DoesNotContain("infra/main.bicep", terminal.Output);
    }

    [Theory]
    [InlineData("owner-keyvault.json")]
    [InlineData("owner-secrets.json")]
    [InlineData("main.json")]
    [InlineData("sre-agent.json")]
    [InlineData("copy-owner-secret.json")]
    public void Default_resolver_uses_shipped_assembly_relative_templates(string entrypoint)
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "assets", "infra", entrypoint),
            new ProvisioningAssets().Resolve(entrypoint));
    }

    [Theory]
    [InlineData("../main.json")]
    [InlineData("/main.json")]
    [InlineData("modules/cosmos.json")]
    [InlineData("main.bicep")]
    [InlineData("unshipped.json")]
    public void Resolver_rejects_non_entrypoints(string entrypoint)
    {
        using var assets = new ProvisioningAssetFixture();
        var error = Assert.Throws<InvalidOperationException>(() => assets.Resolver.Resolve(entrypoint));
        Assert.Contains(entrypoint, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"$schema":42,"contentVersion":"1.0.0.0","resources":[]}""")]
    [InlineData("""{"$schema":"https://example.com/template","contentVersion":"1.0.0.0","resources":[]}""")]
    [InlineData("""{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"invalid","resources":[]}""")]
    [InlineData("""{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":null}""")]
    [InlineData("""{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"properties":{"templateLink":{"uri":"modules/cosmos.json"}}}]}""")]
    public void Missing_or_corrupt_assets_name_path_and_recovery(string? content)
    {
        using var assets = new ProvisioningAssetFixture();
        var path = assets.PathFor("main.json");
        if (content is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, content);
        }

        var error = Assert.Throws<InvalidOperationException>(() => assets.Resolver.Resolve("main.json"));

        Assert.Contains(path, error.Message);
        Assert.Contains("reinstall", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("main.json")]
    [InlineData("sre-agent.json")]
    [InlineData("copy-owner-secret.json")]
    public async Task Product_preflight_checks_every_template_before_azure_commands(string missing)
    {
        using var assets = new ProvisioningAssetFixture();
        File.Delete(assets.PathFor(missing));
        var runner = new RecordingAzureCliRunner();
        var client = new AzureCliProvisioningClient(runner, assets.Resolver);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PreflightAsync(CancellationToken.None));

        Assert.Contains(assets.PathFor(missing), error.Message);
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("owner-keyvault.json")]
    [InlineData("owner-secrets.json")]
    public async Task Owner_preflight_checks_both_templates_before_azure_commands(string missing)
    {
        using var assets = new ProvisioningAssetFixture();
        File.Delete(assets.PathFor(missing));
        var runner = new RecordingAzureCliRunner();
        var client = new AzureCliOwnerBootstrapClient(runner, assets: assets.Resolver);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnsureAsync(
                new OwnerBootstrapRequest("app", "rg", "kv", "cfg", "swedencentral"),
                CancellationToken.None));

        Assert.Contains(assets.PathFor(missing), error.Message);
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData(1, "", "az not installed")]
    [InlineData(0, "{}", "")]
    [InlineData(0, "not json", "")]
    [InlineData(0, """{"azure-cli":"not-a-version"}""", "")]
    public async Task Product_preflight_rejects_unavailable_or_invalid_az_version(
        int exitCode, string stdout, string stderr)
    {
        using var assets = new ProvisioningAssetFixture();
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(exitCode, stdout, stderr));
        var client = new AzureCliProvisioningClient(runner, assets.Resolver);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PreflightAsync(CancellationToken.None));

        Assert.Contains("Azure CLI", error.Message);
        Assert.Equal(["version", "--output", "json"], Assert.Single(runner.Invocations));
    }

    [Fact]
    public async Task Product_preflight_uses_injected_az_runner()
    {
        using var assets = new ProvisioningAssetFixture();
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, """{"azure-cli":"2.77.0"}""", ""));

        await new AzureCliProvisioningClient(runner, assets.Resolver).PreflightAsync(CancellationToken.None);

        Assert.Equal(["version", "--output", "json"], Assert.Single(runner.Invocations));
    }

    [Theory]
    [InlineData("main.json", false)]
    [InlineData("sre-agent.json", false)]
    [InlineData("copy-owner-secret.json", false)]
    [InlineData("main.json", true)]
    public async Task Live_new_asset_failure_prevents_github_creation(string entrypoint, bool corrupt)
    {
        using var assets = new ProvisioningAssetFixture();
        if (corrupt)
        {
            File.WriteAllText(assets.PathFor(entrypoint), "{}");
        }
        else
        {
            File.Delete(assets.PathFor(entrypoint));
        }

        var runner = new RecordingAzureCliRunner();
        var github = new RecordingGitHubProvisioningClient();
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var exitCode = await CliApplication.InvokeAsync(
            [
                "new", "--product", "asset-test", "--owner", "acme",
                "--github-app-id", "1", "--github-installation-id", "2",
                "--owner-appconfig-endpoint", "https://owner.azconfig.io",
                "--config-root", assets.OutputRoot,
            ],
            CancellationToken.None, terminal, github,
            new AzureCliProvisioningClient(runner, assets.Resolver),
            new RecordingAppConfigurationClient(), new RecordingCharterRepositoryClient(null));

        Assert.Equal(1, exitCode);
        Assert.Contains(assets.PathFor(entrypoint), terminal.Error);
        Assert.Empty(github.Requests);
        Assert.Empty(runner.Invocations);
        Assert.False(Directory.Exists(assets.OutputRoot));
    }

    [Fact]
    public async Task Owner_az_version_failure_prevents_resource_creation()
    {
        using var assets = new ProvisioningAssetFixture();
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(1, "", "not available"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureCliOwnerBootstrapClient(runner, assets: assets.Resolver).EnsureAsync(
                new OwnerBootstrapRequest("app", "rg", "kv", "cfg", "swedencentral"),
                CancellationToken.None));

        Assert.Contains("Azure CLI", error.Message);
        Assert.Equal(["version", "--output", "json"], Assert.Single(runner.Invocations));
    }

    [Fact]
    public async Task Secret_operations_resolve_assets_before_any_azure_commands()
    {
        using var assets = new ProvisioningAssetFixture();
        File.Delete(assets.PathFor("owner-secrets.json"));
        File.Delete(assets.PathFor("copy-owner-secret.json"));
        var runner = new RecordingAzureCliRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureCliOwnerBootstrapClient(runner, assets: assets.Resolver).WriteAsync(
                "https://owner.vault.azure.net/", new OwnerGitHubCredentials("1", "2", "secret"),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureCliProvisioningClient(runner, assets.Resolver).CopyOwnerAppPrivateKeyAsync(
                new CopyOwnerAppPrivateKeyRequest("https://owner.vault.azure.net/", "https://product.vault.azure.net/"),
                CancellationToken.None));

        Assert.Empty(runner.Invocations);
    }
}

internal sealed class ProvisioningAssetFixture : IDisposable
{
    private readonly string root = Path.Combine(
        Directory.GetCurrentDirectory(), ".test-artifacts", "provisioning-assets", Guid.NewGuid().ToString("N"));

    internal ProvisioningAssetFixture()
    {
        Directory.CreateDirectory(Path.Combine(root, "assets", "infra"));
        foreach (var entrypoint in new[]
        {
            "owner-keyvault.json", "owner-secrets.json", "main.json", "sre-agent.json", "copy-owner-secret.json",
        })
        {
            File.WriteAllText(PathFor(entrypoint), Template);
        }
    }

    internal const string Template =
        """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[]}""";

    internal ProvisioningAssets Resolver => new(root);

    internal string OutputRoot => Path.Combine(root, "instance-output");

    internal string PathFor(string entrypoint) => Path.Combine(root, "assets", "infra", entrypoint);

    public void Dispose() => Directory.Delete(root, recursive: true);
}
