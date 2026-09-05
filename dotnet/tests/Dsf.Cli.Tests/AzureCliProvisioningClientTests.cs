using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class AzureCliProvisioningClientTests
{
    [Fact]
    public async Task EnsureResourceGroup_invokes_az_group_create_with_tags()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, "", ""));
        var client = new AzureCliProvisioningClient(runner);

        var result = await client.EnsureResourceGroupAsync(
            new EnsureResourceGroupRequest(
                "rg-dsf-paritydemo",
                "swedencentral",
                new Dictionary<string, string>
                {
                    ["project"] = "dark-software-factory",
                    ["product"] = "paritydemo",
                }),
            CancellationToken.None);

        Assert.Equal("rg-dsf-paritydemo", result.Name);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(
            [
                "group", "create",
                "--name", "rg-dsf-paritydemo",
                "--location", "swedencentral",
                "--tags", "project=dark-software-factory", "product=paritydemo",
            ],
            invocation);
    }

    [Fact]
    public async Task EnsureResourceGroup_failure_fails_loudly_with_stderr()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(1, "", "boom"));
        var client = new AzureCliProvisioningClient(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnsureResourceGroupAsync(
                new EnsureResourceGroupRequest("rg-dsf-x", "swedencentral", new Dictionary<string, string>()),
                CancellationToken.None));

        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployTopology_missing_bicep_template_fails_loudly_before_any_az_invocation()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, "{}", ""));
        var client = new AzureCliProvisioningClient(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DeployTopologyAsync(SampleTopologyRequest("/nonexistent/main.bicep"), CancellationToken.None));

        Assert.Contains("/nonexistent/main.bicep", error.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task DeployTopology_builds_the_az_deployment_group_create_command_shape()
    {
        var bicepPath = TempBicepFile();
        try
        {
            var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, "{}", ""));
            var client = new AzureCliProvisioningClient(runner);

            await client.DeployTopologyAsync(SampleTopologyRequest(bicepPath), CancellationToken.None);

            var invocation = Assert.Single(runner.Invocations);
            Assert.Equal(
                [
                    "deployment", "group", "create",
                    "-g", "rg-dsf-paritydemo",
                    "-n", "dsf-paritydemo",
                    "-f", bicepPath,
                    "-p",
                    "namePrefix=parityde0000",
                    "environmentName=dev",
                    "location=swedencentral",
                    "product=paritydemo",
                    "runtimeImage=ghcr.io/joranbergfeld/dsf-runtime:latest",
                    "githubAppId=7",
                    "githubInstallationId=42",
                    "githubRepository=acme/paritydemo",
                    "allowPublicNetworkAccess=true",
                    "operationMaturity=low",
                    "adminPrincipalId=11111111-2222-3333-4444-555555555555",
                    "--query", "properties.outputs", "-o", "json",
                ],
                invocation);
        }
        finally
        {
            File.Delete(bicepPath);
        }
    }

    [Fact]
    public async Task DeployTopology_omits_admin_principal_id_parameter_when_absent()
    {
        var bicepPath = TempBicepFile();
        try
        {
            var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, "{}", ""));
            var client = new AzureCliProvisioningClient(runner);

            await client.DeployTopologyAsync(
                SampleTopologyRequest(bicepPath) with { AdminPrincipalId = null },
                CancellationToken.None);

            var invocation = Assert.Single(runner.Invocations);
            Assert.DoesNotContain(invocation, argument => argument.StartsWith("adminPrincipalId=", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(bicepPath);
        }
    }

    [Fact]
    public async Task DeployTopology_captures_only_allowlisted_non_secret_outputs()
    {
        var bicepPath = TempBicepFile();
        try
        {
            var stdout = """
                {
                  "cosmosEndpoint": {"type": "String", "value": "https://cosmos-paritydemo.documents.azure.com:443/"},
                  "appConfigEndpoint": {"type": "String", "value": "https://appcs-paritydemo.azconfig.io"},
                  "keyVaultUri": {"type": "String", "value": "https://kv-paritydemo.vault.azure.net/"},
                  "appInsightsConnectionString": {"type": "String", "value": "InstrumentationKey=super-secret-key;IngestionEndpoint=https://x"},
                  "appInsightsId": {"type": "String", "value": "/subscriptions/x/resourceGroups/rg/providers/Microsoft.Insights/components/appi-paritydemo"},
                  "logAnalyticsId": {"type": "String", "value": "/subscriptions/x/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/log-paritydemo"},
                  "openaiEndpoint": {"type": "String", "value": "https://foundry-paritydemo.openai.azure.com/"},
                  "openaiDeployment": {"type": "String", "value": "gpt-4o"},
                  "openaiEmbeddingDeployment": {"type": "String", "value": "text-embedding-3-large"},
                  "runtimePrincipalId": {"type": "String", "value": "22222222-3333-4444-5555-666666666666"},
                  "orchestratorAppName": {"type": "String", "value": "dsf-paritydemo-orchestrator"},
                  "keyVaultName": {"type": "String", "value": "kv-paritydemo"}
                }
                """;
            var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, stdout, ""));
            var client = new AzureCliProvisioningClient(runner);

            var result = await client.DeployTopologyAsync(SampleTopologyRequest(bicepPath), CancellationToken.None);

            Assert.Equal(
                "https://cosmos-paritydemo.documents.azure.com:443/",
                result.Outputs["cosmosEndpoint"]);
            Assert.Equal(
                "/subscriptions/x/resourceGroups/rg/providers/Microsoft.Insights/components/appi-paritydemo",
                result.Outputs["appInsightsId"]);
            Assert.False(
                result.Outputs.ContainsKey("appInsightsConnectionString"),
                "the App Insights connection string must never be captured, even though the deployment emits it.");
            Assert.DoesNotContain(
                result.Outputs.Values,
                value => value.Contains("super-secret-key", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(bicepPath);
        }
    }

    [Fact]
    public async Task DeploySreAgent_requires_app_insights_and_log_analytics_ids_from_topology_outputs()
    {
        var bicepPath = TempBicepFile();
        try
        {
            var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, "{}", ""));
            var client = new AzureCliProvisioningClient(runner);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DeploySreAgentAsync(SampleSreAgentRequest(bicepPath) with { AppInsightsId = "" }, CancellationToken.None));

            Assert.Contains("appInsightsId", error.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Invocations);
        }
        finally
        {
            File.Delete(bicepPath);
        }
    }

    [Fact]
    public async Task DeploySreAgent_builds_the_az_deployment_sub_create_command_shape()
    {
        var bicepPath = TempBicepFile();
        try
        {
            var stdout = """
                {
                  "agentId": {"type": "String", "value": "/subscriptions/x/resourceGroups/rg-dsf-sre-paritydemo/providers/Microsoft.SecurityCopilot/sreAgents/dsf-sre-paritydemo"},
                  "agentEndpoint": {"type": "String", "value": "https://dsf-sre-paritydemo.sre.azure.com"},
                  "agentPrincipalId": {"type": "String", "value": "33333333-4444-5555-6666-777777777777"}
                }
                """;
            var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(0, stdout, ""));
            var client = new AzureCliProvisioningClient(runner);

            var result = await client.DeploySreAgentAsync(SampleSreAgentRequest(bicepPath), CancellationToken.None);

            Assert.Equal(
                "/subscriptions/x/resourceGroups/rg-dsf-sre-paritydemo/providers/Microsoft.SecurityCopilot/sreAgents/dsf-sre-paritydemo",
                result.AgentId);
            Assert.Equal("https://dsf-sre-paritydemo.sre.azure.com", result.AgentEndpoint);
            Assert.Equal("33333333-4444-5555-6666-777777777777", result.AgentPrincipalId);

            var invocation = Assert.Single(runner.Invocations);
            Assert.Equal(
                [
                    "deployment", "sub", "create",
                    "-l", "swedencentral",
                    "-n", "dsf-sre-paritydemo",
                    "-f", bicepPath,
                    "-p",
                    "product=paritydemo",
                    "agentName=dsf-sre-paritydemo",
                    "sreAgentLocation=swedencentral",
                    "agentResourceGroup=rg-dsf-sre-paritydemo",
                    """targetResourceGroups=["rg-dsf-paritydemo"]""",
                    "appInsightsId=/subscriptions/x/resourceGroups/rg/providers/Microsoft.Insights/components/appi-paritydemo",
                    "logAnalyticsId=/subscriptions/x/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/log-paritydemo",
                    "operationMaturity=low",
                    "ownerPrincipalId=11111111-2222-3333-4444-555555555555",
                    "--query", "properties.outputs", "-o", "json",
                ],
                invocation);
        }
        finally
        {
            File.Delete(bicepPath);
        }
    }

    [Fact]
    public async Task Cancellation_propagates_before_any_az_invocation_completes()
    {
        var bicepPath = TempBicepFile();
        try
        {
            using var cts = new CancellationTokenSource();
            var runner = new RecordingAzureCliRunner(cancelBeforeReturn: cts);
            var client = new AzureCliProvisioningClient(runner);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => client.DeployTopologyAsync(SampleTopologyRequest(bicepPath), cts.Token));
        }
        finally
        {
            File.Delete(bicepPath);
        }
    }

    private static DeployTopologyRequest SampleTopologyRequest(string bicepPath) => new(
        "rg-dsf-paritydemo",
        "dsf-paritydemo",
        bicepPath,
        "parityde0000",
        "dev",
        "swedencentral",
        "paritydemo",
        "ghcr.io/joranbergfeld/dsf-runtime:latest",
        "7",
        "42",
        "acme/paritydemo",
        AllowPublicNetworkAccess: true,
        "11111111-2222-3333-4444-555555555555");

    private static DeploySreAgentRequest SampleSreAgentRequest(string bicepPath) => new(
        "swedencentral",
        "dsf-sre-paritydemo",
        bicepPath,
        "paritydemo",
        "dsf-sre-paritydemo",
        "rg-dsf-sre-paritydemo",
        ["rg-dsf-paritydemo"],
        "/subscriptions/x/resourceGroups/rg/providers/Microsoft.Insights/components/appi-paritydemo",
        "/subscriptions/x/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/log-paritydemo",
        "low",
        "11111111-2222-3333-4444-555555555555");

    private static string TempBicepFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsf-azure-cli-test-{Guid.NewGuid():N}.bicep");
        File.WriteAllText(path, "// test fixture\n");
        return path;
    }
}

