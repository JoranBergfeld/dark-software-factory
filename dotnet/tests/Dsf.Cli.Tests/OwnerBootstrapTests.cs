using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class OwnerBootstrapTests
{
    [Fact]
    public async Task Execute_records_non_secret_stage_boundaries_in_order()
    {
        var infrastructure = new RecordingOwnerInfrastructure();
        var statusStore = new RecordingOwnerBootstrapStatusStore();
        var github = new RecordingGitHubAppBootstrapper();
        var credentials = new RecordingOwnerCredentialStore();
        var bootstrapper = new OwnerBootstrapper(infrastructure, statusStore, github, credentials);

        await bootstrapper.ExecuteAsync(
            new OwnerBootstrapRequest(
                "dsf-sbx-20260907",
                "rg-dsf-app",
                "kvdsfsbx20260907",
                "appcsdsfsbx20260907",
                "swedencentral"),
            CancellationToken.None);

        Assert.Equal(
            [
                OwnerBootstrapStage.Planned,
                OwnerBootstrapStage.AppConfigurationReady,
                OwnerBootstrapStage.KeyVaultReady,
                OwnerBootstrapStage.GitHubAppCreated,
                OwnerBootstrapStage.CredentialsStored,
                OwnerBootstrapStage.Completed,
            ],
            statusStore.Statuses.Select(status => status.Stage));
        Assert.All(
            statusStore.Statuses,
            status => Assert.DoesNotContain("BEGIN", status.Error ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal("https://kvdsfsbx20260907.vault.azure.net/", credentials.KeyVaultUri);
        Assert.True(github.Completed);
    }
}

internal sealed class RecordingOwnerInfrastructure : IOwnerInfrastructure
{
    public Task<OwnerAuthority> EnsureAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            new OwnerAuthority(
                $"https://{request.KeyVaultName}.vault.azure.net/",
                $"https://{request.AppConfigName}.azconfig.io"));
}

internal sealed class RecordingOwnerBootstrapStatusStore : IOwnerBootstrapStatusStore
{
    public List<OwnerBootstrapStatus> Statuses { get; } = [];

    public Task WriteAsync(
        OwnerAuthority authority,
        OwnerBootstrapRequest request,
        OwnerBootstrapStatus status,
        CancellationToken cancellationToken)
    {
        Statuses.Add(status);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingGitHubAppBootstrapper : IGitHubAppBootstrapper
{
    public bool Completed { get; private set; }

    public Task<OwnerGitHubCredentials> GetOrCreateAsync(
        OwnerBootstrapRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new OwnerGitHubCredentials("7", "42", "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----"));

    public Task CompleteAsync(OwnerBootstrapRequest request, CancellationToken cancellationToken)
    {
        Completed = true;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingOwnerCredentialStore : IOwnerCredentialStore
{
    public string? KeyVaultUri { get; private set; }

    public Task WriteAsync(
        string keyVaultUri,
        OwnerGitHubCredentials credentials,
        CancellationToken cancellationToken)
    {
        KeyVaultUri = keyVaultUri;
        return Task.CompletedTask;
    }
}
