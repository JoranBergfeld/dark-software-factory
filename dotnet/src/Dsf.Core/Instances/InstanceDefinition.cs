using System.Text.Json.Serialization;

namespace Dsf.Core.Instances;

/// <summary>
/// Versioned, on-disk definition of one product factory instance.
/// This is configuration only: desired product/runtime/governance settings,
/// GitHub and Azure identifiers, discovered outputs, and minimal status
/// metadata. It deliberately carries no execution plan, command history, or
/// secret values — only the names of secrets held elsewhere.
/// </summary>
public sealed record InstanceDefinition
{
    /// <summary>Root schema version; bumped whenever the shape changes incompatibly.</summary>
    public int SchemaVersion { get; init; } = InstanceDefinitions.CurrentSchemaVersion;

    public required ProductSettings Product { get; init; }

    public required RuntimeSettings Runtime { get; init; }

    public required GovernanceSettings Governance { get; init; }

    [JsonPropertyName("github")]
    public required GitHubSettings GitHub { get; init; }

    public required AzureSettings Azure { get; init; }

    public required InstanceStatus Status { get; init; }
}

/// <summary>Identity and lifecycle settings for the product this instance serves.</summary>
public sealed record ProductSettings
{
    public required string Key { get; init; }

    public string Environment { get; init; } = "dev";

    /// <summary>Creation-phase autonomy dial: <c>low</c>, <c>medium</c>, or <c>high</c>.</summary>
    public string CreationMaturity { get; init; } = "low";

    /// <summary>Operation-phase autonomy dial: <c>low</c>, <c>medium</c>, or <c>high</c>.</summary>
    public string OperationMaturity { get; init; } = "low";
}

/// <summary>Where and how the factory runtime is hosted.</summary>
public sealed record RuntimeSettings
{
    public string Target { get; init; } = "aca";

    public string Image { get; init; } = "ghcr.io/joranbergfeld/dsf-runtime:latest";
}

/// <summary>Runtime-governable council settings captured at provision time.</summary>
public sealed record GovernanceSettings
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultLabelTaxonomy =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["type"] = ["feature", "bug", "chore"],
            ["area"] = ["api", "ui", "infra"],
            ["severity"] = ["sev-low", "sev-medium", "sev-high", "sev-critical"],
        };

    public double ConfidenceThreshold { get; init; } = 0.6;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> LabelTaxonomy { get; init; } = DefaultLabelTaxonomy;

    /// <summary>Object id of the human owner who governs this instance from outside the loop.</summary>
    public string? AdminPrincipalId { get; init; }

    public bool Equals(GovernanceSettings? other) =>
        other is not null
        && ConfidenceThreshold.Equals(other.ConfidenceThreshold)
        && AdminPrincipalId == other.AdminPrincipalId
        && TaxonomyEquals(LabelTaxonomy, other.LabelTaxonomy);

    public override int GetHashCode() =>
        HashCode.Combine(ConfidenceThreshold, AdminPrincipalId, LabelTaxonomy.Count);

    private static bool TaxonomyEquals(
        IReadOnlyDictionary<string, IReadOnlyList<string>> left,
        IReadOnlyDictionary<string, IReadOnlyList<string>> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var values) && entry.Value.SequenceEqual(values));
}

/// <summary>GitHub identifiers for the product repo and the DSF App binding (names only, never keys).</summary>
public sealed record GitHubSettings
{
    public required string Owner { get; init; }

    public required string Repository { get; init; }

    public string Visibility { get; init; } = "private";

    public string? AppId { get; init; }

    public string? InstallationId { get; init; }

    public long? RepositoryId { get; init; }

    public string DefaultBranch { get; init; } = "main";

    public long? BranchProtectionRulesetId { get; init; }

    /// <summary>Key Vault secret <em>name</em> the runtime reads the App private key from.</summary>
    public string PrivateKeySecretName { get; init; } = "github-app-private-key";

