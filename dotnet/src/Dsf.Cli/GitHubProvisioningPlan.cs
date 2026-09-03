using Dsf.Core.Instances;

namespace Dsf.Cli;

internal interface IGitHubProvisioningClient
{
    Task<GitHubRepositoryProvisioningResult> EnsureRepositoryAsync(
        EnsureRepositoryRequest request,
        CancellationToken cancellationToken);

    Task EnsureSeedRepoAsync(
        EnsureSeedRepoRequest request,
        CancellationToken cancellationToken);

    Task EnsureLabelsAsync(EnsureLabelsRequest request, CancellationToken cancellationToken);

    Task<GitHubAppBindingProvisioningResult?> EnsureAppBindingAsync(
        EnsureAppBindingRequest request,
        CancellationToken cancellationToken);

    Task<GitHubRulesetProvisioningResult> EnsureBranchProtectionRulesetAsync(
        EnsureBranchProtectionRulesetRequest request,
        CancellationToken cancellationToken);
}

internal sealed record GitHubProvisioningPlan(IReadOnlyList<GitHubProvisioningRequest> Requests)
{
    public static GitHubProvisioningPlan Build(InstanceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var repoFullName = definition.GitHub.FullName();
        var defaultBranch = definition.GitHub.DefaultBranch;
        return new GitHubProvisioningPlan(
            [
                new EnsureRepositoryRequest(
                    definition.GitHub.Owner,
                    definition.GitHub.Repository,
                    definition.GitHub.Visibility,
                    defaultBranch),
                new EnsureSeedRepoRequest(
                    repoFullName,
                    defaultBranch),
                new EnsureLabelsRequest(repoFullName, LabelDefinitions()),
                new EnsureAppBindingRequest(
                    repoFullName,
                    definition.GitHub.AppId,
                    definition.GitHub.InstallationId),
                new EnsureBranchProtectionRulesetRequest(
                    repoFullName,
                    defaultBranch,
                    ["ci"],
                    RequiredApprovingReviews(definition.Product.CreationMaturity),
                    definition.GitHub.BranchProtectionRulesetId,
                    AllowAutoMerge: string.Equals(
                        definition.Product.CreationMaturity,
                        "high",
                        StringComparison.Ordinal)),
            ]);
    }

    public async Task<GitHubProvisioningResult> ExecuteAsync(
        IGitHubProvisioningClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        GitHubRepositoryProvisioningResult? repository = null;
        GitHubAppBindingProvisioningResult? appBinding = null;
        GitHubRulesetProvisioningResult? ruleset = null;
        string? resolvedRepoFullName = null;

        foreach (var request in Requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (request)
            {
                case EnsureRepositoryRequest repositoryRequest:
                    repository = await client.EnsureRepositoryAsync(
                        repositoryRequest,
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(repository?.Owner))
                    {
                        resolvedRepoFullName = $"{repository.Owner}/{repositoryRequest.Repository}";
                    }
                    break;
                case EnsureSeedRepoRequest seedRequest:
                    var effectiveSeed = resolvedRepoFullName is not null && seedRequest.RepositoryFullName != resolvedRepoFullName
                        ? seedRequest with { RepositoryFullName = resolvedRepoFullName }
                        : seedRequest;
                    await client.EnsureSeedRepoAsync(effectiveSeed, cancellationToken);
                    break;
                case EnsureLabelsRequest labelsRequest:
                    var effectiveLabels = resolvedRepoFullName is not null && labelsRequest.RepositoryFullName != resolvedRepoFullName
                        ? labelsRequest with { RepositoryFullName = resolvedRepoFullName }
                        : labelsRequest;
                    await client.EnsureLabelsAsync(effectiveLabels, cancellationToken);
                    break;
                case EnsureAppBindingRequest { InstallationId: not null } appRequest:
                    var effectiveApp = resolvedRepoFullName is not null && appRequest.RepositoryFullName != resolvedRepoFullName
                        ? appRequest with { RepositoryFullName = resolvedRepoFullName }
                        : appRequest;
                    appBinding = await client.EnsureAppBindingAsync(effectiveApp, cancellationToken);
                    break;
                case EnsureAppBindingRequest:
                    break;
                case EnsureBranchProtectionRulesetRequest rulesetRequest:
                    var effectiveRuleset = resolvedRepoFullName is not null && rulesetRequest.RepositoryFullName != resolvedRepoFullName
                        ? rulesetRequest with { RepositoryFullName = resolvedRepoFullName }
                        : rulesetRequest;
                    ruleset = await client.EnsureBranchProtectionRulesetAsync(
                        effectiveRuleset,
                        cancellationToken);
                    break;
            }
        }

        return new GitHubProvisioningResult(repository, appBinding, ruleset);
    }

