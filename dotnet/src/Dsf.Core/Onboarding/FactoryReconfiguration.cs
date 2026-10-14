using System.Text.Json;

namespace Dsf.Core.Onboarding;

public enum FactoryReconfigurationState { Inspected, Reviewed, Partial, Blocked, Failed, Canceled, Unknown }
public enum FactoryConfigurationChangeKind { Addition, Removal, Replacement, Metadata }

public sealed record ReconfigurationTelemetrySelection(
    string RuntimeImage, string AudienceClientId, int FreshnessSeconds, string QueryRevision,
    IReadOnlyDictionary<string, string> Queries, IReadOnlyDictionary<string, string> StaleQueries);

public sealed record FactoryReconfigurationInputs(
    int SchemaVersion, ApplicationBoundarySelection Boundary, ReconfigurationTelemetrySelection Telemetry,
    SourceSelection Source, ModelTopologySelection Models, SupplementalSelection? Supplemental,
    CouncilJudgmentConfiguration Judgment);

public sealed record FactoryConfigurationChange(
    string Category, string Target, FactoryConfigurationChangeKind Kind, JsonElement? Before, JsonElement? After);

public sealed record FactoryReconfigurationRevision(string Store, string Key, string ETag, string Sha256);
public sealed record FactoryKeyCustody(
    string SourceVaultId, string SourceSecretVersion, string DestinationVaultId, string? DestinationSecretUri,
    string AuthPrincipalId, Guid AuthorityOperationId);

public sealed record FactoryReconfigurationReview(FactoryReconfigurationState State, string Detail)
{
    public Guid? FactoryId { get; init; }
    public long? RepositoryId { get; init; }
    public string? CurrentRevision { get; init; }
    public string? Fingerprint { get; init; }
    public string? SelectionFingerprint { get; init; }
    public FactoryReconfigurationInputs? CurrentInputs { get; init; }
    public FactoryReconfigurationInputs? DesiredInputs { get; init; }
    public IReadOnlyList<FactoryConfigurationChange> Changes { get; init; } = [];
    public IReadOnlyList<FactoryReconfigurationRevision> Revisions { get; init; } = [];
    public IReadOnlyList<BoundaryPlanFinding> Findings { get; init; } = [];
    public IReadOnlyList<FoundationOperation> OwnershipHistory { get; init; } = [];
    public IReadOnlyList<string> PreservedAssets { get; init; } = [];
    public IReadOnlyList<string> ReusedPrerequisites { get; init; } = [];
    public IReadOnlyList<AuditRecordIdentity> AffectedEvidence { get; init; } = [];
    public FactoryKeyCustody? KeyCustody { get; init; }
    public bool DeliberatelyPaused { get; init; }
    public bool RenewedApprovalRequired { get; init; }
    public bool HasRemoteEffects { get; init; }
    public bool? AdmissionClosed { get; init; }
    public FactoryReconfigurationOperation? Operation { get; init; }
    public bool ConfigurationApplied => false;
    public bool IsActivated => false;
    public bool CanApply => false;
    public string NextActor => "operator / eligible resource, source and model administrators";
    public string NextAction => Operation is not null
        ? "Resume only this original operation with its exact complete input. Inspect admitted and issued work; old admission remains closed "
            + "until exact effects, access and configuration are reconciled, renewed evidence and preview complete, and activation is explicitly approved."
        : "Review the exact current fingerprint before --apply. Do not edit retained governance, reset approvals, delete claims or replay provisioning.";
}