    /// <summary>
    /// GitHub Actions repository secret <em>name</em> (not the value) that carries the DSF
    /// user-to-server GitHub credential (PAT/OAuth/App user token) — the only kind of token
    /// GitHub accepts for re-invoking the Cloud Agent (a server-to-server installation token
    /// cannot). Consumed by the Creation-phase retry workflow (high <see
    /// cref="ProductSettings.CreationMaturity"/>) and, where wired, by SRE-Agent-to-Cloud-Agent
    /// auto-assignment. Provisioning this ticket only makes the name referenceable; seeding the
    /// secret's actual value is a separate, later concern.
    /// </summary>
    public string CloudAgentCredentialSecretName { get; init; } = "DSF_CLOUD_AGENT_TOKEN";
}

/// <summary>Azure resource identifiers plus any outputs discovered after deployment.</summary>
public sealed record AzureSettings
{
    public string Location { get; init; } = "swedencentral";

    public required string NamePrefix { get; init; }

    public required string ResourceGroup { get; init; }

    public required string DeploymentName { get; init; }

    public required SreAgentSettings SreAgent { get; init; }

    /// <summary>Endpoints of the owner-level authority this instance is seeded from (endpoints only, never secret values).</summary>
    public OwnerAuthoritySettings OwnerAuthority { get; init; } = new();

    /// <summary>Deployment outputs (endpoints, resource names) — never secret values.</summary>
    public IReadOnlyDictionary<string, string> Outputs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Equals(AzureSettings? other) =>
        other is not null
        && Location == other.Location
        && NamePrefix == other.NamePrefix
        && ResourceGroup == other.ResourceGroup
        && DeploymentName == other.DeploymentName
        && SreAgent == other.SreAgent
        && OwnerAuthority == other.OwnerAuthority
        && Outputs.Count == other.Outputs.Count
        && Outputs.All(entry => other.Outputs.TryGetValue(entry.Key, out var value) && value == entry.Value);

    public override int GetHashCode() =>
        HashCode.Combine(Location, NamePrefix, ResourceGroup, DeploymentName, SreAgent, OwnerAuthority, Outputs.Count);
}

/// <summary>
/// Owner-authority endpoints the factory reads shared configuration and secrets from.
/// URIs only: the secret values themselves stay in Key Vault.
/// </summary>
public sealed record OwnerAuthoritySettings
{
    /// <summary>URI of the owner Key Vault holding shared secrets (e.g. the DSF App private key).</summary>
    public string? KeyVaultUri { get; init; }

    /// <summary>Endpoint of the owner App Configuration store holding the runtime index.</summary>
    public string? AppConfigEndpoint { get; init; }
}

/// <summary>Azure SRE Agent placement and monitoring scope.</summary>
public sealed record SreAgentSettings
{
    public required string Name { get; init; }

    public required string ResourceGroup { get; init; }

    public string Location { get; init; } = "swedencentral";

    public IReadOnlyList<string> MonitoredResourceGroups { get; init; } = [];

    public bool Equals(SreAgentSettings? other) =>
        other is not null
        && Name == other.Name
        && ResourceGroup == other.ResourceGroup
        && Location == other.Location
        && MonitoredResourceGroups.SequenceEqual(other.MonitoredResourceGroups);

    public override int GetHashCode() =>
        HashCode.Combine(Name, ResourceGroup, Location, MonitoredResourceGroups.Count);
}

/// <summary>Minimal lifecycle metadata: planned vs executed, and when it was generated.</summary>
public sealed record InstanceStatus
{
    public InstanceState State { get; init; } = InstanceState.Planned;

    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>Whether the definition describes a planned instance or an executed provision.</summary>
public enum InstanceState
{
    /// <summary>Written by a dry run: nothing was provisioned.</summary>
    Planned,

    /// <summary>Written after provisioning actually ran.</summary>
    Executed,
}
