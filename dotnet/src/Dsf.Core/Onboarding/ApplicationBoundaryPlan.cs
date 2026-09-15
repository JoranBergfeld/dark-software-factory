namespace Dsf.Core.Onboarding;

public enum BoundaryPlanState { Reviewed, Partial, Blocked, Failed, Canceled, Unknown }

public sealed record EvidenceBackendSelection
{
    public required string ResourceId { get; init; }
    public required IReadOnlyList<string> Tables { get; init; }
}

public sealed record BoundaryDedicationReview
{
    public required string SelectionFingerprint { get; init; }
    public required string AccountableOwner { get; init; }
    public required bool Dedicated { get; init; }
    public required bool CrossOwnerExclusive { get; init; }
}

public sealed record ApplicationBoundarySelection
{
    public required int SchemaVersion { get; init; }
    public required string Repository { get; init; }
    public required string OwnerAppConfigEndpoint { get; init; }
    public required string OwnerKeyVaultUri { get; init; }
    public required string ProductKey { get; init; }
    public required string TenantId { get; init; }
    public required string SubscriptionId { get; init; }
    public required string ApplicationEnvironment { get; init; }
    public required IReadOnlyList<string> ApplicationResourceIds { get; init; }
    public required IReadOnlyList<EvidenceBackendSelection> EvidenceBackends { get; init; }
    public required string FactoryResourceGroup { get; init; }
    public required string FactoryLocation { get; init; }
    public required IReadOnlyList<string> ModelResourceIds { get; init; }
    public required string AutomationOwner { get; init; }
    public required IReadOnlyDictionary<RepositoryMutationClass, RepositoryAutomationDeclaration> AutomationDeclarations { get; init; }
    public required BoundaryDedicationReview? BoundaryReview { get; init; }
}

public sealed record BoundaryPlanFinding(
    string Check, BoundaryPlanState State, string Detail, string NextActor, string NextAction);

public sealed record AzureSubscriptionChoice(string TenantId, string SubscriptionId, string Name);

public sealed record BoundaryResource(
    string Id, string Type, string Location,
    IReadOnlyDictionary<string, string> Signals,
    string? WorkspaceId,
    string? BackingWorkspaceResourceId,
    string? SharedHostResourceId)
{
    public IReadOnlyDictionary<string, string> NetworkConfiguration { get; init; } = new Dictionary<string, string>();
}

public sealed record AzurePermissionObservation(
    string Scope, IReadOnlyList<string> Actions, IReadOnlyList<string> NotActions,
    IReadOnlyList<string>? DataActions, IReadOnlyList<string>? NotDataActions);

public sealed record AzureBoundaryDiscovery(
    string TenantId, string SubscriptionId, string? OperatorPrincipalId,
    IReadOnlyList<BoundaryResource> Resources,
    IReadOnlyList<AzurePermissionObservation> Permissions,
    IReadOnlyList<BoundaryPlanFinding> Findings)
{
    public IReadOnlyList<BoundaryResource> InspectedTopology { get; init; } = [];
    public IReadOnlyList<EvidenceTableObservation> EvidenceTables { get; init; } = [];
}

public sealed record EvidenceTableObservation(string BackendResourceId, string Name, string Plan);

public sealed record ProposedObservationClaim(string Kind, string Target, string Key, string Label);
public sealed record OwnerAdmissionObservation(string Key, string? Label, string ETag, string ValueFingerprint)
{
    public ReviewedGitHubRepository? CurrentRepository { get; init; }
}
public sealed record OwnerAdmissionSnapshot(
    string OwnerEndpoint, IReadOnlyList<OwnerAdmissionObservation> Observations,
    IReadOnlyList<ProposedObservationClaim> ProposedClaims, IReadOnlyList<BoundaryPlanFinding> Findings);

public sealed record PlannedFactoryResource(string Symbol, string ResourceType, string ParentScope, string Purpose);
public sealed record PlannedAccessGrant(
    string Identity, string? PrincipalId, string Scope, string Role, string RoleDefinitionId,
    string? Condition, bool RequiresBindingRecheck);
public sealed record RepositoryAdditionPlan(
    RepositoryMutationClass Mutation, RepositoryReviewState State, string Description);
public sealed record BoundaryPlanCategories(
    IReadOnlyList<string> PreservedAssets,
    IReadOnlyList<PlannedFactoryResource> NewFactoryResources,
    IReadOnlyList<string> ReusedPrerequisites,
    IReadOnlyList<PlannedAccessGrant> ProposedGrants,
    IReadOnlyList<ProposedObservationClaim> RegistryClaims,
    IReadOnlyList<RepositoryAdditionPlan> AllowedRepositoryAdditions,
    IReadOnlyList<string> CostsAndOmissions,
    IReadOnlyList<BoundaryPlanFinding> BlockedAdministratorPrerequisites);

public sealed record ApplicationBoundaryPlan(
    int SchemaVersion,
    DateTimeOffset ObservedAt,
    ApplicationBoundarySelection Selection,
    RepositoryAttachmentReview RepositoryReview,
    IReadOnlyList<BoundaryPlanFinding> Findings)
{
    public AzureBoundaryDiscovery? Azure { get; init; }
    public OwnerAdmissionSnapshot? OwnerAdmission { get; init; }
    public BoundaryPlanCategories? Categories { get; init; }
    public string SelectionFingerprint { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public Guid? FactoryId => null;
    public bool HasRemoteEffects => false;
    public BoundaryPlanState State =>
        Findings.Any(f => f.State == BoundaryPlanState.Blocked) ? BoundaryPlanState.Blocked
        : Findings.Any(f => f.State == BoundaryPlanState.Failed) ? BoundaryPlanState.Failed
        : Findings.Any(f => f.State == BoundaryPlanState.Unknown) ? BoundaryPlanState.Unknown
        : Findings.Any(f => f.State == BoundaryPlanState.Partial) ? BoundaryPlanState.Partial
        : BoundaryPlanState.Reviewed;
}
