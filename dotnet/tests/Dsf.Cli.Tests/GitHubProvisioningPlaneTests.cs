using Dsf.Cli;
using Dsf.Core.Instances;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class GitHubProvisioningPlaneTests
{
    [Fact]
    public async Task Dry_run_prints_github_operations_without_mutating_github()
    {
        var terminal = PlainTerminal();
        var github = new RecordingGitHubProvisioningClient();

        var exitCode = await CliApplication.InvokeAsync(
            [
                "new", "--product", "paritydemo", "--owner", "acme",
                "--dry-run", "--config-root", ArtifactRoot(),
            ],
            CancellationToken.None,
            terminal,
            github);

        Assert.Equal(0, exitCode);
        Assert.Empty(github.Requests);
        Assert.Contains("create_repo", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("create_labels", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("install_app", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("branch_protection", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_uses_create_or_reuse_and_ensure_request_shapes()
    {
        var plan = GitHubProvisioningPlan.Build(SampleDefinition());

        Assert.Collection(
            plan.Requests,
            request =>
            {
                var repository = Assert.IsType<EnsureRepositoryRequest>(request);
                Assert.Equal("ensure_repository", repository.Method);
                Assert.Equal("acme", repository.Owner);
                Assert.Equal("paritydemo", repository.Repository);
                Assert.Equal("private", repository.Visibility);
                Assert.Equal("main", repository.DefaultBranch);
            },
            request =>
            {
                var labels = Assert.IsType<EnsureLabelsRequest>(request);
                Assert.Equal("ensure_labels", labels.Method);
                Assert.Equal("acme/paritydemo", labels.RepositoryFullName);
                Assert.Equal(
                    [
                        "feature", "bug", "chore", "api", "ui", "infra",
                        "sev-low", "sev-medium", "sev-high", "sev-critical",
                        "creation:ready", "incident",
                    ],
                    labels.Labels.Select(label => label.Name).ToArray());
            },
            request =>
            {
                var app = Assert.IsType<EnsureAppBindingRequest>(request);
                Assert.Equal("ensure_app_binding", app.Method);
                Assert.Equal("acme/paritydemo", app.RepositoryFullName);
                Assert.Equal("42", app.InstallationId);
            },
            request =>
            {
                var ruleset = Assert.IsType<EnsureBranchProtectionRulesetRequest>(request);
                Assert.Equal("ensure_branch_protection_ruleset", ruleset.Method);
                Assert.Equal("acme/paritydemo", ruleset.RepositoryFullName);
                Assert.Equal("main", ruleset.TargetBranch);
                Assert.Equal(["ci"], ruleset.RequiredStatusChecks);
                Assert.Equal(1, ruleset.RequiredApprovingReviewCount);
            });
    }

    [Fact]
    public async Task Write_plan_rerun_reuses_existing_github_identity()
    {
        var root = ArtifactRoot();
        try
        {
            var existing = SampleDefinition() with
            {
                GitHub = SampleDefinition().GitHub with
                {
                    RepositoryId = 123,
                    DefaultBranch = "trunk",
                    AppId = "7",
                    InstallationId = "42",
                    BranchProtectionRulesetId = 456,
                },
            };
            InstanceDefinitions.Write(existing, root);

            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "paritydemo", "--owner", "acme",
                    "--dry-run", "--write-plan", "--config-root", root,
                ],
                CancellationToken.None,
                PlainTerminal(),
                new RecordingGitHubProvisioningClient());

            Assert.Equal(0, exitCode);
            var written = InstanceDefinitions.Read(InstanceDefinitions.PathFor(root, "paritydemo"));
            Assert.Equal(123, written.GitHub.RepositoryId);
            Assert.Equal("trunk", written.GitHub.DefaultBranch);
            Assert.Equal("7", written.GitHub.AppId);
            Assert.Equal("42", written.GitHub.InstallationId);
            Assert.Equal(456, written.GitHub.BranchProtectionRulesetId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Executed_plan_updates_clean_instance_github_identifiers()
    {
        var definition = SampleDefinition() with
        {
            GitHub = SampleDefinition().GitHub with
            {
                AppId = null,
                InstallationId = "42",
                RepositoryId = null,
                BranchProtectionRulesetId = null,
            },
        };
        var client = new RecordingGitHubProvisioningClient
        {
            RepositoryId = 123,
            AppId = "7",
            InstallationId = "42",
            RulesetId = 456,
        };

        var result = await GitHubProvisioningPlan.Build(definition).ExecuteAsync(client, CancellationToken.None);
        var updated = result.ApplyTo(definition);
        var json = InstanceDefinitions.Serialize(updated);

        Assert.Equal(123, updated.GitHub.RepositoryId);
        Assert.Equal("main", updated.GitHub.DefaultBranch);
        Assert.Equal("7", updated.GitHub.AppId);
        Assert.Equal("42", updated.GitHub.InstallationId);
        Assert.Equal(456, updated.GitHub.BranchProtectionRulesetId);
        Assert.DoesNotContain("BEGIN RSA", json, StringComparison.Ordinal);
        Assert.DoesNotContain("privateKey\"", json, StringComparison.Ordinal);
    }

    private static InstanceDefinition SampleDefinition() => new()
    {
        Product = new ProductSettings { Key = "paritydemo" },
        Runtime = new RuntimeSettings(),
        Governance = new GovernanceSettings(),
        GitHub = new GitHubSettings
        {
            Owner = "acme",
            Repository = "paritydemo",
            Visibility = "private",
            InstallationId = "42",
        },
        Azure = new AzureSettings
        {
            NamePrefix = "parityde0000",
            ResourceGroup = "rg-dsf-paritydemo",
            DeploymentName = "dsf-paritydemo",
            SreAgent = new SreAgentSettings
            {
                Name = "dsf-sre-paritydemo",
                ResourceGroup = "rg-dsf-sre-paritydemo",
                MonitoredResourceGroups = ["rg-dsf-paritydemo"],
            },
        },
        Status = new InstanceStatus { GeneratedAt = DateTimeOffset.UnixEpoch },
    };

    private static ScriptedTerminal PlainTerminal() =>
        new(new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false), []);

    private static string ArtifactRoot()
    {
        var root = FindSolutionRoot();
        return Path.Combine(root, ".test-artifacts", "github-plane", Guid.NewGuid().ToString("N"));
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dsf.sln")))
        {
            dir = dir.Parent;
        }

        return (dir ?? throw new InvalidOperationException("Could not locate Dsf.sln.")).FullName;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class RecordingGitHubProvisioningClient : IGitHubProvisioningClient
{
    public List<GitHubProvisioningRequest> Requests { get; } = [];

    public long RepositoryId { get; init; } = 123;

    public string AppId { get; init; } = "7";

    public string InstallationId { get; init; } = "42";

    public long RulesetId { get; init; } = 456;

    public Task<GitHubRepositoryProvisioningResult> EnsureRepositoryAsync(
        EnsureRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new GitHubRepositoryProvisioningResult(RepositoryId, request.DefaultBranch));
    }

    public Task EnsureLabelsAsync(EnsureLabelsRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.CompletedTask;
    }

    public Task<GitHubAppBindingProvisioningResult?> EnsureAppBindingAsync(
        EnsureAppBindingRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult<GitHubAppBindingProvisioningResult?>(
            new GitHubAppBindingProvisioningResult(AppId, InstallationId));
    }

    public Task<GitHubRulesetProvisioningResult> EnsureBranchProtectionRulesetAsync(
        EnsureBranchProtectionRulesetRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new GitHubRulesetProvisioningResult(RulesetId));
    }
}
