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
    public bool HasRemoteEffects => false;
    public bool CanApply => false;
    public string NextActor => "operator / eligible resource, source and model administrators";
    public string NextAction => "This command only inspects and reviews; applying reconfiguration is not available. "
        + "Do not edit retained governance, reset approvals, delete claims or replay provisioning. "
        + "A future apply must quiesce old work, reconcile exact effects/access, retain claims without delete/recreate, "
        + "and obtain renewed evidence, full preview and explicit activation without clearing deliberate pause.";
}
