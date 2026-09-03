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
        string namePrefix,
        DateTimeOffset generatedAt)
    {
        var resourceGroup = $"rg-dsf-{product}";

        return new InstanceDefinition
        {
            Product = new ProductSettings
            {
                Key = product,
                Environment = environment,
                CreationMaturity = creationMaturity,
            },
            Runtime = new RuntimeSettings
            {
                Target = runtimeTarget,
                Image = DefaultRuntimeImage,
            },
            Governance = new GovernanceSettings(),
            GitHub = new GitHubSettings
            {
                Owner = owner,
                Repository = string.IsNullOrWhiteSpace(repo) ? product : repo,
                Visibility = visibility,
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
            },
            Status = new InstanceStatus
            {
                State = InstanceState.Planned,
                GeneratedAt = generatedAt,
            },
        };
    }
}
