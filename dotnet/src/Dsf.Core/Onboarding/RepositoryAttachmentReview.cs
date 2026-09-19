namespace Dsf.Core.Onboarding;

public enum RepositoryReviewState
{
    Confirmed,
    Pending,
    Unknown,
    Denied,
    Blocked,
}

public sealed record RepositoryReviewFinding(
    string Check,
    RepositoryReviewState State,
    string Detail,
    string? NextActor = null,
    string? NextAction = null);

public sealed record ReviewedGitHubRepository(
    long Id,
    long OwnerId,
    string Owner,
    string Name,
    string DefaultBranch,
    string Visibility,
    bool Archived,
    bool Disabled,
    bool HasIssues);

public sealed record RepositoryReviewOperator(long Id, string Login);

public sealed record ReviewedGitHubApp(
    long Id,
    string Slug,
    IReadOnlyDictionary<string, string> RequestedPermissions);

public sealed record ReviewedGitHubInstallation(
    long Id,
    long AppId,
    long AccountId,
    string AccountLogin,
    string AccountType,
    string RepositorySelection,
    DateTimeOffset? SuspendedAt,
    IReadOnlyDictionary<string, string> GrantedPermissions);

public sealed record ReviewedRepositoryLabel(
    string Name,
    long? Id,
    string? Color,
    string? Description,
    string RequiredMeaning,
    RepositoryReviewState SemanticState);

public static class DecideRepositoryLabels
{
    public static IReadOnlyDictionary<string, string> Meanings { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["dsf:proposal"] = "DSF proposal for human consideration; not a delivery assignment.",
            ["dsf-outcome:approved"] = "Human-approved DSF proposal; not a delivery assignment.",
            ["dsf-outcome:rejected"] = "Human-rejected DSF proposal.",
            ["dsf-outcome:changes-requested"] = "DSF proposal requiring human-requested changes.",
        });
}

public enum RepositoryMutationClass
{
    ProposalIssue,
    CharterPullRequest,
    LabelCreation,
}

public enum RepositoryAutomationDeclaration
{
    Unknown,
    Clear,
    Conflict,
}

public sealed record RepositoryMutationReview(
    RepositoryMutationClass Mutation,
    RepositoryReviewState State,
    RepositoryAutomationDeclaration Declaration,
    string? DeclarationOwner,
    bool VisibleInventoryComplete,
    IReadOnlyList<string> Evidence)
{
    public bool ExhaustiveTechnicalProof => false;
}

public sealed record RepositoryAttachmentReview(
    int SchemaVersion,
    DateTimeOffset ObservedAt,
    ReviewedGitHubRepository? Repository,
    IReadOnlyList<RepositoryReviewFinding> Findings)
{
    public RepositoryReviewOperator? Operator { get; init; }
    public ReviewedGitHubApp? App { get; init; }
    public ReviewedGitHubInstallation? Installation { get; init; }
    public IReadOnlyList<ReviewedRepositoryLabel> Labels { get; init; } = [];
    public IReadOnlyList<RepositoryMutationReview> Automation { get; init; } = [];

    public IEnumerable<RepositoryReviewFinding> FindingsFor(RepositoryMutationClass mutation) =>
        Findings.Where(f => !f.Check.StartsWith("automation:", StringComparison.Ordinal)
            || f.Check == "automation:" + mutation);

    public RepositoryReviewState State =>
        Findings.Any(finding => finding.State is RepositoryReviewState.Denied or RepositoryReviewState.Blocked)
            ? RepositoryReviewState.Blocked
            : Findings.Any(finding => finding.State is RepositoryReviewState.Unknown)
                ? RepositoryReviewState.Unknown
                : Findings.Any(finding => finding.State is RepositoryReviewState.Pending)
                    ? RepositoryReviewState.Pending
                    : RepositoryReviewState.Confirmed;
}
