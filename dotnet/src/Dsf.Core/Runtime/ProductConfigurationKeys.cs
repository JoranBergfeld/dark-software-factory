using System.Text.Json;

namespace Dsf.Core.Runtime;

/// <summary>Canonical App Configuration key, label, and value conventions for product-scoped factory settings.</summary>
public static class ProductConfigurationKeys
{
    public static string GitHubAuthorityBinding(Guid operationId, string binding) =>
        $"dsf/factory/github-bindings/{operationId:D}/{binding}";
    public const string NoLabel = "\0";
    public const string AnyLabel = "*";

    public const string OwnerIndexGitHubRepository = "GITHUB_REPOSITORY";
    public const string OwnerIndexAppConfigEndpoint = "AZURE_APPCONFIG_ENDPOINT";
    public const string OwnerBootstrapStatusFilter = "dsf/owner/bootstrap/*";
    public const string OwnerOnboardingPrefix = "dsf/onboarding/";
    public const string OwnerOnboardingFilter = OwnerOnboardingPrefix + "*";
    public static string FoundationOperation(Guid operationId, int index) =>
        $"dsf/foundation/{operationId:D}/operations/{index:D4}";
    public const string FactoryManifest = "dsf/factory/manifest";
    public const string FactoryProfile = "dsf/factory/profile";
    public const string FactoryGitHubAuthority = "dsf/factory/github-authority";
    public const string FactoryTelemetry = "dsf/factory/telemetry";
    public const string FactoryModels = "dsf/factory/models";
    public const string FactorySource = "dsf/factory/source";
    public static string SourceOperation(Guid operationId, int index) => $"dsf/factory/source-operations/{operationId:D}/{index:D4}";
    public static string ModelRoute(Guid operationId, int index) => $"dsf/factory/model-routes/{operationId:D}/{index:D4}";
    public static string ModelBinding(Guid operationId, string name) => $"dsf/factory/model-bindings/{operationId:D}/{name}";
    public static string ModelGrant(Guid operationId, int index) => $"dsf/factory/model-grants/{operationId:D}/{index:D4}";
    public static string ModelOperation(Guid operationId, int index) => $"dsf/factory/model-operations/{operationId:D}/{index:D4}";
    public static string ModelProof(Guid operationId, Guid checkId) => $"dsf/factory/model-proofs/{operationId:D}/{checkId:D}";
    public static string TelemetryOperation(Guid operationId, int index) =>
        $"dsf/factory/telemetry-operations/{operationId:D}/{index:D4}";
    public static string TelemetryProof(Guid operationId, Guid checkId) =>
        $"dsf/factory/telemetry-proofs/{operationId:D}/{checkId:D}";
    public static string GitHubAuthorityOperation(Guid operationId, int index) =>
        $"dsf/factory/github-operations/{operationId:D}/{index:D4}";
    public static string FactoryPlanChunk(int index) => $"dsf/factory/reviewed-plan/{index:D4}";

    public static string OwnerProductClaim(string productKey) => OwnerOnboardingPrefix + "products/" + productKey;
    public static string OwnerRepositoryClaim(long repositoryId) => OwnerOnboardingPrefix + "repositories/" + repositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string OwnerResourceClaim(string canonicalResourceId) => OwnerOnboardingPrefix + "resources/"
        + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalResourceId)));
    public static string OwnerReservationPlanChunk(Guid factoryId, int index) =>
        OwnerOnboardingPrefix + "plans/" + factoryId.ToString("D") + "/" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    public static string OwnerBootstrapStatus(string appName) =>
        $"dsf/owner/bootstrap/{(appName ?? string.Empty).Trim().ToLowerInvariant()}/status";

    public const string GitHubRepository = "product.github_repo";
    public const string LabelTaxonomy = "product.label_taxonomy";
    public const string FoundryIqScope = "product.foundryiq_scope";
    public const string SentryProjects = "product.sentry_projects";
    public const string GrafanaDashboards = "product.grafana_dashboards";
    public const string AzureMonitorScope = "product.azure_monitor_scope";
    public const string SweepPaused = "sweep-paused";
    public const string SweepIntervalSeconds = "sweep-interval-seconds";

    public static string Threshold(string product) => $"threshold.{product}";

    public static string AgentEnabled(string kind) => $"agents.{NormalizeKind(kind)}.enabled";

    public static bool TryReadAgentKind(string key, out string kind)
    {
        const string prefix = "agents.";
        const string suffix = ".enabled";
        kind = string.Empty;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !key.EndsWith(suffix, StringComparison.Ordinal)
            || key.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }

        kind = NormalizeKind(key[prefix.Length..^suffix.Length]);
        return kind.Length > 0;
    }

    public static bool IsJsonTrue(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<bool?>(value) ?? false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeKind(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();
}
