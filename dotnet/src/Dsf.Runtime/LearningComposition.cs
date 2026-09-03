using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.Runtime.GitHubApp;

namespace Dsf.Runtime;

/// <summary>
/// The learning loop's collaborators for a product: where it polls human outcomes
/// from, and where it records what it found.
/// </summary>
public sealed record LearningServices(IOutcomeSource OutcomeSource, ILearningStore LearningStore);

/// <summary>
/// Builds the learning loop's collaborators for a product. Reuses the runtime's
/// existing GitHub App auth settings (the same ones <see cref="IConveyorComposer"/>
/// wires the issue filer through) and Cosmos endpoint (a separate container from
/// the run blackboard), so no new required setting exists beyond what filing
/// already needs.
/// </summary>
public interface ILearningComposer
{
    LearningServices ComposeFor(RuntimeSettings settings);
}

/// <summary>
/// The production learning composer: wires the real GitHub outcome poller and the
/// Cosmos-backed learning store. Anything unset raises
/// <see cref="RuntimeConfigurationException"/> naming every missing setting at
/// once, exactly like <see cref="EnvironmentConveyorComposer"/>.
/// </summary>
internal sealed class EnvironmentLearningComposer(
    IReadOnlyDictionary<string, string?> env,
    ICosmosDocumentGateway? cosmosGateway = null,
    IPrivateKeySecretReader? privateKeySecretReader = null) : ILearningComposer
{
    public LearningServices ComposeFor(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var missing = new List<string>();
        var outcomeSource = ComposeOutcomeSource(settings, missing);
        var learningStore = ComposeLearningStore(settings, missing);

        if (missing.Count > 0)
        {
            throw new RuntimeConfigurationException(
                $"the learning loop for product '{settings.Product}' cannot be composed: "
                + string.Join("; ", Explain(settings)),
                missing);
        }

        return new LearningServices(outcomeSource!, learningStore!);
    }

    private IOutcomeSource? ComposeOutcomeSource(RuntimeSettings settings, List<string> missing)
    {
        var repository = settings.GitHubRepository.Trim();
        var appSettings = new (string Value, string EnvVar)[]
        {
            (settings.GitHubAppId.Trim(), RuntimeSettingsComposer.GitHubAppId),
            (settings.GitHubInstallationId.Trim(), RuntimeSettingsComposer.GitHubInstallationId),
            (settings.GitHubAppPrivateKeySecret.Trim(), RuntimeSettingsComposer.GitHubAppPrivateKeySecret),
            (settings.KeyVaultUri.Trim(), RuntimeSettingsComposer.AzureKeyVaultUri),
        };

        var incompleteAppSettings = appSettings.Where(setting => setting.Value.Length == 0)
            .Select(setting => setting.EnvVar)
            .ToList();

        if (repository.Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.GitHubRepository);
        }

        if (incompleteAppSettings.Count > 0)
        {
            missing.AddRange(incompleteAppSettings);
        }

        if (repository.Length == 0 || incompleteAppSettings.Count > 0)
        {
            return null;
        }

        var apiUrl = Read(RuntimeIntegrationSettings.GitHubApiUrl);
        var authProvider = GitHubAppAuthProviderFactory.Build(
            settings.GitHubAppId.Trim(),
            settings.GitHubInstallationId.Trim(),
            settings.KeyVaultUri.Trim(),
            settings.GitHubAppPrivateKeySecret.Trim(),
            apiUrl,
            privateKeySecretReader);

        return GitHubOutcomePoller.Create(apiUrl, authProvider, repository);
    }

    private ILearningStore? ComposeLearningStore(RuntimeSettings settings, List<string> missing)
    {
        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            missing.Add(RuntimeSettingsComposer.AzureCosmosEndpoint);
            return null;
        }

        var database = Read(RuntimeIntegrationSettings.CosmosDatabase);
        var container = Read(RuntimeIntegrationSettings.CosmosLearningContainer);
        return new CosmosLearningStore(
            settings.CosmosEndpoint.Trim(),
            database.Length > 0 ? database : RuntimeIntegrationSettings.DefaultCosmosDatabase,
            container.Length > 0 ? container : RuntimeIntegrationSettings.DefaultCosmosLearningContainer,
            settings.Product,
            cosmosGateway ?? new AzureCosmosDocumentGateway());
    }

    private IEnumerable<string> Explain(RuntimeSettings settings)
    {
        var missingAppSettings = new (string Value, string EnvVar)[]
        {
            (settings.GitHubAppId, RuntimeSettingsComposer.GitHubAppId),
            (settings.GitHubInstallationId, RuntimeSettingsComposer.GitHubInstallationId),
            (settings.GitHubAppPrivateKeySecret, RuntimeSettingsComposer.GitHubAppPrivateKeySecret),
            (settings.KeyVaultUri, RuntimeSettingsComposer.AzureKeyVaultUri),
        }
            .Where(setting => setting.Value.Trim().Length == 0)
            .Select(setting => setting.EnvVar)
            .ToList();

        if (missingAppSettings.Count > 0)
        {
            yield return "no GitHub App auth is configured (set " + string.Join(", ", missingAppSettings) + ")";
        }

        if (settings.GitHubRepository.Trim().Length == 0)
        {
            yield return "no repository is configured to poll outcomes from (set "
                + $"{RuntimeSettingsComposer.GitHubRepository})";
        }

        if (settings.CosmosEndpoint.Trim().Length == 0)
        {
            yield return "no learning persistence is configured (set "
                + $"{RuntimeSettingsComposer.AzureCosmosEndpoint})";
        }
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
