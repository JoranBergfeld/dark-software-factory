namespace Dsf.Core.Onboarding;

public enum FactoryFoundationState { Inactive, Partial, Blocked, Failed, Canceled, Unknown }
public enum FoundationOperationState { Intent, InFlight, Completed, Failed, Unknown }
public enum FoundationResourceDisposition { CreateRequested, Created, Reused, Unproven }

public sealed record FactoryFoundationPointer(
    Guid OperationId, FactoryFoundationState State, int OperationCount, string Stage,
    string? ProductEndpoint = null, string? ProfilePin = null);

public sealed record FoundationOperation(
    int SchemaVersion, string Kind, Guid FactoryId, Guid OperationId, int Index,
    string Target, string ApiVersion, string RequestSha256, Guid ClientRequestId, DateTimeOffset StartedAt,
    FoundationOperationState State, FoundationResourceDisposition Disposition)
{
    public int? HttpStatus { get; init; }
    public string? ProviderRequestId { get; init; }
    public string? ProviderCorrelationId { get; init; }
    public DateTimeOffset? ResponseDate { get; init; }
    public string? ResourceId { get; init; }
    public string? PrincipalId { get; init; }
    public string? ClientId { get; init; }
    public string? Incarnation { get; init; }
    public string? Endpoint { get; init; }
    public string? DeploymentTarget { get; init; }
    public string? ProviderOperationId { get; init; }
    public string? LogicalIdentity { get; init; }
    public string? ManagedBy { get; init; }
}

public sealed record FactoryOnboardingProfile(
    int SchemaVersion, string Profile, Guid FactoryId, string PlanFingerprint,
    IReadOnlyList<string> AllowedRepositoryMutations);

public sealed record FactoryManifest(
    int SchemaVersion, string Kind, string Authority, string Publication,
    FactoryReservationIdentity Identity, string ReviewedPlanSha256, int PlanBytes, int PlanChunks,
    string ProfilePin);

public sealed record FactoryFoundationResult(
    FactoryFoundationState State, string Stage, string Detail, Guid OperationId,
    IReadOnlyList<FoundationOperation> Operations)
{
    public bool IsBaseProvisioned => State == FactoryFoundationState.Inactive;
    public bool IsActivated => false;
    public string? ProductEndpoint { get; init; }
    public string? ProfilePin { get; init; }
    public IReadOnlyList<string> PendingStages { get; init; } =
        ["workload grants", "GitHub authentication", "evidence access and connectivity",
            "factory telemetry connectivity", "models", "code inventory and analysis",
            "charter", "council readiness", "activation"];
}
