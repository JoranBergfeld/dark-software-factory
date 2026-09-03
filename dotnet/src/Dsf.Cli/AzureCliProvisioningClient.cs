using System.Diagnostics;
using System.Text.Json;

namespace Dsf.Cli;

/// <summary>Result of invoking one <c>az</c> CLI command.</summary>
internal sealed record AzureCliInvocationResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs one <c>az</c> CLI invocation. Abstracted so tests can record the exact argument
/// shape and return canned JSON without touching a live Azure subscription.
/// </summary>
internal interface IAzureCliRunner
{
    Task<AzureCliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>Shells out to the real <c>az</c> CLI on PATH.</summary>
internal sealed class SystemAzureCliRunner : IAzureCliRunner
{
    public async Task<AzureCliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("az")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new InvalidOperationException(
                $"az CLI could not be started (is it installed and on PATH?): {exception.Message}",
                exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new AzureCliInvocationResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}

/// <summary>
/// Provisions the Azure side of `dsf new` via the <c>az</c> CLI: the dedicated resource
/// group, the backing-services topology (<c>infra/main.bicep</c>), and the Azure SRE
/// Agent (<c>infra/sre-agent.bicep</c>). Only non-secret deployment outputs (endpoints,
/// resource names/ids) are surfaced to callers — never secret values.
/// </summary>
internal sealed class AzureCliProvisioningClient : IAzureProvisioningClient
{
    /// <summary>
    /// Allowlist of `infra/main.bicep` outputs safe to persist in the clean instance
    /// definition. Deliberately excludes `appInsightsConnectionString`: a connection
    /// string embeds an instrumentation key, so only the App Insights resource id
    /// (`appInsightsId`) is kept.
    /// </summary>
    private static readonly IReadOnlyCollection<string> AllowedTopologyOutputs =
        [
            "cosmosEndpoint",
            "appConfigEndpoint",
            "keyVaultUri",
            "keyVaultName",
            "openaiEndpoint",
            "openaiDeployment",
            "openaiEmbeddingDeployment",
            "runtimePrincipalId",
            "orchestratorAppName",
            "appInsightsId",
            "logAnalyticsId",
        ];

    private readonly IAzureCliRunner runner;

    internal AzureCliProvisioningClient(IAzureCliRunner runner)
    {
        this.runner = runner;
    }

    internal static AzureCliProvisioningClient FromEnvironment() => new(new SystemAzureCliRunner());

    public async Task<AzureResourceGroupProvisioningResult> EnsureResourceGroupAsync(
        EnsureResourceGroupRequest request,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "group", "create",
            "--name", request.ResourceGroup,
            "--location", request.Location,
            "--tags",
        };
        arguments.AddRange(request.Tags.Select(tag => $"{tag.Key}={tag.Value}"));

        await RunAsync(arguments, cancellationToken);
        return new AzureResourceGroupProvisioningResult(request.ResourceGroup);
    }

    public async Task<AzureTopologyProvisioningResult> DeployTopologyAsync(
        DeployTopologyRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.BicepPath))
        {
            throw new InvalidOperationException(
                $"Azure provisioning requires the backing-services template at '{request.BicepPath}'.");
        }

        var parameters = new List<string>
        {
            $"namePrefix={request.NamePrefix}",
            $"environmentName={request.EnvironmentName}",
            $"location={request.Location}",
            $"product={request.Product}",
            $"runtimeImage={request.RuntimeImage}",
            $"githubAppId={request.GitHubAppId}",
            $"githubInstallationId={request.GitHubInstallationId}",
            $"githubRepository={request.GitHubRepository}",
            $"allowPublicNetworkAccess={(request.AllowPublicNetworkAccess ? "true" : "false")}",
        };
        if (!string.IsNullOrWhiteSpace(request.AdminPrincipalId))
        {
            parameters.Add($"adminPrincipalId={request.AdminPrincipalId}");
        }

        var arguments = new List<string>
        {
            "deployment", "group", "create",
            "-g", request.ResourceGroup,
            "-n", request.DeploymentName,
            "-f", request.BicepPath,
            "-p",
        };
        arguments.AddRange(parameters);
        arguments.AddRange(["--query", "properties.outputs", "-o", "json"]);

        var result = await RunAsync(arguments, cancellationToken);
        var outputs = ParseOutputs(result.StandardOutput)
            .Where(entry => AllowedTopologyOutputs.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return new AzureTopologyProvisioningResult(outputs);
    }

    public async Task<AzureSreAgentProvisioningResult> DeploySreAgentAsync(
        DeploySreAgentRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.BicepPath))
        {
            throw new InvalidOperationException(
                $"Azure provisioning requires the SRE Agent template at '{request.BicepPath}'.");
        }

        if (string.IsNullOrWhiteSpace(request.AppInsightsId) || string.IsNullOrWhiteSpace(request.LogAnalyticsId))
        {
            throw new InvalidOperationException(
                "Azure SRE Agent provisioning requires appInsightsId and logAnalyticsId from the "
                + "backing-services deployment outputs; provision_azure must run first.");
        }

        var parameters = new List<string>
        {
            $"product={request.Product}",
            $"agentName={request.AgentName}",
            $"sreAgentLocation={request.Location}",
            $"agentResourceGroup={request.AgentResourceGroup}",
            $"targetResourceGroups={JsonSerializer.Serialize(request.TargetResourceGroups)}",
            $"appInsightsId={request.AppInsightsId}",
            $"logAnalyticsId={request.LogAnalyticsId}",
            $"permissionLevel={request.PermissionLevel}",
        };
        if (!string.IsNullOrWhiteSpace(request.AdminPrincipalId))
        {
            parameters.Add($"ownerPrincipalId={request.AdminPrincipalId}");
        }

        var arguments = new List<string>
        {
            "deployment", "sub", "create",
            "-l", request.Location,
            "-n", request.DeploymentName,
            "-f", request.BicepPath,
            "-p",
        };
        arguments.AddRange(parameters);
        arguments.AddRange(["--query", "properties.outputs", "-o", "json"]);

        var result = await RunAsync(arguments, cancellationToken);
        var outputs = ParseOutputs(result.StandardOutput);
        return new AzureSreAgentProvisioningResult(
            outputs.GetValueOrDefault("agentId"),
            outputs.GetValueOrDefault("agentEndpoint"),
            outputs.GetValueOrDefault("agentPrincipalId"));
    }

    private async Task<AzureCliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        AzureCliInvocationResult result;
        try
        {
            result = await runner.RunAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    /// <summary>
    /// Unwraps an `az ... --query properties.outputs -o json` document — each entry is
    /// either a plain value or a `{"type": ..., "value": ...}` envelope — into a flat
    /// string map. Absent/empty stdout (e.g. a recorder stub) yields an empty map.
    /// </summary>
    private static Dictionary<string, string> ParseOutputs(string standardOutput)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return outputs;
        }

        using var document = JsonDocument.Parse(standardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return outputs;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("value", out var envelopeValue)
                ? envelopeValue
                : property.Value;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            };
            if (text is not null)
            {
                outputs[property.Name] = text;
            }
        }

        return outputs;
    }
}
