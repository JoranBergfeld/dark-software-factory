namespace Dsf.Core.Onboarding;

/// <summary>
/// Overall verdict of a read-only repository attachment review. <see cref="Blocked"/> means at
/// least one hard incompatibility was observed (archived repository, disabled issues, or known
/// issue/PR/label-triggered automation); <see cref="Unknown"/> means a required prerequisite
/// could not be established either way; <see cref="Eligible"/> means every checked prerequisite
/// resolved to a compatible, confirmed state.
/// </summary>
public enum RepositoryAttachmentEligibility
{
    Eligible,
    Blocked,
    Unknown,
}

/// <summary>
/// The observed state of one onboarding prerequisite. This is never inferred from repository
/// visibility, a product key, or a supplied identifier alone; it reflects what the reviewer's
/// real, authenticated lookup actually returned.
/// </summary>
public enum PrerequisiteState
{
    /// <summary>The prerequisite was positively verified through a real, authenticated route.</summary>
    Confirmed,

    /// <summary>The prerequisite is known to be awaiting action by a responsible actor.</summary>
    Pending,

    /// <summary>The prerequisite was positively verified to be absent, suspended, or refused.</summary>
    Denied,

    /// <summary>The prerequisite could not be established with the routes available to this run.</summary>
    Unknown,
}

/// <summary>
/// One reviewed prerequisite (registration, installation account/identity, selected-repository
/// membership, granted permissions, suspension, administrator approval, and similar) with its
/// observed state, a human-readable detail, and the actor responsible for the next action when
/// the state is not <see cref="PrerequisiteState.Confirmed"/>.
/// </summary>
public sealed record RepositoryAttachmentPrerequisite(
    string Name,
    PrerequisiteState State,
    string Detail,
    string? ResponsibleActor);

/// <summary>Durable identity and observed metadata for the repository under review.</summary>
public sealed record RepositoryIdentity(
    string RepositoryId,
    string Owner,
    string Name,
    string DefaultBranch,
    bool Archived,
    bool IssuesEnabled,
    string Visibility);

/// <summary>
/// One of the four permitted repository labels reviewed for reuse (never created or altered by
/// this review): whether it already exists, its observed color/description, and whether that
/// existing definition conflicts with the semantics onboarding would require of it.
/// </summary>
public sealed record RepositoryLabelReview(
    string Name,
    bool Exists,
    string? ExistingColor,
    string? ExistingDescription,
    bool ConflictsWithExpectedMeaning);

/// <summary>
/// How a piece of automation evidence was established: an owner's stated declaration is never
/// treated as equivalent to technical proof obtained from a visible workflow or webhook.
/// </summary>
public enum AutomationEvidence
{
    OwnerDeclared,
    VisibleWorkflow,
    VisibleWebhook,
    Unknown,
}

/// <summary>
/// One finding about automation that could react to a permitted repository mutation class
/// (for example, issue creation or label application). A blocking finding requires an external
/// administrator handoff; the review never overrides or repairs the repository automatically.
/// </summary>
public sealed record RepositoryAutomationFinding(
    string MutationClass,
    bool Blocks,
    AutomationEvidence Evidence,
    string Detail);

/// <summary>
/// The complete, typed result of a read-only repository attachment review: durable identity,
/// observed metadata, prerequisite verification, label conflict review, and automation
/// compatibility findings. This never asserts that repository visibility is installation consent
/// or that the application is already onboarded; later application planning consumes this record
/// rather than re-deriving it from raw API responses.
/// </summary>
public sealed record RepositoryAttachmentReview(
    RepositoryIdentity? Repository,
    IReadOnlyList<RepositoryAttachmentPrerequisite> Prerequisites,
    IReadOnlyList<RepositoryLabelReview> Labels,
    IReadOnlyList<RepositoryAutomationFinding> Automation,
    RepositoryAttachmentEligibility Eligibility,
    IReadOnlyList<string> BlockingReasons)
{
    /// <summary>The four repository labels this and later onboarding slices are ever permitted to create or reuse.</summary>
    public static readonly IReadOnlyList<string> PermittedLabelNames =
    [
        "dsf:proposal",
        "dsf-outcome:approved",
        "dsf-outcome:rejected",
        "dsf-outcome:changes-requested",
    ];
}
