using System.Text.Json;
using System.Text;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryReconfigurationService(
    RepositoryAttachmentReviewer? reviewer, IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryReconfigurationReview> ReviewAsync(FactoryReservationResult admission,
        FactoryReconfigurationInputs? desired, string? reviewedFingerprint, CancellationToken token, Guid? originalTransition = null)
    {
        try { return await ReviewCurrentAsync(admission, desired, reviewedFingerprint, token, originalTransition); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException)
        {
            return new(FactoryReconfigurationState.Unknown,
                "Authoritative configuration, native metadata or protected evidence is missing or inconsistent. "
                + "No local-input fallback, current review or application authority.")
            { FactoryId = admission.FactoryId, RepositoryId = admission.RepositoryId };
        }
    }

    private async Task<FactoryReconfigurationReview> ReviewCurrentAsync(FactoryReservationResult admission,
        FactoryReconfigurationInputs? desired, string? reviewedFingerprint, CancellationToken token, Guid? originalTransition)
    {
        if (admission.Identity is not { } identity || admission.Plan is null
            || admission.Foundation is not { State: FactoryFoundationState.Inactive, ProductEndpoint: { } endpoint }
            || admission.State != FactoryReservationState.Reserved)
            return new(Map(admission.State), "Trusted retained factory required. " + admission.Detail);
        var owner = new OwnerReservationStore(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
        var ownerRecords = await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token);
        if (!ownerRecords.TryGetValue(ProductConfigurationKeys.OwnerProductClaim(identity.ProductKey), out var root)
            || root.ETag.ToString() != admission.Revision)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Current owner coordinator changed during review; restart read-only inspection.");
        var proof = new ReservationAdmissionProof();
        proof.Add(root);
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(root.Value);
        var bytes = Encoding.UTF8.GetBytes(BoundaryPlanFingerprint.Canonical(
            JsonSerializer.SerializeToElement(admission.Plan, FactoryReservationRecords.Json)));
        if (anchor.Identity != identity || FactoryReservationRecords.Hash(bytes) != anchor.PlanSha256) throw new JsonException();
        foreach (var step in FactoryReservationRecords.Steps(anchor, bytes, admission.Plan))
        {
            if (!ownerRecords.TryGetValue(step.Key, out var record) || record.Value != step.Value)
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Original admitted claim/history differs; a matching factory marker never authorizes adoption.");
            proof.Add(record);
        }
        var graph = await new FactoryAuditService(azure, http, clock).ReadAsync(admission, token);
        if (originalTransition is { } transition)
        {
            var retained = graph.Records.Single(r => r.Identity.Store == AuditStoreKind.Governance
                && r.Identity.Key == FactoryReconfigurationOperationRecords.RootKey);
            if (FactoryReconfigurationOperationRecords.Read(retained.Content, identity, clock.GetUtcNow()).OperationId != transition
                || graph.Records.Any(r => r.Identity.Store == AuditStoreKind.Governance
                    && FactoryReconfigurationOperationRecords.IsRecord(r.Identity.Key)
                    && r.Identity.Key != FactoryReconfigurationOperationRecords.RootKey
                    && !r.Identity.Key.StartsWith(FactoryReconfigurationOperationRecords.Prefix + transition.ToString("N") + "/", StringComparison.Ordinal)))
                throw new JsonException();
        }
        var values = graph.Records.Where(r => r.Identity.Store == AuditStoreKind.Governance)
            .ToDictionary(r => r.Identity.Key, r => r.Content, StringComparer.Ordinal);
        var manifest = FactoryGovernance.ReadInactiveManifest(values[ProductConfigurationKeys.FactoryManifest],
            values[ProductConfigurationKeys.FactoryProfile], identity.ProductKey, endpoint, admission.Foundation.ProfilePin);
        if (manifest.Identity != identity) throw new JsonException();
        if (new[] { ProductConfigurationKeys.FactoryTelemetry, ProductConfigurationKeys.FactoryModels,
                ProductConfigurationKeys.FactorySource, ProductConfigurationKeys.FactoryPreview }.Any(k => !values.ContainsKey(k)))
            return new(FactoryReconfigurationState.Partial,
                "Complete retained telemetry, model, source and preview configuration required for this review. "
                + "Finish or reconcile original preparation; missing current inputs are never invented.") { FactoryId = identity.FactoryId };
        var telemetry = FactoryTelemetryValidation.ReadPreparation(manifest, values, requireChecking: false);
        var models = FactoryModelsValidation.ReadPreparation(manifest, values);
        var github = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
        var (source, _) = FactorySourceValidation.Read(manifest, github, values);
        var preview = FactoryReservationRecords.Parse<FactoryPreviewCapability>(values[ProductConfigurationKeys.FactoryPreview]);
        if (preview.OperationId != manifest.PreviewOperationId || preview.ApprovalFingerprint != manifest.PreviewApprovalFingerprint
            || preview.Configuration.Inputs is null) throw new JsonException();
        var supplemental = manifest.SupplementalOperationId is null ? null
            : FactorySupplementalValidation.Read(manifest, values[ProductConfigurationKeys.FactorySupplemental]);
        var current = new FactoryReconfigurationInputs(1, admission.Plan.Selection,
            new(telemetry.Configuration.RuntimeImage, telemetry.Configuration.AudienceClientId, telemetry.Configuration.FreshnessSeconds,
                telemetry.Configuration.QueryRevision, telemetry.Configuration.Queries, telemetry.Configuration.StaleQueries),
            source.Configuration.Selection, new(models.Configuration.RuntimeImage, models.Configuration.AudienceClientId,
                models.Configuration.ValidForSeconds, models.Routes!.Select(r => r.Selection).ToArray()),
            supplemental?.Configuration?.Selection, preview.Configuration.Inputs.Judgment);
        var revisions = ownerRecords.Values.Select(r => new FactoryReconfigurationRevision("owner", r.Key, r.ETag.ToString(), FactoryAudit.TextHash(r.Value)))
            .Concat(graph.Records.Where(r => originalTransition is null || r.Identity.Store != AuditStoreKind.Governance
                    || !FactoryReconfigurationOperationRecords.IsRecord(r.Identity.Key))
                .Select(r => new FactoryReconfigurationRevision(r.Identity.Store.ToString(), r.Identity.Key,
                r.Identity.NativeRevision, r.Identity.Sha256))).OrderBy(r => r.Store, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal).ToArray();
        var offboardingOrDetached = anchor.Offboarding is not null || values.Keys.Any(FactoryOffboarding.IsStopRecord);
        var result = new FactoryReconfigurationReview(FactoryReconfigurationState.Inspected,
            "Authoritative current configuration and protected history, not current readiness or application approval."
            + (!offboardingOrDetached ? "" : " Council offboarding closes admission; retained state remains read-only."))
        {
            FactoryId = identity.FactoryId, RepositoryId = identity.RepositoryId, CurrentInputs = current,
            CurrentRevision = FactoryModelsValidation.Hash(revisions), Revisions = revisions,
            OwnershipHistory = Ownership(admission, graph),
            KeyCustody = new(github.SourceVaultId, github.SourceSecretVersion, github.DestinationVaultId,
                github.DestinationSecretUri, github.AuthPrincipalId, github.OperationId),
            PreservedAssets = admission.Plan.Categories!.PreservedAssets,
            ReusedPrerequisites = admission.Plan.Categories.ReusedPrerequisites,
            DeliberatelyPaused = values.Keys.Any(k => k == FactoryPause.Key || k.StartsWith(FactoryPause.HistoryPrefix, StringComparison.Ordinal)),
            OffboardingOrDetached = offboardingOrDetached,
        };
        if (desired is not null)
        {
            RequireSameIdentity(current, desired, identity);
            desired = desired with { Boundary = BoundarySelectionInput.Normalize(desired.Boundary) };
            var plan = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
                .PlanAsync(desired.Boundary, token, reservationProof: proof);
            if (plan.RepositoryReview.Repository?.Id != identity.RepositoryId)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "NewOnboardingRequired: durable repository ID changed or is unconfirmed; a name cannot retarget the retained factory.");
            RequireGitHubAuthority(plan.RepositoryReview, github, identity);
            desired = desired with { Boundary = plan.Selection };
            var native = await ValidateDesiredAsync(admission, current, desired, plan, models, token);
            var changes = Changes(admission, current, desired, plan, telemetry, supplemental, result.OwnershipHistory);
            var governing = changes.Any(c => c.Kind != FactoryConfigurationChangeKind.Metadata);
            result = result with
            {
                State = Enum.Parse<FactoryReconfigurationState>(plan.State.ToString()), DesiredInputs = desired,
                SelectionFingerprint = plan.SelectionFingerprint, Changes = changes, Findings = plan.Findings,
                RenewedApprovalRequired = governing,
                AffectedEvidence = governing ? graph.Records.Where(r => r.Status == AuditRetentionStatus.Protected)
                    .Select(r => r.Identity).ToArray() : [],
                PreservedAssets = admission.Plan.Categories!.PreservedAssets.Concat(plan.Categories!.PreservedAssets)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                ReusedPrerequisites = plan.Categories.ReusedPrerequisites
                    .Concat(desired.Models.Routes.Select(r => r.AccountResourceId))
                    .Concat(desired.Models.Routes.Where(r => !r.Create).Select(FactoryModelsValidation.DeploymentTarget))
                    .Concat(desired.Supplemental?.Sources.Where(s => s.SearchResourceId is not null).Select(s => s.SearchResourceId!) ?? [])
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Fingerprint = FactoryModelsValidation.Hash(new { identity.FactoryId, result.CurrentRevision, desired,
                    plan.Fingerprint, Native = native, Changes = changes }),
                Detail = "Read-only diff; additions/removals/replacements are not dispatched. Original ownership, key custody, grants and "
                    + "protected evidence remain retained. Selection validation is not effective access, renewed evidence or activation.",
            };
            if (reviewedFingerprint is not null && reviewedFingerprint != result.Fingerprint)
                result = result with { State = FactoryReconfigurationState.Blocked,
                    Detail = "StaleReconfigurationReview: desired inputs, current authority, retained evidence or pause changed; review again. Generic yes cannot accept drift." };
        }
        await RequireUnchangedOwnerAsync(owner, ownerRecords, token);
        if ((await new FactoryAuditService(azure, http, clock).ReadAsync(admission, token)).Revision != graph.Revision)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "ConcurrentReconfigurationReview: protected current authority/evidence changed while reading; no current review released.");
        return result;
    }

    private static FactoryReconfigurationState Map(FactoryReservationState state) => state == FactoryReservationState.Reserved
        ? FactoryReconfigurationState.Blocked : Enum.Parse<FactoryReconfigurationState>(state.ToString());

    private static void RequireGitHubAuthority(RepositoryAttachmentReview review, FactoryGitHubAuthority authority, FactoryReservationIdentity identity)
    {
        if (review.Operator?.Id != identity.GitHubActorId || review.App?.Id != authority.AppId
            || review.Installation is not { SuspendedAt: null } installation || installation.Id != authority.InstallationId
            || installation.AppId != authority.AppId || installation.AccountId != authority.AccountId
            || review.Repository?.OwnerId != authority.AccountId || installation.RepositorySelection != authority.ExpectedInstallationRepositorySelection
            || !Match(review.App.RequestedPermissions, authority.ExpectedAppPermissions)
            || !Match(installation.GrantedPermissions, authority.ExpectedPermissions))
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "GitHubAuthorityAdministratorRequired: current operator, App, installation, account, selected-repository scope or permissions differ. "
                + "Eligible GitHub administrator review is required; reconfiguration never upgrades consent or rewrites original permission authority.");
        static bool Match(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected) =>
            actual.Count == expected.Count && actual.All(p => expected.TryGetValue(p.Key, out var value) && value == p.Value);
    }

    private static void RequireSameIdentity(FactoryReconfigurationInputs current, FactoryReconfigurationInputs desired, FactoryReservationIdentity identity)
    {
        var boundary = desired.Boundary;
        if (desired.SchemaVersion != 1)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Unsupported desired-input schema; complete version-1 selections required.");
        if (boundary.OwnerAppConfigEndpoint.TrimEnd('/') != identity.OwnerEndpoint || boundary.ProductKey != identity.ProductKey
            || boundary.TenantId != identity.TenantId || boundary.SubscriptionId != identity.SubscriptionId
            || ApplicationBoundaryPolicy.EnvironmentName(boundary.ApplicationEnvironment) != identity.ApplicationEnvironment)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "NewOnboardingRequired: owner/product, tenant/subscription or actual application environment differs from retained identity.");
        if (boundary.OwnerKeyVaultUri != current.Boundary.OwnerKeyVaultUri
            || boundary.FactoryResourceGroup != current.Boundary.FactoryResourceGroup || boundary.FactoryLocation != current.Boundary.FactoryLocation)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "AuthorityAdministratorRequired: owner key custody and retained factory infrastructure cannot be retargeted through input reconfiguration.");
    }

    private static IReadOnlyList<FoundationOperation> Ownership(FactoryReservationResult admission, AuditGraph graph) =>
        admission.Foundation!.Operations.Concat(graph.Records.Where(r => r.Identity.Store == AuditStoreKind.Governance
                && new[] { "github", "telemetry", "model", "source", "supplemental", "preview" }
                    .Any(kind => r.Identity.Key.StartsWith("dsf/factory/" + kind + "-operations/", StringComparison.Ordinal)))
            .Select(r => FactoryReservationRecords.Parse<FoundationOperation>(r.Content)))
            .DistinctBy(o => (o.OperationId, o.Index)).OrderBy(o => o.Target, StringComparer.Ordinal)
            .ThenBy(o => o.OperationId).ThenBy(o => o.Index).ToArray();

    private static async Task RequireUnchangedOwnerAsync(OwnerReservationStore owner,
        IReadOnlyDictionary<string, ConfigurationSetting> expected, CancellationToken token)
    {
        var actual = await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token);
        if (actual.Count != expected.Count || expected.Any(p => !actual.TryGetValue(p.Key, out var value)
            || p.Value.ETag != value.ETag || p.Value.Value != value.Value || p.Value.ContentType != value.ContentType))
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "ConcurrentReconfigurationReview: owner claims or original operation changed during inspection; no current review released.");
    }
}
