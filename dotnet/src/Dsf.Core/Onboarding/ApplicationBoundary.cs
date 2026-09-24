namespace Dsf.Core.Onboarding;

/// <summary>
/// One Azure resource an operator may explicitly select as part of the application
/// boundary. Discovery reports what Azure says; it never decides membership.
/// </summary>
public sealed record ApplicationResourceCandidate
{
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string ResourceGroup { get; init; }
    public required string SubscriptionId { get; init; }
    public required string TenantId { get; init; }

    /// <summary>Environment moniker observed on the resource, or empty when unknown.</summary>
    public string Environment { get; init; } = "";

    /// <summary>True when the resource is declared shared between applications.</summary>
    public bool Shared { get; init; }

    /// <summary>
    /// Existing DSF observation signal read from tags. A conflict signal only: matching
    /// values never prove ownership and never authorize adoption.
    /// </summary>
    public string ExistingClaimSignal { get; init; } = "";

    /// <summary>True when the resource's tags/metadata could not be read at all.</summary>
    public bool MetadataUnreadable { get; init; }
}

/// <summary>An evidence backend candidate, selected separately from application membership.</summary>
public sealed record EvidenceBackendCandidate
{
    public required string ResourceId { get; init; }
    public required string Name { get; init; }

    /// <summary>Evidence route kind, e.g. <c>loganalytics</c>.</summary>
    public required string Kind { get; init; }

    public required string SubscriptionId { get; init; }
    public required string TenantId { get; init; }
    public string Environment { get; init; } = "";
    public bool Shared { get; init; }
    public string ExistingClaimSignal { get; init; } = "";
    public bool MetadataUnreadable { get; init; }
}

/// <summary>Everything discovery could read inside the selected tenant/subscription.</summary>
public sealed record AzureApplicationInventory
{
    public required string TenantId { get; init; }
    public required string SubscriptionId { get; init; }
    public IReadOnlyList<ApplicationResourceCandidate> Resources { get; init; } = [];
    public IReadOnlyList<EvidenceBackendCandidate> EvidenceBackends { get; init; } = [];
}

/// <summary>
/// The exact selection an operator asks the factory to preview. Every member is
/// explicit: nothing is inferred from groups, parentage, dependencies, or tags.
/// </summary>
public sealed record ApplicationBoundarySelection
{
    public required string Product { get; init; }
    public required string TenantId { get; init; }
    public required string SubscriptionId { get; init; }

    /// <summary>The application's named environment, distinct from the factory environment.</summary>
    public required string ApplicationEnvironment { get; init; }

    public IReadOnlyList<string> ApplicationResourceIds { get; init; } = [];
    public IReadOnlyList<string> EvidenceBackendIds { get; init; } = [];

    /// <summary>The operator explicitly declared these resources dedicated to this application.</summary>
    public bool DedicationDeclared { get; init; }

    /// <summary>The operator explicitly reviewed cross-owner exclusivity for this exact selection.</summary>
    public bool CrossOwnerExclusivityReviewed { get; init; }

    /// <summary>Owner App Configuration endpoint holding the reservation/claim registry.</summary>
    public string OwnerAppConfigEndpoint { get; init; } = "";

    /// <summary>Owner Key Vault URI holding the DSF App private key.</summary>
    public string OwnerKeyVaultUri { get; init; } = "";

    /// <summary>The factory's own environment moniker, distinct from the application environment.</summary>
    public string FactoryEnvironment { get; init; } = "dev";
}

/// <summary>Why the previewed selection cannot proceed. Preview never repairs anything.</summary>
public sealed record BoundaryRejection(string Subject, string Reason);

/// <summary>One categorized line of the preview.</summary>
public sealed record BoundaryPlanEntry(string Subject, string Detail);

/// <summary>Outcome of previewing a boundary. Never partially true.</summary>
public enum BoundaryPlanOutcome
{
    /// <summary>The exact selection is reviewable and no blocker remains.</summary>
    Reviewable,

    /// <summary>At least one rejection or missing prerequisite blocks the selection.</summary>
    Blocked,
}

/// <summary>
/// A read-only, categorized preview of attaching a proposal-only council to one
/// explicitly selected application boundary. Producing it performs no side effect.
/// </summary>
public sealed record ApplicationBoundaryPlan
{
    public required BoundaryPlanOutcome Outcome { get; init; }
    public required ApplicationBoundarySelection Selection { get; init; }

    /// <summary>Stable fingerprint over the decision-relevant inputs of this plan.</summary>
    public required string Fingerprint { get; init; }

    public IReadOnlyList<BoundaryPlanEntry> PreservedAssets { get; init; } = [];
    public IReadOnlyList<BoundaryPlanEntry> NewFactoryResources { get; init; } = [];
    public IReadOnlyList<BoundaryPlanEntry> ReusedPrerequisites { get; init; } = [];
    public IReadOnlyList<BoundaryPlanEntry> ProposedGrants { get; init; } = [];
    public IReadOnlyList<BoundaryPlanEntry> RegistryClaims { get; init; } = [];
    public IReadOnlyList<BoundaryPlanEntry> RepositoryAdditions { get; init; } = [];
    public IReadOnlyList<BoundaryPlanEntry> CostsAndOmissions { get; init; } = [];
    public IReadOnlyList<BoundaryPlanEntry> AdministratorPrerequisites { get; init; } = [];
    public IReadOnlyList<BoundaryRejection> Rejections { get; init; } = [];
}
