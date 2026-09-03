using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

internal static class CosmosLearningStoreFactory
{
    public static ILearningStore Create(
        RuntimeSettings settings,
        IReadOnlyDictionary<string, string?> env,
        ICosmosDocumentGateway? cosmosGateway = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(env);

        var database = Read(env, RuntimeIntegrationSettings.CosmosDatabase);
        var container = Read(env, RuntimeIntegrationSettings.CosmosLearningContainer);
        return new CosmosLearningStore(
            settings.CosmosEndpoint.Trim(),
            database.Length > 0 ? database : RuntimeIntegrationSettings.DefaultCosmosDatabase,
            container.Length > 0 ? container : RuntimeIntegrationSettings.DefaultCosmosLearningContainer,
            settings.Product,
            cosmosGateway ?? new AzureCosmosDocumentGateway());
    }

    private static string Read(IReadOnlyDictionary<string, string?> env, string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
