using System.Text.Json;

namespace Dsf.Cli;

/// <summary>Resolves only templates shipped alongside this CLI build, never from the working directory.</summary>
internal sealed class ProvisioningAssets
{
    private static readonly HashSet<string> Entrypoints = new(StringComparer.Ordinal)
    {
        "owner-keyvault.json",
        "owner-secrets.json",
        "main.json",
        "sre-agent.json",
        "copy-owner-secret.json",
    };

    private readonly string directory;

    internal ProvisioningAssets(string? baseDirectory = null)
    {
        directory = Path.Combine(Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory), "assets", "infra");
    }

    internal string Resolve(string entrypoint)
    {
        if (!Entrypoints.Contains(entrypoint))
        {
            throw new InvalidOperationException(
                $"Unknown provisioning template '{entrypoint}' in '{directory}'. "
                + "Reinstall the DSF CLI or rebuild it from source.");
        }

        var path = Path.Combine(directory, entrypoint);
        ValidateTemplate(path);
        return path;
    }

    internal static void ValidateTemplate(string path)
    {
        try
        {
            if (!Entrypoints.Contains(Path.GetFileName(path)))
            {
                throw new InvalidDataException("Not a shipped ARM template entrypoint.");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("$schema", out var schema)
                || schema.ValueKind != JsonValueKind.String
                || schema.GetString() is not (
                    "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"
                    or "https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#")
                || !root.TryGetProperty("contentVersion", out var version)
                || version.ValueKind != JsonValueKind.String
                || !Version.TryParse(version.GetString(), out var parsedVersion)
                || parsedVersion.Revision < 0
                || !root.TryGetProperty("resources", out var resources)
                || resources.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)
                || HasTemplateLink(root))
            {
                throw new InvalidDataException("Expected a self-contained ARM deployment template.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException(
                $"Provisioning template '{path}' is missing, unreadable, or invalid. "
                + "Reinstall the DSF CLI or rebuild it from source.",
                exception);
        }
    }

    private static bool HasTemplateLink(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(
            property => property.NameEquals("templateLink") || HasTemplateLink(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasTemplateLink),
        _ => false,
    };

    internal static async Task CheckAzureCliAsync(IAzureCliRunner runner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await runner.RunAsync(["version", "--output", "json"], cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"az version exited {result.ExitCode}: {result.StandardError}");
            }

            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("azure-cli", out var version)
                || version.ValueKind != JsonValueKind.String
                || !Version.TryParse(version.GetString(), out _))
            {
                throw new InvalidOperationException("az version returned no valid azure-cli version.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException)
        {
            throw new InvalidOperationException(
                $"Azure CLI preflight failed. Install or repair Azure CLI and ensure 'az' is on PATH. {exception.Message}",
                exception);
        }
    }
}
