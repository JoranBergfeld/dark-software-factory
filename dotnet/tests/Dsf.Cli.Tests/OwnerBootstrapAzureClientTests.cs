using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class OwnerBootstrapAzureClientTests
{
    [Fact]
    public void Owner_vault_explicitly_allows_operator_data_plane_access()
    {
        var template = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "owner-keyvault.bicep"));

        Assert.Contains("publicNetworkAccess: 'Enabled'", template);
        Assert.Contains("defaultAction: 'Allow'", template);
    }

    [Fact]
    public async Task Write_credentials_uses_private_file_and_suppresses_secret_output()
    {
        var runner = new RecordingAzureCliRunner();
        var client = new AzureCliOwnerBootstrapClient(runner);

        await client.WriteAsync(
            "https://kvdsfsbx20260907.vault.azure.net/",
            new OwnerGitHubCredentials(
                "7",
                "42",
                "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----"),
            CancellationToken.None);

        Assert.Equal(3, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation => Assert.Contains("-o", invocation));
        Assert.All(runner.Invocations, invocation => Assert.Contains("none", invocation));
        Assert.DoesNotContain(
            runner.Invocations.SelectMany(invocation => invocation),
            argument => argument.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.Contains("--file", runner.Invocations[2]);
    }

    [Fact]
    public async Task Write_status_persists_a_non_secret_document_in_owner_app_configuration()
    {
        var runner = new RecordingAzureCliRunner();
        var client = new AzureCliOwnerBootstrapClient(runner);
        var authority = new OwnerAuthority(
            "https://kvdsfsbx20260907.vault.azure.net/",
            "https://appcsdsfsbx20260907.azconfig.io");

        await client.WriteAsync(
            authority,
            new OwnerBootstrapRequest(
                "dsf-sbx-20260907", "rg-dsf-app", "kvdsfsbx20260907",
                "appcsdsfsbx20260907", "swedencentral"),
            new OwnerBootstrapStatus(
                OwnerBootstrapStage.AppConfigurationReady,
                DateTimeOffset.UnixEpoch,
                [OwnerBootstrapStage.AppConfigurationReady],
                AppConfigEndpoint: authority.AppConfigEndpoint),
            CancellationToken.None);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("appconfig", invocation[0]);
        Assert.Contains("dsf/owner/bootstrap/dsf-sbx-20260907/status", invocation);
        Assert.DoesNotContain(invocation, argument => argument.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_creates_app_configuration_before_key_vault_and_grants_operator_roles()
    {
        var runner = new RecordingAzureCliRunner(
            new AzureCliInvocationResult(0, "sub-id", ""),
            new AzureCliInvocationResult(0, "operator-id", ""),
            new AzureCliInvocationResult(0, "", ""),
            new AzureCliInvocationResult(0, "", ""),
            new AzureCliInvocationResult(0, "", ""),
            new AzureCliInvocationResult(0, "", ""));
        var client = new AzureCliOwnerBootstrapClient(runner);

        var authority = await client.EnsureAsync(
            new OwnerBootstrapRequest(
                "dsf-sbx-20260907",
                "rg-dsf-app",
                "kvdsfsbx20260907",
                "appcsdsfsbx20260907",
                "swedencentral"),
            CancellationToken.None);

        Assert.Equal("https://kvdsfsbx20260907.vault.azure.net/", authority.KeyVaultUri);
        Assert.Equal("https://appcsdsfsbx20260907.azconfig.io", authority.AppConfigEndpoint);
        Assert.Equal(
            [
                "group", "create", "--name", "rg-dsf-app", "--location", "swedencentral",
            ],
            runner.Invocations[2]);
        Assert.Equal(
            [
                "appconfig", "create", "--name", "appcsdsfsbx20260907",
                "--resource-group", "rg-dsf-app", "--location", "swedencentral",
                "--sku", "Standard", "--disable-local-auth", "true",
            ],
            runner.Invocations[3]);
        Assert.Equal(
            [
                "deployment", "group", "create", "--resource-group", "rg-dsf-app",
                "--name", "dsf-owner-kv-kvdsfsbx20260907", "--template-file",
                Path.Combine(FindRepoRoot(), "infra", "owner-keyvault.bicep"),
                "--parameters", "vaultName=kvdsfsbx20260907", "location=swedencentral",
            ],
            runner.Invocations[4]);
        Assert.Contains("App Configuration Data Owner", runner.Invocations[5]);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return (directory ?? throw new InvalidOperationException("Repository root not found.")).FullName;
    }
}
