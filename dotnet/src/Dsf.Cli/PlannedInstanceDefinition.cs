using Dsf.Core.Instances;

namespace Dsf.Cli;

/// <summary>
/// Builds the clean, versioned instance definition that `dsf new` persists.
/// Only configuration lands here: the execution plan itself stays on stdout.
/// </summary>
internal static class PlannedInstanceDefinition
{
    private const string DefaultRuntimeImage = "ghcr.io/joranbergfeld/dsf-runtime:latest";

    internal static InstanceDefinition Build(
        string product,
        string owner,
        string repo,
        string visibility,
        string runtimeTarget,
        string environment,
        string location,
        string creationMaturity,
        string operationMaturity,
        string namePrefix,
        string? ownerKeyVaultUri,
        string? ownerAppConfigEndpoint,
        string? adminPrincipalId,
        string? githubAppId,
        string? githubInstallationId,
        DateTimeOffset generatedAt,
        InstanceDefinition? existing = null)
    {
        var resourceGroup = $"rg-dsf-{product}";
        var repoName = string.IsNullOrWhiteSpace(repo) ? product : repo;
        var reusableGitHubIdentity = existing?.GitHub.Owner == owner
            && existing.GitHub.Repository == repoName
            ? existing.GitHub
            : null;

        return new InstanceDefinition
        {
            Product = new ProductSettings
            {
                Key = product,
                Environment = environment,
                CreationMaturity = creationMaturity,
                OperationMaturity = operationMaturity,
            },
            Runtime = new RuntimeSettings
            {
                Target = runtimeTarget,
                Image = DefaultRuntimeImage,
            },
            Governance = new GovernanceSettings
            {
                AdminPrincipalId = Trimmed(adminPrincipalId),
            },
            GitHub = new GitHubSettings
            {
                Owner = owner,
                Repository = repoName,
                Visibility = visibility,
                AppId = Trimmed(githubAppId) ?? reusableGitHubIdentity?.AppId,
                InstallationId = Trimmed(githubInstallationId)
                    ?? reusableGitHubIdentity?.InstallationId,
                RepositoryId = reusableGitHubIdentity?.RepositoryId,
                DefaultBranch = reusableGitHubIdentity?.DefaultBranch ?? "main",
                BranchProtectionRulesetId = reusableGitHubIdentity?.BranchProtectionRulesetId,
            },
            Azure = new AzureSettings
            {
                Location = location,
                NamePrefix = namePrefix,
                ResourceGroup = resourceGroup,
                DeploymentName = $"dsf-{product}",
                SreAgent = new SreAgentSettings
                {
                    Name = $"dsf-sre-{product}",
                    ResourceGroup = $"rg-dsf-sre-{product}",
                    Location = location,
                    MonitoredResourceGroups = [resourceGroup],
                },
                OwnerAuthority = new OwnerAuthoritySettings
                {
                    KeyVaultUri = Trimmed(ownerKeyVaultUri),
                    AppConfigEndpoint = Trimmed(ownerAppConfigEndpoint),
                },
            },
            Status = new InstanceStatus
            {
                State = InstanceState.Planned,
                GeneratedAt = generatedAt,
            },
        };
    }

    /// <summary>Absent options arrive as empty strings; they are stored as absent, not as blanks.</summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
