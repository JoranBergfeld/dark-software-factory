using Dsf.Cli;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

/// <summary>
/// Records every discovery call so a preview can be proven read-only: the port exposes
/// no mutation, and these tests assert the exact reads the CLI performed.
/// </summary>
internal sealed class RecordingDiscoveryClient(
    AzureIdentityContext? identity,
    AzureApplicationInventory inventory) : IAzureApplicationDiscoveryClient
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    public Task<AzureIdentityContext?> GetSignedInContextAsync(CancellationToken cancellationToken)
    {
        _calls.Add("account-show");
        return Task.FromResult(identity);
    }

    public Task<AzureApplicationInventory> DiscoverAsync(
        string tenantId,
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        _calls.Add($"discover:{tenantId}/{subscriptionId}");
        return Task.FromResult(inventory);
    }
}

public sealed class OnboardBoundaryPreviewTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Subscription = "22222222-2222-2222-2222-222222222222";

    private const string ApiId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-shop-api/providers/Microsoft.Web/sites/shop-api";

    private const string JobsId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-shop-jobs/providers/Microsoft.Web/sites/shop-jobs";

    private const string SharedId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform/providers/Microsoft.Web/sites/platform-gateway";

    private const string StagingId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-shop-stg/providers/Microsoft.Web/sites/shop-api-stg";

    private const string ClaimedId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-shop-api/providers/Microsoft.Web/sites/shop-legacy";

    private const string UnreadableId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-shop-api/providers/Microsoft.Web/sites/shop-opaque";

    private const string WorkspaceId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-shop-obs/providers/Microsoft.OperationalInsights/workspaces/law-shop-prod";

    private const string SharedWorkspaceId =
        "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform/providers/Microsoft.OperationalInsights/workspaces/law-platform";

    private const string OtherSubscriptionId =
        "/subscriptions/33333333-3333-3333-3333-333333333333/resourceGroups/rg-other/providers/Microsoft.Web/sites/other-api";

    [Fact]
    public async Task Preview_categorizes_an_exact_multi_group_selection_without_side_effects()
    {
        var discovery = Discovery();
        var terminal = Redirected();

        var exitCode = await CliApplication.InvokeAsync(
            PreviewArguments(ApiId, JobsId),
            CancellationToken.None,
            terminal,
            discovery);

        Assert.Equal(0, exitCode);
        Assert.Contains("outcome: REVIEWABLE", terminal.Output, StringComparison.Ordinal);

        // Exactly the two selected resources across two groups; no sibling, parent, or dependency.
        Assert.Contains(ApiId, terminal.Output, StringComparison.Ordinal);
        Assert.Contains(JobsId, terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(SharedId, terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(StagingId, terminal.Output, StringComparison.Ordinal);

        // Categories are separate, not one undifferentiated list.
        foreach (var category in new[]
                 {
                     "preserved application/repository assets",
                     "newly owned factory resources (proposed)",
                     "reused owner/model prerequisites",
                     "proposed grant scopes and factory identities",
                     "owner registry claims",
                     "allowed repository additions",
                     "costs and omissions",
                 })
        {
            Assert.Contains(category, terminal.Output, StringComparison.Ordinal);
        }

        Assert.Contains("application-environment=production factory-environment=dev", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("Log Analytics Reader", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("do not exist yet and have no principal ID", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("no tag is written", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("not a global lock", terminal.Output, StringComparison.Ordinal);
        Assert.Contains(
            "created no reservation, factory resource, role assignment, secret copy, label, issue, or pull request",
            terminal.Output,
            StringComparison.Ordinal);

        // Read-only seam: only identity and inventory reads reached Azure.
        Assert.Equal(["account-show", $"discover:{Tenant}/{Subscription}"], discovery.Calls);
        Assert.Empty(terminal.Prompts);
    }

    [Fact]
    public async Task Preview_rejects_shared_staging_cross_subscription_claimed_and_unreadable_selections()
    {
        var discovery = Discovery();
        var terminal = Redirected();

        var exitCode = await CliApplication.InvokeAsync(
            PreviewArguments(ApiId, SharedId, StagingId, OtherSubscriptionId, ClaimedId, UnreadableId),
            CancellationToken.None,
            terminal,
            discovery);

        Assert.Equal(1, exitCode);
        Assert.Contains("outcome: BLOCKED", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("declared shared with other applications", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("belongs to environment 'staging'", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("not discovered as an application resource", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("never authorizes adoption", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("unreadable state is not unclaimed", terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("REVIEWABLE", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_rejects_shared_and_unsupported_evidence_backends_separately_from_membership()
    {
        var discovery = Discovery();
        var terminal = Redirected();

        var exitCode = await CliApplication.InvokeAsync(
            [
                "onboard", "decide", "preview",
                "--product", "shop",
                "--tenant", Tenant,
                "--subscription", Subscription,
                "--app-environment", "production",
                "--application-resource", ApiId,
                "--evidence-backend", SharedWorkspaceId,
                "--evidence-backend", ApiId,
                "--dedicated",
                "--cross-owner-reviewed",
                "--owner-appconfig-endpoint", "https://appcs-dsf-owner.azconfig.io",
                "--owner-keyvault-uri", "https://kv-dsf-owner.vault.azure.net/",
            ],
            CancellationToken.None,
            terminal,
            discovery);

        Assert.Equal(1, exitCode);
        Assert.Contains("declared shared with other applications or environments", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("not discovered as an evidence backend", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("the two selections stay separate", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_requires_dedication_and_cross_owner_review_for_this_exact_selection()
    {
        var discovery = Discovery();
        var terminal = Redirected();

        var exitCode = await CliApplication.InvokeAsync(
            [
                "onboard", "decide", "preview",
                "--product", "shop",
                "--tenant", Tenant,
                "--subscription", Subscription,
                "--app-environment", "production",
                "--application-resource", ApiId,
                "--evidence-backend", WorkspaceId,
                "--owner-appconfig-endpoint", "https://appcs-dsf-owner.azconfig.io",
                "--owner-keyvault-uri", "https://kv-dsf-owner.vault.azure.net/",
            ],
            CancellationToken.None,
            terminal,
            discovery);

        Assert.Equal(1, exitCode);
        Assert.Contains("absence of DSF tags is not proof of dedication", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("not a global technical lock", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_reports_missing_owner_prerequisites_as_an_administrator_handoff()
    {
        var discovery = Discovery();
        var terminal = Redirected();
        var priorEndpoint = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        var priorVault = Environment.GetEnvironmentVariable("DSF_OWNER_KEYVAULT_URI");
        Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", null);
        Environment.SetEnvironmentVariable("DSF_OWNER_KEYVAULT_URI", null);

        int exitCode;
        try
        {
            exitCode = await CliApplication.InvokeAsync(
                [
                    "onboard", "decide", "preview",
                    "--product", "shop",
                    "--tenant", Tenant,
                    "--subscription", Subscription,
                    "--app-environment", "production",
                    "--application-resource", ApiId,
                    "--evidence-backend", WorkspaceId,
                    "--dedicated",
                    "--cross-owner-reviewed",
                ],
                CancellationToken.None,
                terminal,
                discovery);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", priorEndpoint);
            Environment.SetEnvironmentVariable("DSF_OWNER_KEYVAULT_URI", priorVault);
        }

        Assert.Equal(1, exitCode);
        Assert.Contains("blocked administrator prerequisites: 2", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("dsf bootstrap", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("never creates owner-wide infrastructure implicitly", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_selection_and_explicit_input_produce_the_same_plan()
    {
        var explicitTerminal = Redirected();
        var explicitExitCode = await CliApplication.InvokeAsync(
            PreviewArguments(ApiId, JobsId),
            CancellationToken.None,
            explicitTerminal,
            Discovery());

        var interactiveTerminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            ["production", "1,2", "1", "y", "y"]);
        var interactiveExitCode = await CliApplication.InvokeAsync(
            [
                "onboard", "decide", "preview",
                "--product", "shop",
                "--tenant", Tenant,
                "--subscription", Subscription,
                "--owner-appconfig-endpoint", "https://appcs-dsf-owner.azconfig.io",
                "--owner-keyvault-uri", "https://kv-dsf-owner.vault.azure.net/",
            ],
            CancellationToken.None,
            interactiveTerminal,
            Discovery());

        Assert.Equal(0, explicitExitCode);
        Assert.Equal(0, interactiveExitCode);
        Assert.Equal(Fingerprint(explicitTerminal.Output), Fingerprint(interactiveTerminal.Output));
        Assert.Contains(
            $"[dsf] equivalent: dsf onboard decide preview --product shop --tenant {Tenant} --subscription {Subscription} --app-environment production --application-resource {ApiId} --application-resource {JobsId} --evidence-backend {WorkspaceId} --dedicated --cross-owner-reviewed --factory-environment dev",
            interactiveTerminal.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_selection_of_an_unlisted_number_fails_with_only_that_reason()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            ["production", "1,99"]);

        var exitCode = await CliApplication.InvokeAsync(
            [
                "onboard", "decide", "preview",
                "--product", "shop",
                "--tenant", Tenant,
                "--subscription", Subscription,
            ],
            CancellationToken.None,
            terminal,
            Discovery());

        Assert.Equal(1, exitCode);
        Assert.Contains("'99' is not one of the listed application resources", terminal.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("no application resource was selected", terminal.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_declarations_accept_mixed_case_confirmations()
    {
        var terminal = new ScriptedTerminal(
            new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
            ["production", "1", "1", "Yes", "Y"]);

        var exitCode = await CliApplication.InvokeAsync(
            [
                "onboard", "decide", "preview",
                "--product", "shop",
                "--tenant", Tenant,
                "--subscription", Subscription,
                "--owner-appconfig-endpoint", "https://appcs-dsf-owner.azconfig.io",
                "--owner-keyvault-uri", "https://kv-dsf-owner.vault.azure.net/",
            ],
            CancellationToken.None,
            terminal,
            Discovery());

        Assert.Equal(0, exitCode);
        Assert.Contains("--dedicated --cross-owner-reviewed", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirected_preview_rejects_incomplete_input_without_inventing_a_selection()
    {
        var discovery = Discovery();
        var terminal = Redirected();

        var exitCode = await CliApplication.InvokeAsync(
            [
                "onboard", "decide", "preview",
                "--product", "shop",
                "--tenant", Tenant,
                "--subscription", Subscription,
                "--dedicated",
                "--cross-owner-reviewed",
            ],
            CancellationToken.None,
            terminal,
            discovery);

        Assert.Equal(1, exitCode);
        Assert.Contains("explicit input is incomplete", terminal.Error, StringComparison.Ordinal);
        Assert.Contains("a generic --yes flag is not selection authority", terminal.Error, StringComparison.Ordinal);
        Assert.Empty(terminal.Prompts);
        Assert.DoesNotContain("outcome:", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_selection_invalidates_the_previously_reviewed_fingerprint()
    {
        var reviewed = Redirected();
        await CliApplication.InvokeAsync(
            PreviewArguments(ApiId),
            CancellationToken.None,
            reviewed,
            Discovery());
        var reviewedFingerprint = Fingerprint(reviewed.Output);

        var replayed = Redirected();
        var sameSelection = await CliApplication.InvokeAsync(
            [.. PreviewArguments(ApiId), "--expect-fingerprint", reviewedFingerprint],
            CancellationToken.None,
            replayed,
            Discovery());

        var widened = Redirected();
        var widenedSelection = await CliApplication.InvokeAsync(
            [.. PreviewArguments(ApiId, JobsId), "--expect-fingerprint", reviewedFingerprint],
            CancellationToken.None,
            widened,
            Discovery());

        Assert.Equal(0, sameSelection);
        Assert.Equal(1, widenedSelection);
        Assert.Contains("is stale", widened.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome:", widened.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_without_an_azure_cli_identity_fails_explicitly()
    {
        var terminal = Redirected();

        var exitCode = await CliApplication.InvokeAsync(
            PreviewArguments(ApiId),
            CancellationToken.None,
            terminal,
            new RecordingDiscoveryClient(null, Inventory()));

        Assert.Equal(1, exitCode);
        Assert.Contains("no Azure CLI identity is available", terminal.Error, StringComparison.Ordinal);
        Assert.Contains("az login", terminal.Error, StringComparison.Ordinal);
    }

    private static string[] PreviewArguments(params string[] resourceIds)
    {
        var arguments = new List<string>
        {
            "onboard", "decide", "preview",
            "--product", "shop",
            "--tenant", Tenant,
            "--subscription", Subscription,
            "--app-environment", "production",
        };
        foreach (var resourceId in resourceIds)
        {
            arguments.Add("--application-resource");
            arguments.Add(resourceId);
        }

        arguments.AddRange(
        [
            "--evidence-backend", WorkspaceId,
            "--dedicated",
            "--cross-owner-reviewed",
            "--owner-appconfig-endpoint", "https://appcs-dsf-owner.azconfig.io",
            "--owner-keyvault-uri", "https://kv-dsf-owner.vault.azure.net/",
        ]);
        return [.. arguments];
    }

    private const string FingerprintPrefix = "[dsf] plan fingerprint: ";

    private static string Fingerprint(string output) => output
        .Split('\n')
        .Select(line => line.Trim())
        .First(line => line.StartsWith(FingerprintPrefix, StringComparison.Ordinal))[FingerprintPrefix.Length..];

    private static ScriptedTerminal Redirected() => new(
        new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false),
        []);

    private static RecordingDiscoveryClient Discovery() => new(
        new AzureIdentityContext(Tenant, Subscription, "shop-production", "operator@example.com"),
        Inventory());

    private static AzureApplicationInventory Inventory() => new()
    {
        TenantId = Tenant,
        SubscriptionId = Subscription,
        Resources =
        [
            Resource(ApiId, "shop-api", "rg-shop-api", "production"),
            Resource(JobsId, "shop-jobs", "rg-shop-jobs", "production"),
            Resource(SharedId, "platform-gateway", "rg-platform", "production") with { Shared = true },
            Resource(StagingId, "shop-api-stg", "rg-shop-stg", "staging"),
            Resource(ClaimedId, "shop-legacy", "rg-shop-api", "production") with
            {
                ExistingClaimSignal = "dsf-product=other",
            },
            Resource(UnreadableId, "shop-opaque", "rg-shop-api", "production") with { MetadataUnreadable = true },
        ],
        EvidenceBackends =
        [
            new EvidenceBackendCandidate
            {
                ResourceId = WorkspaceId,
                Name = "law-shop-prod",
                Kind = "loganalytics",
                SubscriptionId = Subscription,
                TenantId = Tenant,
                Environment = "production",
            },
            new EvidenceBackendCandidate
            {
                ResourceId = SharedWorkspaceId,
                Name = "law-platform",
                Kind = "loganalytics",
                SubscriptionId = Subscription,
                TenantId = Tenant,
                Environment = "production",
                Shared = true,
            },
        ],
    };

    private static ApplicationResourceCandidate Resource(
        string resourceId,
        string name,
        string resourceGroup,
        string environment) => new()
    {
        ResourceId = resourceId,
        Name = name,
        Type = "Microsoft.Web/sites",
        ResourceGroup = resourceGroup,
        SubscriptionId = Subscription,
        TenantId = Tenant,
        Environment = environment,
    };
}
