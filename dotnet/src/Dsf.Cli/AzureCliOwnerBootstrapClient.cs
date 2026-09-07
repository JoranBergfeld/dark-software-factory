namespace Dsf.Cli;

internal sealed class AzureCliOwnerBootstrapClient(IAzureCliRunner runner) : IOwnerInfrastructure
{
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
               && !File.Exists(Path.Combine(directory.FullName, "README.md")))
        {
            directory = directory.Parent;
        }

        return (directory ?? throw new DirectoryNotFoundException("Could not locate repository root.")).FullName;
    }
}
