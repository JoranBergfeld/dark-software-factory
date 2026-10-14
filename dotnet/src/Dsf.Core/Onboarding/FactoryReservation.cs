namespace Dsf.Core.Onboarding;

public enum FactoryReservationState { Reserved, Partial, Blocked, Failed, Canceled, Unknown }

public sealed record FactoryReservationIdentity(
    Guid FactoryId, Guid OperationId, string OwnerEndpoint, string ProductKey, long RepositoryId,
    string TenantId, string SubscriptionId, string ApplicationEnvironment,
    string SelectionFingerprint, string PlanFingerprint,
    string AzureActorId, long GitHubActorId, DateTimeOffset CreatedAt);

public sealed record ReservationWriteIntent(int Step, string Key, string Label, string ValueSha256);

/// <summary>The product claim is also the first durable admission and staged recovery anchor.</summary>
public sealed record FactoryReservationAnchor(
    int SchemaVersion, string Kind, FactoryReservationIdentity Identity,
    string PlanSha256, int PlanBytes, int PlanChunks, int ClaimCount)
{
    public int CompletedSteps { get; init; }
    public ReservationWriteIntent? Intent { get; init; }
    public FactoryReservationState State { get; init; } = FactoryReservationState.Partial;
    public FactoryFoundationPointer? Foundation { get; init; }
    public FactoryOffboardingPointer? Offboarding { get; init; }
    public FactoryPurgePointer? Purge { get; init; }
}

public sealed record FactoryReservationPlanChunk(
    int SchemaVersion, string Kind, FactoryReservationIdentity Identity, int Index, string Data);

public sealed record FactoryReservationClaim(
    int SchemaVersion, string Kind, FactoryReservationIdentity Identity, ProposedObservationClaim Claim);

public sealed record FactoryReservationResult(
    FactoryReservationState State, string Detail, FactoryReservationIdentity? Identity = null)
{
    public Guid? FactoryId => Identity?.FactoryId;
    public Guid? OperationId => Identity?.OperationId;
    public string? ProductKey => Identity?.ProductKey;
    public long? RepositoryId => Identity?.RepositoryId;
    public bool IsProvisioned => false;
    public bool IsActivated => false;
    public bool HasRegistryEffects { get; init; }
    public string? Revision { get; init; }
    public int ConfirmedClaims { get; init; }
    public int ConfirmedPlanChunks { get; init; }
    public ReservationWriteIntent? PendingIntent { get; init; }
    public ApplicationBoundaryPlan? Plan { get; init; }
    public ApplicationBoundaryPlan? ReconfiguredPlan { get; init; }
    public FactoryFoundationResult? Foundation { get; init; }
    public FactoryGitHubAuthority? GitHubAuthority { get; init; }
    public FactoryTelemetry? Telemetry { get; init; }
    public FactoryModels? Models { get; init; }
    public FactorySource? Source { get; init; }
    public FactorySupplemental? Supplemental { get; init; }
    public IReadOnlyList<FoundationOperation> GitHubOperations { get; init; } = [];
    public string NextAction => Foundation is not null
        ? "Use resume with the same trusted owner locator to reconcile receipts and continue only verified safe work. "
            + "Unknown effects and unsupported incarnations require administrator handoff; no force, new identity or rollback. Foundation history is not current readiness or activation."
        : "Inspect trusted owner status; resume only this pre-provisioning operation. "
        + "Do not release claims, replace identity or infer consent, workload access, readiness or activation.";
}
