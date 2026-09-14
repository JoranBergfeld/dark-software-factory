using System.Text.Json;

namespace Dsf.Cli;

internal sealed class AzureCliOwnerBootstrapClient(
    IAzureCliRunner runner,
    ICliTerminal? terminal = null,
    HttpClient? appConfigHttpClient = null,
    Func<TimeSpan, CancellationToken, Task>? authorizationRetryDelay = null,
    ProvisioningAssets? assets = null)
    : IOwnerInfrastructure, IOwnerBootstrapStatusStore, IOwnerCredentialStore, IOwnerCredentialReader
{
    private static readonly HttpClient DefaultAppConfigHttpClient = new();
    private readonly ProvisioningAssets assets = assets ?? new ProvisioningAssets();
    private readonly OwnerAppConfigurationStatusStore statusStore = new(
        runner, appConfigHttpClient ?? DefaultAppConfigHttpClient, terminal, authorizationRetryDelay);

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

    public async Task<OwnerGitHubIdentity?> ReadIdentityFromStatusAsync(
        string ownerAppConfigEndpoint,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            [
                "appconfig", "kv", "list", "--endpoint", ownerAppConfigEndpoint,
                "--auth-mode", "login", "--key", "dsf/owner/bootstrap/*",
                "-o", "json",
            ],
            cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var identities = document.RootElement.EnumerateArray()
            .Where(entry => entry.TryGetProperty("key", out var key)
                            && (key.GetString()?.EndsWith("/status", StringComparison.Ordinal) ?? false))
            .Select(entry => entry.TryGetProperty("value", out var value) ? value.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => JsonSerializer.Deserialize<OwnerBootstrapStatus>(value!))
            .Where(status => status?.Stage is OwnerBootstrapStage.Completed
                             && !string.IsNullOrWhiteSpace(status.AppId)
                             && !string.IsNullOrWhiteSpace(status.InstallationId))
            .Select(status => new OwnerGitHubIdentity(
                status!.AppId!,
                status.InstallationId!,
                status.InstallationSelection))
            .ToArray();
        return identities.Length switch
        {
            0 => null,
            1 => identities[0],
            _ => throw new InvalidOperationException(
                $"Owner App Configuration '{ownerAppConfigEndpoint}' contains more than one completed bootstrap status."),
        };
    }

    public async Task WriteAsync(
        string keyVaultUri,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken)
    {
        var templatePath = assets.Resolve("owner-secrets.json");
        await ProvisioningAssets.CheckAzureCliAsync(runner, cancellationToken);
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
                    "--template-file", templatePath,
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

    public Task WriteAsync(
        OwnerAuthority authority,
        OwnerBootstrapRequest request,
        OwnerBootstrapStatus status,
        CancellationToken cancellationToken) =>
        statusStore.WriteAsync(authority, request, status, cancellationToken);

    public async Task<OwnerAuthority> EnsureAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        var templatePath = assets.Resolve("owner-keyvault.json");
        assets.Resolve("owner-secrets.json");
        await ProvisioningAssets.CheckAzureCliAsync(runner, cancellationToken);
        terminal?.WriteLine("[dsf] Checking Azure subscription and signed-in operator...");
        var subscriptionId = await RequiredOutputAsync(
            ["account", "show", "--query", "id", "-o", "tsv"],
            cancellationToken);
        var operatorObjectId = await RequiredOutputAsync(
            ["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"],
            cancellationToken);

        terminal?.WriteLine($"[dsf] Ensuring resource group {request.ResourceGroup} in {request.Location} (subscription {subscriptionId})...");
        await RunAsync(
            ["group", "create", "--name", request.ResourceGroup, "--location", request.Location],
            cancellationToken);
        terminal?.WriteLine($"[dsf] Resource group {request.ResourceGroup} ready.");
        terminal?.WriteLine($"[dsf] Ensuring App Configuration {request.AppConfigName}; Azure provisioning may take several minutes...");
        await RunAsync(
            [
                "appconfig", "create", "--name", request.AppConfigName,
                "--resource-group", request.ResourceGroup, "--location", request.Location,
                "--sku", "Standard", "--disable-local-auth", "true",
            ],
            cancellationToken);
        terminal?.WriteLine($"[dsf] App Configuration {request.AppConfigName} ready.");
        terminal?.WriteLine($"[dsf] Deploying Key Vault {request.KeyVaultName}; Azure provisioning may take several minutes...");
        await RunAsync(
            [
                "deployment", "group", "create", "--resource-group", request.ResourceGroup,
                "--name", $"dsf-owner-kv-{request.KeyVaultName}",
                "--template-file", templatePath,
                "--parameters", $"vaultName={request.KeyVaultName}", $"location={request.Location}",
            ],
            cancellationToken);
        terminal?.WriteLine($"[dsf] Key Vault {request.KeyVaultName} ready.");

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

        terminal?.WriteLine("[dsf] Owner Azure services and operator role assignments ready.");
        return new OwnerAuthority(
            $"https://{request.KeyVaultName}.vault.azure.net/",
            $"https://{request.AppConfigName}.azconfig.io");
    }

    private async Task AssignRoleAsync(
        string role,
        string assigneeObjectId,
        string scope,
        CancellationToken cancellationToken)
    {
        terminal?.WriteLine($"[dsf] Ensuring operator role: {role}...");
        await RunAsync(
            [
                "role", "assignment", "create", "--role", role,
                "--assignee-object-id", assigneeObjectId,
                "--assignee-principal-type", "User",
                "--scope", scope,
            ],
            cancellationToken);
        terminal?.WriteLine($"[dsf] Operator role assigned: {role}.");
    }

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

}
