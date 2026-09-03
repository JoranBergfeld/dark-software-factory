using System.Diagnostics;
using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// Reads the owner App Configuration runtime index via the authenticated Azure
/// CLI, matching the shape <c>dsf new</c> (<c>Dsf.Cli</c>'s
/// <c>AzureCliAppConfigurationClient.PublishRuntimeIndexAsync</c>) publishes: entries
/// labeled with the product key, keyed by the exact env var names
/// <see cref="RuntimeSettingsComposer"/> reads. This is the runtime host's only
/// production implementation of <see cref="IOwnerRuntimeIndexReader"/>; tests use a
/// deterministic double instead of shelling out to a real Azure subscription.
/// </summary>
internal sealed class AzureCliOwnerRuntimeIndexReader : IOwnerRuntimeIndexReader
{
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string ownerAppConfigEndpoint,
        string product,
        CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "appconfig", "kv", "list",
            "--endpoint", ownerAppConfigEndpoint,
            "--auth-mode", "login",
            "--label", product,
            "-o", "json",
        };

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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"az CLI could not be started to read the owner runtime index (is it installed and on PATH?): {exception.Message}",
                exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr}");
        }

        using var document = JsonDocument.Parse(stdout);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"App Configuration at '{ownerAppConfigEndpoint}' returned an invalid runtime index for product '{product}'.");
        }

        var values = document.RootElement.EnumerateArray()
            .Where(entry => entry.TryGetProperty("key", out _) && entry.TryGetProperty("value", out _))
            .ToDictionary(
                entry => entry.GetProperty("key").GetString() ?? string.Empty,
                entry => entry.GetProperty("value").GetString() ?? string.Empty,
                StringComparer.Ordinal);

        if (values.Count == 0)
        {
            throw new InvalidOperationException(
                $"product '{product}' has no published runtime index in the owner App Configuration at '{ownerAppConfigEndpoint}'.");
        }

        return values;
    }
}
