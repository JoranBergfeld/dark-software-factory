using System.Text.Json;
using Dsf.Core.Instances;

namespace Dsf.Cli;

internal static class DecideConfigurationFile
{
    public static DecideDeploymentSettings Read(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<DecideDeploymentSettings>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new InstanceDefinitionException("Decide configuration must be a JSON object.");
            settings.Validate();
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InstanceDefinitionException($"Cannot read --decide-config '{path}': {exception.Message}", exception);
        }
    }
}
