using Dsf.Cli;
using Dsf.Core.Instances;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class AzureProvisioningPlaneTests
{
    [Fact]
    public async Task Dry_run_prints_azure_operations_without_mutating_azure()
    {
        var terminal = PlainTerminal();
        var azure = new RecordingAzureProvisioningClient();

        var exitCode = await CliApplication.InvokeAsync(
            [
                "new", "--product", "paritydemo", "--owner", "acme",
                "--dry-run", "--config-root", ArtifactRoot(),
            ],
            CancellationToken.None,
            terminal,
            new RecordingGitHubProvisioningClient(),
            azure);

        Assert.Equal(0, exitCode);
        Assert.Empty(azure.Requests);
        Assert.Contains("create_resource_group", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("provision_azure", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("deploy_sre_agent", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_uses_expected_request_shapes()
    {
        var plan = AzureProvisioningPlan.Build(SampleDefinition(), "/repo-root");

        Assert.Collection(
            plan.Requests,
            request =>
            {
                var resourceGroup = Assert.IsType<EnsureResourceGroupRequest>(request);
                Assert.Equal("ensure_resource_group", resourceGroup.Method);
                Assert.Equal("rg-dsf-paritydemo", resourceGroup.ResourceGroup);
                Assert.Equal("swedencentral", resourceGroup.Location);
                Assert.Equal("paritydemo", resourceGroup.Tags["product"]);
                Assert.Equal("dark-software-factory", resourceGroup.Tags["project"]);
                Assert.Equal("backing-services", resourceGroup.Tags["component"]);
            },
            request =>
            {
                var topology = Assert.IsType<DeployTopologyRequest>(request);
                Assert.Equal("deploy_topology", topology.Method);
                Assert.Equal("rg-dsf-paritydemo", topology.ResourceGroup);
                Assert.Equal("dsf-paritydemo", topology.DeploymentName);
                Assert.Equal("/repo-root/infra/main.bicep".Replace('/', Path.DirectorySeparatorChar), topology.BicepPath);
                Assert.Equal("parityde0000", topology.NamePrefix);
                Assert.Equal("dev", topology.EnvironmentName);
                Assert.Equal("swedencentral", topology.Location);
                Assert.Equal("paritydemo", topology.Product);
                Assert.Equal("7", topology.GitHubAppId);
                Assert.Equal("42", topology.GitHubInstallationId);
                Assert.Equal("acme/paritydemo", topology.GitHubRepository);
                Assert.True(topology.AllowPublicNetworkAccess);
            },
            request =>
            {
                var sreAgent = Assert.IsType<DeploySreAgentRequest>(request);
                Assert.Equal("deploy_sre_agent", sreAgent.Method);
                Assert.Equal("dsf-sre-paritydemo", sreAgent.AgentName);
                Assert.Equal("rg-dsf-sre-paritydemo", sreAgent.AgentResourceGroup);
                Assert.Equal(["rg-dsf-paritydemo"], sreAgent.TargetResourceGroups);
                Assert.Equal("/repo-root/infra/sre-agent.bicep".Replace('/', Path.DirectorySeparatorChar), sreAgent.BicepPath);
                // Not known until the topology deployment runs -- ExecuteAsync threads these in.
                Assert.Equal("", sreAgent.AppInsightsId);
                Assert.Equal("", sreAgent.LogAnalyticsId);
            });
    }

    [Fact]
    public async Task Execution_threads_topology_outputs_into_the_sre_agent_request()
    {
        var azure = new RecordingAzureProvisioningClient();

        await AzureProvisioningPlan.Build(SampleDefinition(), "/repo-root").ExecuteAsync(azure, CancellationToken.None);

        var sreRequest = Assert.IsType<DeploySreAgentRequest>(azure.Requests[2]);
        Assert.Equal(azure.TopologyOutputs["appInsightsId"], sreRequest.AppInsightsId);
        Assert.Equal(azure.TopologyOutputs["logAnalyticsId"], sreRequest.LogAnalyticsId);
    }

    [Fact]
    public async Task Executed_plan_captures_outputs_without_secret_values()
    {
        var azure = new RecordingAzureProvisioningClient();

        var result = await AzureProvisioningPlan.Build(SampleDefinition(), "/repo-root")
            .ExecuteAsync(azure, CancellationToken.None);
        var updated = result.ApplyTo(SampleDefinition());
        var json = InstanceDefinitions.Serialize(updated);

        Assert.Equal(
            "https://cosmos-paritydemo.documents.azure.com:443/",
            updated.Azure.Outputs["cosmosEndpoint"]);
        Assert.Equal(
            azure.AgentId,
            updated.Azure.Outputs["sreAgentId"]);
        Assert.Equal(
            azure.AgentEndpoint,
            updated.Azure.Outputs["sreAgentEndpoint"]);
        Assert.Equal(
            azure.AgentPrincipalId,
            updated.Azure.Outputs["sreAgentPrincipalId"]);
        foreach (var forbidden in new[] { "InstrumentationKey=", "BEGIN RSA", "\"steps\"", "\"plan\"" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Execution_honors_cancellation_token_during_azure_provisioning()
    {
        var root = ArtifactRoot();
        try
        {
            using var cts = new CancellationTokenSource();
            var azure = new RecordingAzureProvisioningClient
            {
                OnEnsureResourceGroup = () => cts.Cancel(),
            };

            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "paritydemo", "--owner", "acme",
                    "--config-root", root,
                ],
                cts.Token,
                PlainTerminal(),
                new RecordingGitHubProvisioningClient(),
                azure);

            Assert.Equal(CliApplication.CanceledExitCode, exitCode);
            var manifestPath = InstanceDefinitions.PathFor(root, "paritydemo");
            Assert.False(File.Exists(manifestPath), "Manifest should not be written when canceled.");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Executed_plan_via_cli_persists_azure_outputs_into_the_instance_definition()
    {
        var root = ArtifactRoot();
        try
        {
            var azure = new RecordingAzureProvisioningClient();

            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "paritydemo", "--owner", "acme",
                    "--config-root", root,
                ],
                CancellationToken.None,
                PlainTerminal(),
                new RecordingGitHubProvisioningClient(),
                azure);

            Assert.Equal(0, exitCode);
            var written = InstanceDefinitions.Read(InstanceDefinitions.PathFor(root, "paritydemo"));
            Assert.Equal(
                "https://appcs-paritydemo.azconfig.io",
                written.Azure.Outputs["appConfigEndpoint"]);
            Assert.Equal(azure.AgentEndpoint, written.Azure.Outputs["sreAgentEndpoint"]);
            Assert.Equal(InstanceState.Executed, written.Status.State);
            Assert.NotEmpty(azure.Requests);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static InstanceDefinition SampleDefinition() => new()
    {
        Product = new ProductSettings { Key = "paritydemo" },
        Runtime = new RuntimeSettings(),
        Governance = new GovernanceSettings(),
        GitHub = new GitHubSettings
        {
            Owner = "acme",
            Repository = "paritydemo",
            Visibility = "private",
            AppId = "7",
            InstallationId = "42",
        },
        Azure = new AzureSettings
        {
            NamePrefix = "parityde0000",
            ResourceGroup = "rg-dsf-paritydemo",
            DeploymentName = "dsf-paritydemo",
            SreAgent = new SreAgentSettings
            {
                Name = "dsf-sre-paritydemo",
                ResourceGroup = "rg-dsf-sre-paritydemo",
                MonitoredResourceGroups = ["rg-dsf-paritydemo"],
            },
        },
        Status = new InstanceStatus { GeneratedAt = DateTimeOffset.UnixEpoch },
    };

    private static ScriptedTerminal PlainTerminal() =>
        new(new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false), []);

    private static string ArtifactRoot()
    {
        var root = FindSolutionRoot();
        return Path.Combine(root, ".test-artifacts", "azure-plane", Guid.NewGuid().ToString("N"));
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dsf.sln")))
        {
            dir = dir.Parent;
        }

        return (dir ?? throw new InvalidOperationException("Could not locate Dsf.sln.")).FullName;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class RecordingAzureProvisioningClient : IAzureProvisioningClient
{
    public List<AzureProvisioningRequest> Requests { get; } = [];

    public IReadOnlyDictionary<string, string> TopologyOutputs { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cosmosEndpoint"] = "https://cosmos-paritydemo.documents.azure.com:443/",
        ["appConfigEndpoint"] = "https://appcs-paritydemo.azconfig.io",
        ["keyVaultUri"] = "https://kv-paritydemo.vault.azure.net/",
        ["appInsightsId"] = "/subscriptions/x/resourceGroups/rg/providers/Microsoft.Insights/components/appi-paritydemo",
        ["logAnalyticsId"] = "/subscriptions/x/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/log-paritydemo",
    };

    public string? AgentId { get; init; } =
        "/subscriptions/x/resourceGroups/rg-dsf-sre-paritydemo/providers/Microsoft.SecurityCopilot/sreAgents/dsf-sre-paritydemo";

    public string? AgentEndpoint { get; init; } = "https://dsf-sre-paritydemo.sre.azure.com";

    public string? AgentPrincipalId { get; init; } = "33333333-4444-5555-6666-777777777777";

    public Action? OnEnsureResourceGroup { get; init; }

    public Task<AzureResourceGroupProvisioningResult> EnsureResourceGroupAsync(
        EnsureResourceGroupRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        OnEnsureResourceGroup?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AzureResourceGroupProvisioningResult(request.ResourceGroup));
    }

    public Task<AzureTopologyProvisioningResult> DeployTopologyAsync(
        DeployTopologyRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AzureTopologyProvisioningResult(TopologyOutputs));
    }

    public Task<AzureSreAgentProvisioningResult> DeploySreAgentAsync(
        DeploySreAgentRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AzureSreAgentProvisioningResult(AgentId, AgentEndpoint, AgentPrincipalId));
    }
}