/// <summary>Records every `az` invocation's argument list; returns canned results in order.</summary>
internal sealed class RecordingAzureCliRunner : IAzureCliRunner
{
    private readonly Queue<AzureCliInvocationResult> results;
    private readonly CancellationTokenSource? cancelBeforeReturn;

    public RecordingAzureCliRunner(params AzureCliInvocationResult[] results)
    {
        this.results = new Queue<AzureCliInvocationResult>(results);
    }

    public RecordingAzureCliRunner(CancellationTokenSource cancelBeforeReturn)
    {
        this.results = new Queue<AzureCliInvocationResult>();
        this.cancelBeforeReturn = cancelBeforeReturn;
    }

    public List<IReadOnlyList<string>> Invocations { get; } = [];

    public Task<AzureCliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Invocations.Add(arguments);
        if (cancelBeforeReturn is not null)
        {
            cancelBeforeReturn.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Task.FromResult(
            results.Count > 0 ? results.Dequeue() : new AzureCliInvocationResult(0, "{}", ""));
    }
}

/// <summary>
/// <c>SystemAzureCliRunner</c> shells out to the real <c>az</c> CLI. On cancellation it must
/// kill the whole process tree it spawned, not just stop awaiting it -- otherwise a
/// Ctrl+C during `dsf new` can leave `az` (and any Azure mutation it's mid-flight on)
/// running in the background. Exercised via the injectable <see cref="IManagedProcess"/>
/// seam so no real OS process needs to be started.
/// </summary>
public sealed class SystemAzureCliRunnerCancellationTests
{
    [Fact]
    public async Task Cancelling_the_token_kills_the_entire_spawned_process_tree()
    {
        var process = new FakeManagedProcess();
        var runner = new SystemAzureCliRunner(_ => process);
        using var cts = new CancellationTokenSource();

        var runTask = runner.RunAsync(["group", "create"], cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(process.Started);
        Assert.Equal(1, process.KillCallCount);
        Assert.True(process.LastKillWasEntireTree);
        Assert.True(process.Disposed, "the process must still be disposed after being killed.");
    }
}

/// <summary>Fake <see cref="IManagedProcess"/> whose exit is controlled entirely by the test.</summary>
internal sealed class FakeManagedProcess : IManagedProcess
{
    private readonly TaskCompletionSource exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Started { get; private set; }

    public int KillCallCount { get; private set; }

    public bool LastKillWasEntireTree { get; private set; }

    public bool Disposed { get; private set; }

    public int ExitCode => 0;

    public void Start() => Started = true;

    public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

    public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(() => exitSignal.TrySetCanceled(cancellationToken)))
        {
            await exitSignal.Task;
        }
    }

    public void Kill(bool entireProcessTree)
    {
        KillCallCount++;
        LastKillWasEntireTree = entireProcessTree;
        exitSignal.TrySetCanceled();
    }

    public void Dispose() => Disposed = true;
}
