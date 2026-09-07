using System.Text.Json;

namespace Dsf.Core.Runtime;

/// <summary>Canonical App Configuration key, label, and value conventions for product-scoped factory settings.</summary>
public static class ProductConfigurationKeys
{
    public const string NoLabel = "\0";
    public const string AnyLabel = "*";

    public const string OwnerIndexGitHubRepository = "GITHUB_REPOSITORY";
    public const string OwnerIndexAppConfigEndpoint = "AZURE_APPCONFIG_ENDPOINT";

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