    private static IReadOnlyList<GitHubLabelDefinition> LabelDefinitions() =>
        [
            new("feature"),
            new("bug"),
            new("chore"),
            new("api"),
            new("ui"),
            new("infra"),
            new("sev-low"),
            new("sev-medium"),
            new("sev-high"),
            new("sev-critical"),
            new(
                "creation:ready",
                "1d76db",
                "Council-filed issue ready for the creation phase (Coding Agent)"),
            new(
                "incident",
                "b60205",
                "SRE-filed incident the feature council reflects on"),
            new(
                "dsf-outcome:approved",
                "0e8a16",
                "Human verdict: the council's proposal was approved as filed"),
            new(
                "dsf-outcome:rejected",
                "b60205",
                "Human verdict: the council's proposal was rejected"),
            new(
                "dsf-outcome:changes-requested",
                "fbca04",
                "Human verdict: the council's proposal needs changes before it can land"),
        ];

    private static int RequiredApprovingReviews(string creationMaturity) =>
        string.Equals(creationMaturity, "high", StringComparison.Ordinal) ? 0 : 1;
}

internal abstract record GitHubProvisioningRequest(string Method);

internal sealed record EnsureRepositoryRequest(
    string Owner,
    string Repository,
    string Visibility,
    string DefaultBranch)
    : GitHubProvisioningRequest("ensure_repository");

internal sealed record EnsureSeedRepoRequest(
    string RepositoryFullName,
    string DefaultBranch,
    string WorkflowPath = ".github/workflows/ci.yml")
    : GitHubProvisioningRequest("seed_repo");

internal sealed record EnsureLabelsRequest(
    string RepositoryFullName,
    IReadOnlyList<GitHubLabelDefinition> Labels)
    : GitHubProvisioningRequest("ensure_labels");

internal sealed record EnsureAppBindingRequest(
    string RepositoryFullName,
    string? AppId,
    string? InstallationId)
    : GitHubProvisioningRequest("ensure_app_binding");

internal sealed record EnsureBranchProtectionRulesetRequest(
    string RepositoryFullName,
    string TargetBranch,
    IReadOnlyList<string> RequiredStatusChecks,
    int RequiredApprovingReviewCount,
    long? ExistingRulesetId,
    string Name = "dsf-creation",
    bool AllowAutoMerge = false)
    : GitHubProvisioningRequest("ensure_branch_protection_ruleset");

internal sealed record GitHubLabelDefinition(
    string Name,
    string? Color = null,
    string? Description = null);

internal sealed record GitHubRepositoryProvisioningResult(long RepositoryId, string DefaultBranch, string? Owner = null);

internal sealed record GitHubAppBindingProvisioningResult(string? AppId, string InstallationId);

internal sealed record GitHubRulesetProvisioningResult(long? RulesetId);

internal sealed record GitHubProvisioningResult(
    GitHubRepositoryProvisioningResult? Repository,
    GitHubAppBindingProvisioningResult? AppBinding,
    GitHubRulesetProvisioningResult? Ruleset)
{
    public InstanceDefinition ApplyTo(InstanceDefinition definition) =>
        definition with
        {
            GitHub = definition.GitHub with
            {
                Owner = string.IsNullOrWhiteSpace(definition.GitHub.Owner)
                    ? Repository?.Owner ?? definition.GitHub.Owner
                    : definition.GitHub.Owner,
                RepositoryId = Repository?.RepositoryId ?? definition.GitHub.RepositoryId,
                DefaultBranch = Repository?.DefaultBranch ?? definition.GitHub.DefaultBranch,
                AppId = AppBinding?.AppId ?? definition.GitHub.AppId,
                InstallationId = AppBinding?.InstallationId ?? definition.GitHub.InstallationId,
                BranchProtectionRulesetId = Ruleset?.RulesetId
                    ?? definition.GitHub.BranchProtectionRulesetId,
            },
        };
}

internal static class GitHubSettingsExtensions
{
    public static string FullName(this GitHubSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Owner)
            ? settings.Repository
            : $"{settings.Owner}/{settings.Repository}";
}
