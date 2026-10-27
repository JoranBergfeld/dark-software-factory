namespace Dsf.Core.Onboarding;

public enum SupplementalState { NotSelected, ReadySupplemental, Partial, Blocked, Failed, Canceled, Unknown }

public sealed record FactorySupplemental(SupplementalState State, string Detail)
{
    public Guid OperationId { get; init; }
    public string? ApprovalFingerprint { get; init; }
    public SupplementalConfiguration? Configuration { get; init; }
    public string Stage { get; init; } = "preview";
    public int OperationCount { get; init; }
    public IReadOnlyList<string> EffectHashes { get; init; } = [];
    public Guid? CheckId { get; init; }
    public IReadOnlyList<SupplementalCheck> Checks { get; init; } = [];
    public SupplementalResourceReview? ResourceReview => Configuration is { } config
        ? new(FactorySupplementalValidation.Resources(config), config.ResourceApprovalSha256) : null;
}

public sealed record SupplementalSelection(int SchemaVersion, string RuntimeImage, string AudienceClientId,
    IReadOnlyList<SupplementalSourceSelection> Sources);

public sealed record SupplementalSourceSelection(string Kind, string Query, bool PublicQueryApproved)
{
    public string? SecretName { get; init; }
    public string? SecretVersion { get; init; }
    public string? SearchResourceId { get; init; }
    public string? KnowledgeBase { get; init; }
    public string? KnowledgeBaseETag { get; init; }
    public string? MetadataRoleDefinitionId { get; init; }
    public bool DedicatedSearchService { get; init; }
    public IReadOnlyList<SupplementalKnowledgeSource> KnowledgeSources { get; init; } = [];
}

public sealed record SupplementalKnowledgeSource(string Name, string IndexName, string SourceETag, string IndexETag);

public sealed record SupplementalConfiguration(Guid FactoryId, string PlanFingerprint, string ProfilePin,
    string ProductEndpoint, string FactoryGroup, string Location, SupplementalSelection Selection,
    FoundationOperation Hosting, FoundationOperation? Vault)
{
    public string? TemplateSha256 { get; init; }
    public string ResourceApprovalSha256 { get; init; } = "";
    public IReadOnlyDictionary<string, string> PrerequisiteRevisions { get; init; } = new Dictionary<string, string>();
}

public sealed record SupplementalCheck(string Kind, SupplementalState State, SupplementalReason Reason, string ProofKey, string ProofSha256);
public sealed record SupplementalGrant(string Target, string Scope, string RoleDefinitionId)
{
    public IReadOnlyList<string> Actions { get; init; } = [];
    public IReadOnlyList<string> DataActions { get; init; } = [];
}
public sealed record SupplementalSourceResources(string Kind, string IdentityTarget, string HostTarget, IReadOnlyList<SupplementalGrant> Grants);
public sealed record SupplementalResourceReview(IReadOnlyList<SupplementalSourceResources> Sources, string Sha256);

public enum SupplementalReason
{
    None, Empty, PermissionDenied, MissingSecret, MissingKnowledgeBase, MalformedResponse,
    Incomplete, Unsupported, AuthorityChanged, IdentityMismatch, NetworkUnavailable, CapacityExceeded, Canceled,
}

public sealed record SupplementalHostConfiguration(Guid FactoryId, string TenantId, string OperatorPrincipalId,
    string SourceIdentityId, string SourcePrincipalId, string SourceClientId, string ConfigurationRevision,
    SupplementalSourceSelection Source, string? SecretUri);

public sealed record SupplementalProofRequest(Guid CheckId, string ConfigurationRevision);
public sealed record SupplementalEvidenceReference(string Reference, string ContentSha256);
public sealed record SupplementalEvidenceItem(string Reference, string Content);
public sealed record SupplementalProof(Guid FactoryId, Guid CheckId, string ConfigurationRevision, string Kind,
    string SourcePrincipalId, SupplementalState State, SupplementalReason Reason, DateTimeOffset ObservedAt,
    IReadOnlyList<SupplementalEvidenceReference> Records, string? NativeSourceRevision, string? SecretVersion)
{
    public string Coverage { get; init; } = FactorySupplementalValidation.Coverage;
}
public sealed record SupplementalEvidence(SupplementalProof Proof, IReadOnlyList<SupplementalEvidenceItem> Items);

public interface IFactorySupplementalEvidenceSource
{
    Task<SupplementalEvidence> ReadAsync(SupplementalProofRequest request, CancellationToken cancellationToken);
}

public sealed class SupplementalSourceException(SupplementalReason reason) : Exception("Supplemental source blocked: " + reason + ". Provider details withheld.")
{
    public SupplementalReason Reason { get; } = reason;
}
