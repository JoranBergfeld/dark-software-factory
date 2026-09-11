namespace Dsf.Core.Runtime;

/// <summary>Nonsecret integration values allowed in the owner runtime index.</summary>
public static class RuntimeIntegrationConfiguration
{
    public static IReadOnlyList<string> Keys { get; } =
    [
        RuntimeIntegrationSettings.CosmosDatabase,
        RuntimeIntegrationSettings.CosmosContainer,
        RuntimeIntegrationSettings.CosmosLearningContainer,
        RuntimeIntegrationSettings.SourceAgentEndpointTemplate,
        .. SourceAgentKinds.Known.Select(RuntimeIntegrationSettings.SourceAgentEndpoint),
        .. JurySettings.Keys,
        .. DeliberationSettings.Keys,
    ];
}
