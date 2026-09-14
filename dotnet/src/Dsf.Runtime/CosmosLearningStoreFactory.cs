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

        var (database, container) = Location(settings, env);
        return new CosmosLearningStore(
            settings.CosmosEndpoint.Trim(),
            database,
            container,
            settings.Product,
            cosmosGateway ?? new AzureCosmosDocumentGateway());
    }

    public static IProblemIdentityResolver CreateProblemIdentityResolver(
        RuntimeSettings settings, IReadOnlyDictionary<string, string?> env, ICosmosDocumentGateway? cosmosGateway = null)
    {
        var (database, container) = Location(settings, env);
        return new CosmosProblemIdentityResolver(settings.CosmosEndpoint.Trim(), database, container,
            settings.Product, cosmosGateway ?? new AzureCosmosDocumentGateway());
    }

    private static (string Database, string Container) Location(RuntimeSettings settings, IReadOnlyDictionary<string, string?> env)
    {
        var database = Read(settings, env, RuntimeIntegrationSettings.CosmosDatabase);
        var container = Read(settings, env, RuntimeIntegrationSettings.CosmosLearningContainer);
        return (
            database.Length > 0 ? database : RuntimeIntegrationSettings.DefaultCosmosDatabase,
            container.Length > 0 ? container : RuntimeIntegrationSettings.DefaultCosmosLearningContainer);
    }

    private static string Read(RuntimeSettings settings, IReadOnlyDictionary<string, string?> env, string name) =>
        env.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : settings.IntegrationSettings.GetValueOrDefault(name)?.Trim() ?? string.Empty;
}
