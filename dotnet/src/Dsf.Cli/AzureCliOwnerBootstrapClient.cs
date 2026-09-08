using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class AzureCliOwnerBootstrapClient(IAzureCliRunner runner)
    : IOwnerInfrastructure, IOwnerBootstrapStatusStore, IOwnerCredentialStore, IOwnerCredentialReader
{
    public async Task<OwnerGitHubCredentials> ReadAsync(
        string keyVaultUri,
        bool includePrivateKey,
        CancellationToken cancellationToken)
    {
        var vaultName = new Uri(keyVaultUri).Host.Split('.', 2)[0];
        var appId = await ReadSecretAsync(vaultName, "github-app-id", cancellationToken);
        var installationId = await ReadSecretAsync(
            vaultName,
            "github-app-installation-id",
            cancellationToken);
        var privateKey = includePrivateKey
            ? await ReadSecretAsync(vaultName, "github-app-private-key", cancellationToken)
            : string.Empty;
        return new OwnerGitHubCredentials(appId, installationId, privateKey);
    }

    public async Task WriteAsync(
        string keyVaultUri,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken)
    {
        var vaultName = new Uri(keyVaultUri).Host.Split('.', 2)[0];
        var resourceGroup = await RequiredOutputAsync(
            ["keyvault", "show", "--name", vaultName, "--query", "resourceGroup", "-o", "tsv"],
            cancellationToken);
        var parametersPath = Path.Combine(
            Path.GetTempPath(),
            $"dsf-owner-secrets-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                parametersPath,
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
                    ["contentVersion"] = "1.0.0.0",
                    ["parameters"] = new Dictionary<string, object>
                    {
                        ["vaultName"] = new { value = vaultName },
                        ["githubAppId"] = new { value = credentials.AppId },
                        ["githubInstallationId"] = new { value = credentials.InstallationId },
                        ["githubAppPrivateKey"] = new { value = credentials.PrivateKey },
                    },
                }),
                cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    parametersPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await RunAsync(
                [
                    "deployment", "group", "create", "--resource-group", resourceGroup,
                    "--name", $"dsf-owner-secrets-{vaultName}",
                    "--template-file", Path.Combine(FindRepoRoot(), "infra", "owner-secrets.bicep"),
                    "--parameters", $"@{parametersPath}",
                    "-o", "none",
                ],
                cancellationToken);
        }
        finally
        {
            File.Delete(parametersPath);
        }
    }

    public async Task WriteAsync(
        OwnerAuthority authority,
        OwnerBootstrapRequest request,
        OwnerBootstrapStatus status,
        CancellationToken cancellationToken)
    {
        var value = JsonSerializer.Serialize(status);
        if (value.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Bootstrap status must not contain private-key material.");
        }

        await RunAsync(
            [
                "appconfig", "kv", "set", "--endpoint", authority.AppConfigEndpoint,
                "--auth-mode", "login", "--key", ProductConfigurationKeys.OwnerBootstrapStatus(request.AppName),
                "--value", value, "--yes",
            ],
            cancellationToken);
    }

    public async Task<OwnerAuthority> EnsureAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        var subscriptionId = await RequiredOutputAsync(
            ["account", "show", "--query", "id", "-o", "tsv"],
            cancellationToken);
        var operatorObjectId = await RequiredOutputAsync(
            ["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"],
            cancellationToken);

        await RunAsync(
            ["group", "create", "--name", request.ResourceGroup, "--location", request.Location],
            cancellationToken);
        await RunAsync(
            [
                "appconfig", "create", "--name", request.AppConfigName,
                "--resource-group", request.ResourceGroup, "--location", request.Location,
                "--sku", "Standard", "--disable-local-auth", "true",
            ],
            cancellationToken);
        await RunAsync(
            [
                "deployment", "group", "create", "--resource-group", request.ResourceGroup,
                "--name", $"dsf-owner-kv-{request.KeyVaultName}",
                "--template-file", Path.Combine(FindRepoRoot(), "infra", "owner-keyvault.bicep"),
                "--parameters", $"vaultName={request.KeyVaultName}", $"location={request.Location}",
            ],
            cancellationToken);

        await AssignRoleAsync(
            "App Configuration Data Owner",
            operatorObjectId,
            $"/subscriptions/{subscriptionId}/resourceGroups/{request.ResourceGroup}/providers/Microsoft.AppConfiguration/configurationStores/{request.AppConfigName}",
            cancellationToken);
        await AssignRoleAsync(
            "Key Vault Secrets Officer",
            operatorObjectId,
            $"/subscriptions/{subscriptionId}/resourceGroups/{request.ResourceGroup}/providers/Microsoft.KeyVault/vaults/{request.KeyVaultName}",
            cancellationToken);

        return new OwnerAuthority(
            $"https://{request.KeyVaultName}.vault.azure.net/",
            $"https://{request.AppConfigName}.azconfig.io");
    }

    private async Task AssignRoleAsync(
        string role,
        string assigneeObjectId,
        string scope,
        CancellationToken cancellationToken) =>
        await RunAsync(
            [
                "role", "assignment", "create", "--role", role,
                "--assignee-object-id", assigneeObjectId,
                "--assignee-principal-type", "User",
                "--scope", scope,
            ],
            cancellationToken);

    private async Task<string> ReadSecretAsync(
        string vaultName,
        string secretName,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            [
                "keyvault", "secret", "show", "--vault-name", vaultName,
                "--name", secretName, "--query", "value", "-o", "tsv",
            ],
            cancellationToken);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException(
                $"Owner Key Vault '{vaultName}' secret '{secretName}' is empty.");
        }

        return result.StandardOutput.Trim();
    }

    private async Task<string> RequiredOutputAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException($"az {string.Join(' ', arguments)} returned no output.");
        }

        return result.StandardOutput.Trim();
    }

    private async Task<AzureCliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "infra", "owner-keyvault.bicep")))
        {
            directory = directory.Parent;
        }

        return (directory ?? throw new DirectoryNotFoundException("Could not locate repository root.")).FullName;
    }
}
