using System.Globalization;
using System.Text.Json.Serialization;
using Dsf.Core.Runtime;

namespace Dsf.Core.Instances;

/// <summary>Nonsecret configuration for incrementally enabling served source agents.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DecideDeploymentSettings
{
    public IReadOnlyList<string> EnabledSourceAgentKinds { get; init; } = [];
    public string AzureMonitorWorkspaceId { get; init; } = "";
    public string AzureMonitorQuery { get; init; } = "";
    public string FoundryIqSearchEndpoint { get; init; } = "";
    public string FoundryIqKnowledgeBase { get; init; } = "";
    public string FoundryIqQuery { get; init; } = "";
    public string WebIqQuery { get; init; } = "";
    public IReadOnlyList<JurorModelSettings> JuryModels { get; init; } = [];
    public int JuryTimeoutSeconds { get; init; } = 120;
    public int DeliberationRounds { get; init; } = 2;
    public IReadOnlyList<DeliberationLensSettings> Lenses { get; init; } = [];

    public bool Equals(DecideDeploymentSettings? other) =>
        other is not null
        && EnabledSourceAgentKinds.SequenceEqual(other.EnabledSourceAgentKinds, StringComparer.Ordinal)
        && AzureMonitorWorkspaceId == other.AzureMonitorWorkspaceId
        && AzureMonitorQuery == other.AzureMonitorQuery
        && FoundryIqSearchEndpoint == other.FoundryIqSearchEndpoint
        && FoundryIqKnowledgeBase == other.FoundryIqKnowledgeBase
        && FoundryIqQuery == other.FoundryIqQuery
        && WebIqQuery == other.WebIqQuery
        && JuryModels.SequenceEqual(other.JuryModels)
        && JuryTimeoutSeconds == other.JuryTimeoutSeconds
        && DeliberationRounds == other.DeliberationRounds
        && Lenses.SequenceEqual(other.Lenses);

    public override int GetHashCode() =>
        HashCode.Combine(EnabledSourceAgentKinds.Count, AzureMonitorWorkspaceId, AzureMonitorQuery,
            FoundryIqSearchEndpoint, FoundryIqKnowledgeBase, FoundryIqQuery, WebIqQuery,
            HashCode.Combine(JuryModels.Count, JuryTimeoutSeconds, DeliberationRounds, Lenses.Count));

    public void Validate()
    {
        if (EnabledSourceAgentKinds is null)
        {
            throw new InstanceDefinitionException("decide.enabledSourceAgentKinds must be an array.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in EnabledSourceAgentKinds)
        {
            if (!SourceAgentKinds.Known.Contains(kind, StringComparer.Ordinal) || !seen.Add(kind))
            {
                throw new InstanceDefinitionException(
                    $"decide.enabledSourceAgentKinds contains an unknown or duplicate kind '{kind}'.");
            }

            switch (kind)
            {
                case "azuremonitor":
                    Require(AzureMonitorWorkspaceId, "azureMonitorWorkspaceId");
                    Require(AzureMonitorQuery, "azureMonitorQuery");
                    break;
                case "foundryiq":
                    Require(FoundryIqSearchEndpoint, "foundryIqSearchEndpoint");
                    if (!Uri.TryCreate(FoundryIqSearchEndpoint, UriKind.Absolute, out var searchEndpoint)
                        || searchEndpoint.Scheme != Uri.UriSchemeHttps || searchEndpoint.AbsolutePath != "/"
                        || searchEndpoint.UserInfo.Length > 0 || searchEndpoint.Query.Length > 0
                        || searchEndpoint.Fragment.Length > 0)
                    {
                        throw new InstanceDefinitionException(
                            "decide.foundryIqSearchEndpoint must be an HTTPS Search service root, without credentials or a project path.");
                    }
                    Require(FoundryIqKnowledgeBase, "foundryIqKnowledgeBase");
                    Require(FoundryIqQuery, "foundryIqQuery");
                    break;
                case "webiq":
                    Require(WebIqQuery, "webIqQuery");
                    break;
            }
        }

        if (JuryModels is null || Lenses is null)
        {
            throw new InstanceDefinitionException("decide.juryModels and decide.lenses must be arrays.");
        }

        try
        {
            if (EnabledSourceAgentKinds.Count > 0 || JuryModels.Count > 0)
            {
                JurySettings.Create(JuryModels, JuryTimeoutSeconds);
            }
            else if (JuryTimeoutSeconds is < 1 or > 600)
            {
                throw new InstanceDefinitionException("decide.juryTimeoutSeconds must be between 1 and 600.");
            }
            DeliberationSettings.Create(DeliberationRounds, Lenses);
        }
        catch (RuntimeConfigurationException exception)
        {
            throw new InstanceDefinitionException(exception.Message, exception);
        }
    }

    public IReadOnlyDictionary<string, string> JudgmentEnvironment()
    {
        Validate();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DeliberationSettings.RoundsKey] = DeliberationRounds.ToString(CultureInfo.InvariantCulture),
            [JurySettings.TimeoutSeconds] = JuryTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };
        if (JuryModels.Count > 0)
        {
            values[JurySettings.ModelsKey] = JurySettings.Create(JuryModels, JuryTimeoutSeconds).SerializeModels();
        }
        foreach (var lens in DeliberationSettings.Create(DeliberationRounds, Lenses).Lenses)
        {
            values[DeliberationSettings.Key(lens.Name, "ENABLED")] = lens.Enabled ? "true" : "false";
            values[DeliberationSettings.Key(lens.Name, "WEIGHT")] = lens.Weight.ToString(CultureInfo.InvariantCulture);
        }
        return values;
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InstanceDefinitionException($"decide.{name} is required for its enabled source agent.");
        }
    }
}
