using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dsf.Core.Onboarding;

/// <summary>
/// Turns one exact operator selection plus read-only Azure discovery into a categorized,
/// reviewable boundary preview. The planner performs no I/O and authorizes nothing: it
/// only explains what a later, separately approved apply would and would not do.
/// </summary>
public static class ApplicationBoundaryPlanner
{
    /// <summary>Evidence routes this profile supports today.</summary>
    public static readonly IReadOnlyList<string> SupportedEvidenceRoutes = ["loganalytics"];

    /// <summary>Factory identities that a later apply would create. None of them exists yet.</summary>
    public static readonly IReadOnlyList<string> PlannedFactoryIdentities =
        ["council worker identity", "source workload identity", "code-intent identity"];

    public static ApplicationBoundaryPlan Plan(
        ApplicationBoundarySelection selection,
        AzureApplicationInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(inventory);

        var rejections = new List<BoundaryRejection>();
        var prerequisites = new List<BoundaryPlanEntry>();

        var resourceIds = Distinct(selection.ApplicationResourceIds);
        var backendIds = Distinct(selection.EvidenceBackendIds);

        if (resourceIds.Count == 0)
        {
            rejections.Add(new BoundaryRejection(
                "application resources",
                "no application resource was selected; membership is never inferred from a resource group, parent, child, or dependency."));
        }

        if (backendIds.Count == 0)
        {
            rejections.Add(new BoundaryRejection(
                "evidence backends",
                "no evidence backend was selected; selecting an application resource never implies a telemetry backend."));
        }

        if (!string.Equals(inventory.TenantId, selection.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(inventory.SubscriptionId, selection.SubscriptionId, StringComparison.OrdinalIgnoreCase))
        {
            rejections.Add(new BoundaryRejection(
                "authority",
                $"discovery ran in {inventory.TenantId}/{inventory.SubscriptionId} but the selection names {selection.TenantId}/{selection.SubscriptionId}; cross-tenant and cross-subscription footprints are rejected."));
        }

        var selectedResources = new List<ApplicationResourceCandidate>();
        foreach (var resourceId in resourceIds)
        {
            var candidate = inventory.Resources.FirstOrDefault(
                resource => Same(resource.ResourceId, resourceId));
            if (candidate is null)
            {
                rejections.Add(new BoundaryRejection(
                    resourceId,
                    "not discovered as an application resource in the selected subscription; preview never invents a selection."));
                continue;
            }

            if (backendIds.Any(backendId => Same(backendId, resourceId)))
            {
                rejections.Add(new BoundaryRejection(
                    resourceId,
                    "selected both as an application resource and as an evidence backend; the two selections stay separate."));
                continue;
            }

            if (Reject(candidate.MetadataUnreadable
                    ? "tags/metadata could not be read; unreadable state is not unclaimed and preview does not repair it."
                    : null, resourceId, rejections)
                || Reject(candidate.Shared
                    ? "declared shared with other applications; shared application resources are rejected, not reconfigured."
                    : null, resourceId, rejections)
                || Reject(EnvironmentMismatch(candidate.Environment, selection.ApplicationEnvironment), resourceId, rejections)
                || Reject(ClaimConflict(candidate.ExistingClaimSignal), resourceId, rejections)
                || Reject(AuthorityMismatch(candidate.TenantId, candidate.SubscriptionId, selection), resourceId, rejections))
            {
                continue;
            }

            selectedResources.Add(candidate);
        }

        var selectedBackends = new List<EvidenceBackendCandidate>();
        foreach (var backendId in backendIds)
        {
            var candidate = inventory.EvidenceBackends.FirstOrDefault(
                backend => Same(backend.ResourceId, backendId));
            if (candidate is null)
            {
                rejections.Add(new BoundaryRejection(
                    backendId,
                    "not discovered as an evidence backend in the selected subscription; preview never invents a selection."));
                continue;
            }

            if (!SupportedEvidenceRoutes.Contains(candidate.Kind, StringComparer.OrdinalIgnoreCase))
            {
                rejections.Add(new BoundaryRejection(
                    backendId,
                    $"evidence route '{candidate.Kind}' is not supported; supported routes: {string.Join(", ", SupportedEvidenceRoutes)}."));
                continue;
            }

            if (Reject(candidate.MetadataUnreadable
                    ? "tags/metadata could not be read; unreadable state is not unclaimed and preview does not repair it."
                    : null, backendId, rejections)
                || Reject(candidate.Shared
                    ? "declared shared with other applications or environments; shared evidence backends are rejected."
                    : null, backendId, rejections)
                || Reject(EnvironmentMismatch(candidate.Environment, selection.ApplicationEnvironment), backendId, rejections)
                || Reject(ClaimConflict(candidate.ExistingClaimSignal), backendId, rejections)
                || Reject(AuthorityMismatch(candidate.TenantId, candidate.SubscriptionId, selection), backendId, rejections))
            {
                continue;
            }

            selectedBackends.Add(candidate);
        }

        if (!selection.DedicationDeclared)
        {
            rejections.Add(new BoundaryRejection(
                "dedication declaration",
                "the exact selection was not declared dedicated to this application; absence of DSF tags is not proof of dedication."));
        }

        if (!selection.CrossOwnerExclusivityReviewed)
        {
            rejections.Add(new BoundaryRejection(
                "cross-owner exclusivity review",
                "cross-owner exclusivity was not reviewed for this exact selection; an owner-scoped reservation is not a global technical lock."));
        }

        if (string.IsNullOrWhiteSpace(selection.OwnerAppConfigEndpoint))
        {
            prerequisites.Add(new BoundaryPlanEntry(
                "owner App Configuration",
                "missing: an administrator must run `dsf bootstrap`; onboarding never creates owner-wide infrastructure implicitly."));
        }

        if (string.IsNullOrWhiteSpace(selection.OwnerKeyVaultUri))
        {
            prerequisites.Add(new BoundaryPlanEntry(
                "owner Key Vault",
                "missing: an administrator must provide the owner Key Vault holding the DSF GitHub App private key."));
        }

        var outcome = rejections.Count == 0 && prerequisites.Count == 0
            ? BoundaryPlanOutcome.Reviewable
            : BoundaryPlanOutcome.Blocked;

        return new ApplicationBoundaryPlan
        {
            Outcome = outcome,
            Selection = selection with
            {
                ApplicationResourceIds = resourceIds,
                EvidenceBackendIds = backendIds,
            },
            Fingerprint = Fingerprint(selection, resourceIds, backendIds),
            PreservedAssets = Preserved(selectedResources, selectedBackends),
            NewFactoryResources = NewFactoryResources(selection),
            ReusedPrerequisites = Reused(selection),
            ProposedGrants = Grants(selectedResources, selectedBackends),
            RegistryClaims = Claims(selection, selectedResources, selectedBackends),
            RepositoryAdditions = RepositoryAdditions(),
            CostsAndOmissions = CostsAndOmissions(),
            AdministratorPrerequisites = prerequisites,
            Rejections = rejections,
        };
    }

    /// <summary>
    /// Stable fingerprint over every decision-relevant input. A changed selection produces a
    /// different fingerprint, so an earlier review or confirmation cannot silently widen.
    /// </summary>
    public static string Fingerprint(ApplicationBoundarySelection selection)
        => Fingerprint(
            selection,
            Distinct(selection.ApplicationResourceIds),
            Distinct(selection.EvidenceBackendIds));

    private static string Fingerprint(
        ApplicationBoundarySelection selection,
        IReadOnlyList<string> resourceIds,
        IReadOnlyList<string> backendIds)
    {
        var canonical = new StringBuilder();
        canonical.Append("product=").Append(selection.Product).Append('\n');
        canonical.Append("tenant=").Append(selection.TenantId).Append('\n');
        canonical.Append("subscription=").Append(selection.SubscriptionId).Append('\n');
        canonical.Append("applicationEnvironment=").Append(selection.ApplicationEnvironment).Append('\n');
        canonical.Append("factoryEnvironment=").Append(selection.FactoryEnvironment).Append('\n');
        canonical.Append("ownerAppConfig=").Append(selection.OwnerAppConfigEndpoint).Append('\n');
        canonical.Append("ownerKeyVault=").Append(selection.OwnerKeyVaultUri).Append('\n');
        canonical.Append("dedicationDeclared=").Append(selection.DedicationDeclared ? "true" : "false").Append('\n');
        canonical.Append("crossOwnerReviewed=")
            .Append(selection.CrossOwnerExclusivityReviewed ? "true" : "false").Append('\n');
        foreach (var resourceId in resourceIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            canonical.Append("resource=").Append(resourceId).Append('\n');
        }

        foreach (var backendId in backendIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            canonical.Append("backend=").Append(backendId).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }

    private static bool Reject(string? reason, string subject, List<BoundaryRejection> rejections)
    {
        if (reason is null)
        {
            return false;
        }

        rejections.Add(new BoundaryRejection(subject, reason));
        return true;
    }

    private static string? EnvironmentMismatch(string observed, string applicationEnvironment) =>
        observed.Length > 0 && !string.Equals(observed, applicationEnvironment, StringComparison.OrdinalIgnoreCase)
            ? $"belongs to environment '{observed}', not the selected application environment '{applicationEnvironment}'; environments are never mixed."
            : null;

    private static string? ClaimConflict(string signal) =>
        signal.Length > 0
            ? $"carries an existing DSF signal '{signal}'; a matching name or tag never authorizes adoption, so this needs external reconciliation. Preview writes no Azure tag."
            : null;

    private static string? AuthorityMismatch(
        string tenantId,
        string subscriptionId,
        ApplicationBoundarySelection selection) =>
        !string.Equals(tenantId, selection.TenantId, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(subscriptionId, selection.SubscriptionId, StringComparison.OrdinalIgnoreCase)
            ? $"lives in {tenantId}/{subscriptionId}, outside the selected authority {selection.TenantId}/{selection.SubscriptionId}."
            : null;

    private static IReadOnlyList<BoundaryPlanEntry> Preserved(
        IReadOnlyList<ApplicationResourceCandidate> resources,
        IReadOnlyList<EvidenceBackendCandidate> backends)
    {
        var entries = resources
            .Select(resource => new BoundaryPlanEntry(
                resource.ResourceId,
                $"{resource.Type} in resource group {resource.ResourceGroup}: configuration, diagnostics, tags, networking, delivery, and operations unchanged."))
            .ToList();
        entries.AddRange(backends.Select(backend => new BoundaryPlanEntry(
            backend.ResourceId,
            $"{backend.Kind} backend read-only: no workspace mode, diagnostic setting, retention, or tag change.")));
        entries.Add(new BoundaryPlanEntry(
            "product repository",
            "content, settings, protections, workflows, and existing labels unchanged by this preview."));
        return entries;
    }

    private static IReadOnlyList<BoundaryPlanEntry> NewFactoryResources(ApplicationBoundarySelection selection)
    {
        var suffix = $"{selection.Product}-{selection.FactoryEnvironment}";
        return
        [
            new BoundaryPlanEntry($"rg-dsf-{suffix}", "proposed factory resource group, separate from every application group."),
            new BoundaryPlanEntry($"cae-dsf-{suffix}", "proposed Container Apps environment hosting the council worker and source workloads."),
            new BoundaryPlanEntry($"appcs-dsf-{suffix}", "proposed product App Configuration holding the factory manifest."),
            new BoundaryPlanEntry($"kv-dsf-{suffix}", "proposed product Key Vault for the factory's own secrets."),
            new BoundaryPlanEntry($"cosmos-dsf-{suffix}", "proposed Cosmos account for run state and audit records."),
            new BoundaryPlanEntry($"appi-dsf-{suffix}", "proposed factory-only telemetry; never an application evidence backend."),
        ];
    }

    private static IReadOnlyList<BoundaryPlanEntry> Reused(ApplicationBoundarySelection selection)
    {
        var entries = new List<BoundaryPlanEntry>();
        if (!string.IsNullOrWhiteSpace(selection.OwnerAppConfigEndpoint))
        {
            entries.Add(new BoundaryPlanEntry(
                selection.OwnerAppConfigEndpoint,
                "existing owner App Configuration reused for reservations and the claim registry; not recreated."));
        }

        if (!string.IsNullOrWhiteSpace(selection.OwnerKeyVaultUri))
        {
            entries.Add(new BoundaryPlanEntry(
                selection.OwnerKeyVaultUri,
                "existing owner Key Vault reused for the DSF GitHub App private key; not recreated or rotated."));
        }

        entries.Add(new BoundaryPlanEntry(
            "model prerequisites",
            "synthesis, embedding, and three distinct-family juror deployments are existing owner-approved prerequisites; owner-approved model accounts are neither application resources nor evidence backends."));
        return entries;
    }

    private static IReadOnlyList<BoundaryPlanEntry> Grants(
        IReadOnlyList<ApplicationResourceCandidate> resources,
        IReadOnlyList<EvidenceBackendCandidate> backends)
    {
        var entries = resources
            .Select(resource => new BoundaryPlanEntry(
                resource.ResourceId,
                "proposed role 'Reader' at this exact resource scope for the planned source workload identity."))
            .ToList();
        entries.AddRange(backends.Select(backend => new BoundaryPlanEntry(
            backend.ResourceId,
            "proposed role 'Log Analytics Reader' at this exact backend scope for the planned source workload identity.")));
        entries.Add(new BoundaryPlanEntry(
            "planned identities",
            $"{string.Join(", ", PlannedFactoryIdentities)} do not exist yet and have no principal ID; the actual binding must be checked before any grant write. Discovery or deployment rights are neither delegation nor effective workload or network access."));
        return entries;
    }

    private static IReadOnlyList<BoundaryPlanEntry> Claims(
        ApplicationBoundarySelection selection,
        IReadOnlyList<ApplicationResourceCandidate> resources,
        IReadOnlyList<EvidenceBackendCandidate> backends)
    {
        var count = (resources.Count + backends.Count).ToString(CultureInfo.InvariantCulture);
        return
        [
            new BoundaryPlanEntry(
                $"owner registry claim for {selection.Product}/{selection.ApplicationEnvironment}",
                $"would record {count} owner-scoped observation claim(s) in owner App Configuration when applied. Owner-scoped reservation is not a global lock across separate owner authorities."),
            new BoundaryPlanEntry(
                "Azure tags",
                "no tag is written, repaired, released, or migrated; existing tags are read only as conflict signals."),
        ];
    }

    private static IReadOnlyList<BoundaryPlanEntry> RepositoryAdditions() =>
    [
        new BoundaryPlanEntry(
            "labels",
            "only `dsf:proposal`, `dsf-outcome:approved`, `dsf-outcome:rejected`, and `dsf-outcome:changes-requested` may be created or reused; existing label identity, color, and description are preserved."),
        new BoundaryPlanEntry(
            "not permitted",
            "no repository secrets, variables, workflows, webhooks, rulesets, protections, settings repair, assignment, or delivery trigger."),
    ];

    private static IReadOnlyList<BoundaryPlanEntry> CostsAndOmissions() =>
    [
        new BoundaryPlanEntry(
            "costs",
            "the proposed factory resources bill to the selected subscription once applied; application resources keep their current cost."),
        new BoundaryPlanEntry(
            "omitted here",
            "evidence readiness, charter adoption, preview deliberation, and activation approval are separate later steps; this preview neither proves nor grants them."),
    ];

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string>? values) =>
        (values ?? [])
        .Select(value => value?.Trim() ?? string.Empty)
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
