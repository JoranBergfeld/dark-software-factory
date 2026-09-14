using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class OwnerBootstrapAzureClientTests
{
    private static readonly AzureCliInvocationResult AzureVersion =
        new(0, """{"azure-cli":"2.77.0"}""", "");

    [Fact]
    public async Task Ensure_reports_resource_and_role_progress()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var runner = new RecordingAzureCliRunner(
            AzureVersion,
            new AzureCliInvocationResult(0, "sub-id", ""),
            new AzureCliInvocationResult(0, "operator-id", ""));
        var client = new AzureCliOwnerBootstrapClient(runner, terminal);

        await client.EnsureAsync(
            new OwnerBootstrapRequest("dsf-test", "rg-dsf-test", "kvtest", "cfgtest", "swedencentral"),
            CancellationToken.None);

        Assert.Contains("subscription", terminal.Output);
        Assert.Contains("rg-dsf-test", terminal.Output);
        Assert.Contains("App Configuration cfgtest", terminal.Output);
        Assert.Contains("Key Vault kvtest", terminal.Output);
        Assert.Contains("App Configuration Data Owner", terminal.Output);
        Assert.Contains("Key Vault Secrets Officer", terminal.Output);
        Assert.Contains("ready", terminal.Output);
    }

    [Fact]
    public void Owner_vault_keeps_public_access_disabled_by_policy()
    {
        var template = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "owner-keyvault.bicep"));

        Assert.Contains("publicNetworkAccess: 'Disabled'", template);
        Assert.Contains("defaultAction: 'Deny'", template);
    }

    [Fact]
    public void Owner_secret_template_uses_secure_parameters()
    {
        var template = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "owner-secrets.bicep"));

        Assert.Contains("@secure()", template);
        Assert.Contains("resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing", template);
        Assert.Contains("resource githubAppPrivateKeySecret 'Microsoft.KeyVault/vaults/secrets@", template);
    }

    [Fact]
    public async Task Write_credentials_uses_arm_secure_parameters_and_suppresses_secret_output()
    {
        var runner = new RecordingAzureCliRunner(AzureVersion, new AzureCliInvocationResult(0, "rg-dsf-app", ""));
        var client = new AzureCliOwnerBootstrapClient(runner);

        await client.WriteAsync(
            "https://kvdsfsbx20260907.vault.azure.net/",
            new OwnerGitHubCredentials(
                "7",
                "42",
                "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----"),
            CancellationToken.None);

        Assert.Equal(
            ["keyvault", "show", "--name", "kvdsfsbx20260907", "--query", "resourceGroup", "-o", "tsv"],
            runner.Invocations[1]);
        var invocation = runner.Invocations[2];
        Assert.Equal("deployment", invocation[0]);
        Assert.Equal("group", invocation[1]);
        Assert.Equal("create", invocation[2]);
        Assert.Contains("--parameters", invocation);
        Assert.Contains(invocation, argument => argument.StartsWith("@", StringComparison.Ordinal));
        Assert.DoesNotContain(
            invocation,
            argument => argument.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.Contains("-o", invocation);
        Assert.Contains("none", invocation);
        Assert.Contains(Path.Combine(AppContext.BaseDirectory, "assets", "infra", "owner-secrets.json"), invocation);
    }

    [Fact]
    public async Task Ensure_creates_app_configuration_before_key_vault_and_grants_operator_roles()
    {
        var runner = new RecordingAzureCliRunner(
            AzureVersion,
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
            runner.Invocations[3]);
        Assert.Equal(
            [
                "appconfig", "create", "--name", "appcsdsfsbx20260907",
                "--resource-group", "rg-dsf-app", "--location", "swedencentral",
                "--sku", "Standard", "--disable-local-auth", "true",
            ],
            runner.Invocations[4]);
        Assert.Equal(
            [
                "deployment", "group", "create", "--resource-group", "rg-dsf-app",
                "--name", "dsf-owner-kv-kvdsfsbx20260907", "--template-file",
                Path.Combine(AppContext.BaseDirectory, "assets", "infra", "owner-keyvault.json"),
                "--parameters", "vaultName=kvdsfsbx20260907", "location=swedencentral",
            ],
            runner.Invocations[5]);
        Assert.Contains("App Configuration Data Owner", runner.Invocations[6]);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
               && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return (directory ?? throw new InvalidOperationException("Repository root not found.")).FullName;
    }
}
