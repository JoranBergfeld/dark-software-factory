using Dsf.Core.Instances;

namespace Dsf.Cli;

internal interface IGitHubProvisioningClient
{
    Task<GitHubRepositoryProvisioningResult> EnsureRepositoryAsync(
        EnsureRepositoryRequest request,
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
                new EnsureLabelsRequest(repoFullName, LabelDefinitions()),
                new EnsureAppBindingRequest(repoFullName, definition.GitHub.InstallationId),
                new EnsureBranchProtectionRulesetRequest(
                    repoFullName,
                    defaultBranch,
                    ["ci"],
                    RequiredApprovingReviews(definition.Product.CreationMaturity),
                    definition.GitHub.BranchProtectionRulesetId),
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

        foreach (var request in Requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (request)
            {
                case EnsureRepositoryRequest repositoryRequest:
                    repository = await client.EnsureRepositoryAsync(
                        repositoryRequest,
                        cancellationToken);
                    break;
                case EnsureLabelsRequest labelsRequest:
                    await client.EnsureLabelsAsync(labelsRequest, cancellationToken);
                    break;
                case EnsureAppBindingRequest { InstallationId: not null } appRequest:
                    appBinding = await client.EnsureAppBindingAsync(appRequest, cancellationToken);
                    break;
                case EnsureAppBindingRequest:
                    break;
                case EnsureBranchProtectionRulesetRequest rulesetRequest:
                    ruleset = await client.EnsureBranchProtectionRulesetAsync(
                        rulesetRequest,
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

internal sealed record EnsureLabelsRequest(
    string RepositoryFullName,
    IReadOnlyList<GitHubLabelDefinition> Labels)
    : GitHubProvisioningRequest("ensure_labels");

internal sealed record EnsureAppBindingRequest(string RepositoryFullName, string? InstallationId)
    : GitHubProvisioningRequest("ensure_app_binding");

internal sealed record EnsureBranchProtectionRulesetRequest(
    string RepositoryFullName,
    string TargetBranch,
    IReadOnlyList<string> RequiredStatusChecks,
    int RequiredApprovingReviewCount,
    long? ExistingRulesetId)
    : GitHubProvisioningRequest("ensure_branch_protection_ruleset");

internal sealed record GitHubLabelDefinition(
    string Name,
    string? Color = null,
    string? Description = null);

internal sealed record GitHubRepositoryProvisioningResult(long RepositoryId, string DefaultBranch);

internal sealed record GitHubAppBindingProvisioningResult(string? AppId, string InstallationId);

internal sealed record GitHubRulesetProvisioningResult(long RulesetId);

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
                RepositoryId = Repository?.RepositoryId ?? definition.GitHub.RepositoryId,
                DefaultBranch = Repository?.DefaultBranch ?? definition.GitHub.DefaultBranch,
                AppId = AppBinding?.AppId ?? definition.GitHub.AppId,
                InstallationId = AppBinding?.InstallationId ?? definition.GitHub.InstallationId,
                BranchProtectionRulesetId = Ruleset?.RulesetId
                    ?? definition.GitHub.BranchProtectionRulesetId,
            },
        };
}

internal sealed class UnavailableGitHubProvisioningClient : IGitHubProvisioningClient
{
    public Task<GitHubRepositoryProvisioningResult> EnsureRepositoryAsync(
        EnsureRepositoryRequest request,
        CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task EnsureLabelsAsync(EnsureLabelsRequest request, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task<GitHubAppBindingProvisioningResult?> EnsureAppBindingAsync(
        EnsureAppBindingRequest request,
        CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task<GitHubRulesetProvisioningResult> EnsureBranchProtectionRulesetAsync(
        EnsureBranchProtectionRulesetRequest request,
        CancellationToken cancellationToken) =>
        throw NotConfigured();

    private static InvalidOperationException NotConfigured() =>
        new("GitHub provisioning client is not configured; use --dry-run to preview safely.");
}

internal static class GitHubSettingsExtensions
{
    public static string FullName(this GitHubSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Owner)
            ? settings.Repository
            : $"{settings.Owner}/{settings.Repository}";
}
