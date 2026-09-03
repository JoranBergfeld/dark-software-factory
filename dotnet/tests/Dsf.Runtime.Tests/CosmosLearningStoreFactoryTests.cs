using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class CosmosLearningStoreFactoryTests
{
    private sealed class RecordingCosmosGateway : ICosmosDocumentGateway
    {
        public List<(string Endpoint, string Database, string Container, string PartitionKey)> Creates { get; } = [];

        public Task UpsertAsync(
            string endpoint,
            string database,
            string container,
            string partitionKey,
            string id,
            string json,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("learning store records must create-if-absent");

        public Task<string?> ReadAsync(
            string endpoint,
            string database,
            string container,
            string partitionKey,
            string id,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<bool> CreateIfAbsentAsync(
            string endpoint,
            string database,
            string container,
            string partitionKey,
            string id,
            string json,
            CancellationToken cancellationToken)
        {
            Creates.Add((endpoint, database, container, partitionKey));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Factory_uses_runtime_cosmos_learning_overrides()
    {
        var env = new Dictionary<string, string?>
        {
            [RuntimeIntegrationSettings.CosmosDatabase] = " dsf-prod ",
            [RuntimeIntegrationSettings.CosmosLearningContainer] = " outcomes-prod ",
        };
        var gateway = new RecordingCosmosGateway();

        var store = CosmosLearningStoreFactory.Create(SettingsWith(cosmosEndpoint: " https://cosmos.example "), env, gateway);
        await store.RecordAsync(Record(), CancellationToken.None);

        var create = Assert.Single(gateway.Creates);
        Assert.Equal(("https://cosmos.example", "dsf-prod", "outcomes-prod", "acme"), create);
    }

    [Fact]
    public async Task Factory_uses_runtime_cosmos_learning_defaults()
    {
        var gateway = new RecordingCosmosGateway();

        var store = CosmosLearningStoreFactory.Create(SettingsWith(), new Dictionary<string, string?>(), gateway);
        await store.RecordAsync(Record(), CancellationToken.None);

        var create = Assert.Single(gateway.Creates);
        Assert.Equal(
            (
                "https://cosmos.example",
                RuntimeIntegrationSettings.DefaultCosmosDatabase,
                RuntimeIntegrationSettings.DefaultCosmosLearningContainer,
                "acme"
            ),
            create);
    }

    private static RuntimeSettings SettingsWith(string cosmosEndpoint = "https://cosmos.example") => new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: "https://acme-kv.vault.azure.net/",
        AppInsightsConnectionString: "InstrumentationKey=abc123",
        CosmosEndpoint: cosmosEndpoint,
        OpenAiEndpoint: "https://openai.example",
        OpenAiDeployment: "gpt-deploy",
        OpenAiEmbeddingDeployment: "embed-deploy",
        GitHubAppId: "12345",
        GitHubInstallationId: "67890",
        GitHubAppPrivateKeySecret: "gh-app-private-key",
        GitHubRepository: "acme/acme");

    private static LearningRecord Record() =>
        new(
            "fingerprint-1:sentry",
            "dsf-outcome:approved",
            "https://github.com/acme/acme/issues/9",
            "[sentry] checkout 500s spiked",
            DateTimeOffset.UtcNow);
}
