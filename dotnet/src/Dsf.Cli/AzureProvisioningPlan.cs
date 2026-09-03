using Dsf.Core.Instances;

namespace Dsf.Cli;

internal interface IAzureProvisioningClient
{
    Task<AzureResourceGroupProvisioningResult> EnsureResourceGroupAsync(
        EnsureResourceGroupRequest request,
        CancellationToken cancellationToken);

    Task<AzureTopologyProvisioningResult> DeployTopologyAsync(
        DeployTopologyRequest request,
        CancellationToken cancellationToken);

    Task<AzureSreAgentProvisioningResult> DeploySreAgentAsync(
        DeploySreAgentRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Plans (and executes) the Azure side of `dsf new`: the dedicated resource
/// group, the backing-services topology from <c>infra/main.bicep</c>, and the
/// Azure SRE Agent from <c>infra/sre-agent.bicep</c> — preserving the resource
/// graph, managed identity, role assignments, configuration/model resources,
/// and SRE Agent behavior already deployed by the Python provisioner.
/// </summary>
internal sealed record AzureProvisioningPlan(IReadOnlyList<AzureProvisioningRequest> Requests)
{
    public static AzureProvisioningPlan Build(InstanceDefinition definition, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(repoRoot);

        var azure = definition.Azure;
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["project"] = "dark-software-factory",
            ["managed-by"] = "dsf",
            ["product"] = definition.Product.Key,
            ["component"] = "backing-services",
        };

        return new AzureProvisioningPlan(
            [
                new EnsureResourceGroupRequest(azure.ResourceGroup, azure.Location, tags),
                new DeployTopologyRequest(
                    azure.ResourceGroup,
                    azure.DeploymentName,
                    Path.Combine(repoRoot, "infra", "main.bicep"),
                    azure.NamePrefix,
                    definition.Product.Environment,
                    azure.Location,
                    definition.Product.Key,
                    definition.Runtime.Image,
                    definition.GitHub.AppId ?? string.Empty,
                    definition.GitHub.InstallationId ?? string.Empty,
                    definition.GitHub.FullName(),
                    AllowPublicNetworkAccess: true,
                    definition.Governance.AdminPrincipalId),
                new DeploySreAgentRequest(
                    azure.SreAgent.Location,
                    $"dsf-sre-{definition.Product.Key}",
                    Path.Combine(repoRoot, "infra", "sre-agent.bicep"),
                    definition.Product.Key,
                    azure.SreAgent.Name,
                    azure.SreAgent.ResourceGroup,
                    azure.SreAgent.MonitoredResourceGroups,
                    AppInsightsId: string.Empty,
                    LogAnalyticsId: string.Empty,
                    PermissionLevel: "Reader",
                    definition.Governance.AdminPrincipalId),
            ]);
    }

    public async Task<AzureProvisioningResult> ExecuteAsync(
        IAzureProvisioningClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        AzureResourceGroupProvisioningResult? resourceGroup = null;
        AzureTopologyProvisioningResult? topology = null;
        AzureSreAgentProvisioningResult? sreAgent = null;

        foreach (var request in Requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (request)
            {
                case EnsureResourceGroupRequest resourceGroupRequest:
                    resourceGroup = await client.EnsureResourceGroupAsync(
                        resourceGroupRequest,
                        cancellationToken);
                    break;
                case DeployTopologyRequest topologyRequest:
                    topology = await client.DeployTopologyAsync(topologyRequest, cancellationToken);
                    break;
                case DeploySreAgentRequest sreAgentRequest:
                    // The SRE Agent template needs the Application Insights + Log
                    // Analytics resource ids the topology deployment just produced —
                    // known only once provision_azure has actually run, never at plan time.
                    var effectiveSreAgent = sreAgentRequest with
                    {
                        AppInsightsId = topology?.Outputs.GetValueOrDefault("appInsightsId")
                            ?? sreAgentRequest.AppInsightsId,
                        LogAnalyticsId = topology?.Outputs.GetValueOrDefault("logAnalyticsId")
                            ?? sreAgentRequest.LogAnalyticsId,
                    };
                    sreAgent = await client.DeploySreAgentAsync(effectiveSreAgent, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unrecognized Azure provisioning request type '{request.GetType().Name}': "
                        + "no execution branch is wired up for it.");
            }
        }

        return new AzureProvisioningResult(resourceGroup, topology, sreAgent);
    }
}

internal abstract record AzureProvisioningRequest(string Method);

internal sealed record EnsureResourceGroupRequest(
    string ResourceGroup,
    string Location,
    IReadOnlyDictionary<string, string> Tags)
    : AzureProvisioningRequest("ensure_resource_group");

internal sealed record DeployTopologyRequest(
    string ResourceGroup,
    string DeploymentName,
    string BicepPath,
    string NamePrefix,
    string EnvironmentName,
    string Location,
    string Product,
    string RuntimeImage,
    string GitHubAppId,
    string GitHubInstallationId,
    string GitHubRepository,
    bool AllowPublicNetworkAccess,
    string? AdminPrincipalId)
    : AzureProvisioningRequest("deploy_topology");

internal sealed record DeploySreAgentRequest(
    string Location,
    string DeploymentName,
    string BicepPath,
    string Product,
    string AgentName,
    string AgentResourceGroup,
    IReadOnlyList<string> TargetResourceGroups,
    string AppInsightsId,
    string LogAnalyticsId,
    string PermissionLevel,
    string? AdminPrincipalId)
    : AzureProvisioningRequest("deploy_sre_agent");

internal sealed record AzureResourceGroupProvisioningResult(string Name);

/// <summary>Non-secret deployment outputs only: endpoints, resource names/ids — never key values.</summary>
internal sealed record AzureTopologyProvisioningResult(IReadOnlyDictionary<string, string> Outputs);

internal sealed record AzureSreAgentProvisioningResult(
    string? AgentId,
    string? AgentEndpoint,
    string? AgentPrincipalId);

internal sealed record AzureProvisioningResult(
    AzureResourceGroupProvisioningResult? ResourceGroup,
    AzureTopologyProvisioningResult? Topology,
    AzureSreAgentProvisioningResult? SreAgent)
{
    /// <summary>
    /// Folds discovered outputs into the clean instance definition. Only endpoints,
    /// resource names, and resource ids land here — the topology client already
    /// excludes secret-bearing outputs (e.g. the App Insights connection string)
    /// before they ever reach this result.
    /// </summary>
    public InstanceDefinition ApplyTo(InstanceDefinition definition)
    {
        var outputs = new Dictionary<string, string>(definition.Azure.Outputs, StringComparer.Ordinal);
        if (Topology is not null)
        {
            foreach (var (key, value) in Topology.Outputs)
            {
                outputs[key] = value;
            }
        }

        if (SreAgent?.AgentId is not null)
        {
            outputs["sreAgentId"] = SreAgent.AgentId;
        }

        if (SreAgent?.AgentEndpoint is not null)
        {
            outputs["sreAgentEndpoint"] = SreAgent.AgentEndpoint;
        }

        if (SreAgent?.AgentPrincipalId is not null)
        {
            outputs["sreAgentPrincipalId"] = SreAgent.AgentPrincipalId;
        }

        return definition with { Azure = definition.Azure with { Outputs = outputs } };
    }
}
