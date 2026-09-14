namespace Dsf.Cli;

internal enum OwnerBootstrapStage
{
    Planned,
    AppConfigurationReady,
    KeyVaultReady,
    GitHubAppCreated,
    CredentialsStored,
    Completed,
    Failed,
}

internal sealed record OwnerBootstrapRequest(
    string AppName,
    string ResourceGroup,
    string KeyVaultName,
    string AppConfigName,
    string Location);

internal sealed record OwnerAuthority(string KeyVaultUri, string AppConfigEndpoint);

internal sealed record OwnerGitHubCredentials(
    string AppId,
    string InstallationId,
    string PrivateKey,
    string? InstallationSelection = null,
    string? Slug = null);

internal sealed record OwnerBootstrapStatus(
    OwnerBootstrapStage Stage,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OwnerBootstrapStage> CompletedStages,
    string? KeyVaultUri = null,
    string? AppConfigEndpoint = null,
    string? AppId = null,
    string? InstallationId = null,
    string? InstallationSelection = null,
    string? Error = null);

internal interface IOwnerInfrastructure
{
    Task<OwnerAuthority> EnsureAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken);
}

internal interface IOwnerBootstrapStatusStore
{
    Task WriteAsync(
        OwnerAuthority authority,
        OwnerBootstrapRequest request,
        OwnerBootstrapStatus status,
        CancellationToken cancellationToken);
}

internal interface IGitHubAppBootstrapper
{
    Task<OwnerGitHubCredentials> GetOrCreateAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal interface IOwnerCredentialStore
{
    Task WriteAsync(
        string keyVaultUri,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken);
}

internal sealed class OwnerBootstrapper(
    IOwnerInfrastructure infrastructure,
    IOwnerBootstrapStatusStore statusStore,
    IGitHubAppBootstrapper github,
    IOwnerCredentialStore credentials,
    ICliTerminal? terminal = null)
{
    public async Task ExecuteAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        var completed = new List<OwnerBootstrapStage>();
        var authority = await infrastructure.EnsureAsync(request, cancellationToken);

        await WriteAsync(OwnerBootstrapStage.Planned);
        await WriteAsync(OwnerBootstrapStage.AppConfigurationReady);
        await WriteAsync(OwnerBootstrapStage.KeyVaultReady);

        var appCredentials = await github.GetOrCreateAsync(request, cancellationToken);
        await WriteAsync(
            OwnerBootstrapStage.GitHubAppCreated,
            appCredentials.AppId,
            appCredentials.InstallationId,
            appCredentials.InstallationSelection);

        terminal?.WriteLine($"[dsf] Storing GitHub App credentials in Key Vault {request.KeyVaultName}...");
        await credentials.WriteAsync(authority.KeyVaultUri, appCredentials, cancellationToken);
        terminal?.WriteLine("[dsf] Credentials stored in Key Vault.");
        await github.CompleteAsync(request, cancellationToken);
        await WriteAsync(
            OwnerBootstrapStage.CredentialsStored,
            appCredentials.AppId,
            appCredentials.InstallationId,
            appCredentials.InstallationSelection);
        await WriteAsync(
            OwnerBootstrapStage.Completed,
            appCredentials.AppId,
            appCredentials.InstallationId,
            appCredentials.InstallationSelection);

        async Task WriteAsync(
            OwnerBootstrapStage stage,
            string? appId = null,
            string? installationId = null,
            string? installationSelection = null)
        {
            if (stage is not OwnerBootstrapStage.Planned)
            {
                completed.Add(stage);
            }

            terminal?.WriteLine($"[dsf] Recording owner bootstrap status: {stage}...");
            await statusStore.WriteAsync(
                authority,
                request,
                new OwnerBootstrapStatus(
                    stage,
                    DateTimeOffset.UtcNow,
                    completed.ToArray(),
                    authority.KeyVaultUri,
                    authority.AppConfigEndpoint,
                    appId,
                    installationId,
                    installationSelection),
                cancellationToken);
        }
    }
}
