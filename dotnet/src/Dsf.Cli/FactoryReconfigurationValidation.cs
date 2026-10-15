using System.Net;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryReconfigurationService
{
    private async Task<IReadOnlyDictionary<string, string>> ValidateDesiredAsync(FactoryReservationResult admission,
        FactoryReconfigurationInputs current, FactoryReconfigurationInputs desired, ApplicationBoundaryPlan plan,
        FactoryModels models, CancellationToken token)
    {
        var identity = admission.Identity!;
        try { ValidateSelection(desired, identity); }
        catch (JsonException)
        {
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Complete supported source/telemetry/supplemental selections required; arbitrary queries, missing opt-in or unpinned hosts are not approved.");
        }
        var scopes = desired.Boundary.ApplicationResourceIds.Concat(desired.Boundary.EvidenceBackends.Select(b => b.ResourceId)).ToArray();
        if (desired.Models.Routes.Any(r => scopes.Any(scope => Overlap(scope, r.AccountResourceId)))
            || desired.Supplemental?.Sources.Any(s => s.SearchResourceId is { } search && scopes.Any(scope => Overlap(scope, search))) == true)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "SeparateSourceAuthorityRequired: model-serving and supplemental prerequisites cannot become application or telemetry membership.");
        var arm = new FactoryPreparationArm(azure, http, identity);
        var native = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plan.Azure is not null)
            foreach (var backend in desired.Boundary.EvidenceBackends)
            {
                var workspace = plan.Azure.Resources.SingleOrDefault(r => r.Id == backend.ResourceId);
                if (workspace?.Type != "microsoft.operationalinsights/workspaces" || !Guid.TryParse(workspace.WorkspaceId, out var id) || id == Guid.Empty
                    || !plan.Azure.Resources.Any(r => r.Type == "microsoft.insights/components" && r.BackingWorkspaceResourceId == backend.ResourceId
                        && desired.Boundary.ApplicationResourceIds.Contains(r.Id, StringComparer.Ordinal)))
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "UnsupportedTelemetryAttribution: every selected dedicated backend requires an explicitly selected workspace-based Application Insights component.");
                using var resource = await ReadAsync(backend.ResourceId, FactoryTelemetryValidation.BackendApiVersion);
                if (!FactoryTelemetryValidation.HasSupportedWorkspaceAuthorization(resource.RootElement)
                    || !string.Equals(resource.RootElement.GetProperty("id").GetString(), backend.ResourceId, StringComparison.OrdinalIgnoreCase)
                    || resource.RootElement.GetProperty("properties").GetProperty("customerId").GetString() != workspace.WorkspaceId)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "UnsupportedTelemetryNativeAuthority: current workspace identity/access mode differs; backend administrator must reconcile.");
                foreach (var table in backend.Tables)
                {
                    using var tableResource = await ReadAsync(backend.ResourceId + "/tables/" + table, FactoryTelemetryValidation.BackendApiVersion);
                    if (!FactoryTelemetryValidation.HasSupportedTableAuthorization(tableResource.RootElement))
                        throw new ReservationStoreException(FactoryReservationState.Blocked,
                            "UnsupportedTelemetryTableAuthority: selected tables require current Analytics/General authorization; no fallback.");
                }
            }
        foreach (var route in desired.Models.Routes)
        {
            using var account = await ReadAsync(route.AccountResourceId, FactoryModelsValidation.ProviderApi);
            _ = FactoryModelsValidation.ObserveAccount(route, account.RootElement);
            using var role = await ReadAsync(route.InferenceRoleDefinitionId, "2022-04-01");
            _ = FactoryModelsValidation.ObserveRole(route.InferenceRoleDefinitionId, role.RootElement);
            var target = FactoryModelsValidation.DeploymentTarget(route);
            using var response = await arm.SendAsync(HttpMethod.Get, target, FactoryModelsValidation.ProviderApi, token);
            var original = current.Models.Routes.Single(r => r.Name == route.Name);
            if (route.Create && target != FactoryModelsValidation.DeploymentTarget(original))
            {
                if (response.StatusCode != HttpStatusCode.NotFound)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "ModelTargetNotAbsent: a new owned deployment cannot adopt or replace an existing target.");
                native[target] = "absent";
                continue;
            }
            RequireRead(response, "model deployment");
            using var deployment = await EvidencePayload.ReadAsync(response, token);
            var observation = FactoryModelsValidation.ObserveDeployment(route.Create ? original : route, deployment.RootElement);
            native[target] = FactoryModelsValidation.Hash(deployment.RootElement);
            if (route.Create)
            {
                var receipt = models.Effects?.SingleOrDefault(e => e.Target == target);
                if (receipt is null || receipt.State != FoundationOperationState.Completed || receipt.Disposition != FoundationResourceDisposition.Created
                    || receipt.Incarnation != observation.Incarnation || receipt.NativeRevision != observation.Revision)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "ModelOwnershipUnproven: current deployment differs from original provider receipt; administrator reconciliation required.");
            }
        }
        if (desired.Supplemental is not null)
        {
            var supplemental = await new FactorySupplementalService(azure, http, clock).ReviewChangeAsync(admission, desired.Supplemental, token);
            foreach (var pair in supplemental.Configuration!.PrerequisiteRevisions) native[pair.Key] = pair.Value;
        }
        return native;

        async Task<JsonDocument> ReadAsync(string target, string api)
        {
            using var response = await arm.SendAsync(HttpMethod.Get, target, api, token);
            RequireRead(response, "native prerequisite");
            var document = await EvidencePayload.ReadAsync(response, token);
            var hash = FactoryModelsValidation.Hash(document.RootElement);
            if (native.TryGetValue(target, out var earlier) && earlier != hash)
            {
                document.Dispose();
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "ConcurrentReconfigurationReview: native prerequisite changed during inspection.");
            }
            native[target] = hash;
            return document;
        }
    }

    private static void ValidateSelection(FactoryReconfigurationInputs desired, FactoryReservationIdentity identity)
    {
        FactorySourceValidation.ValidateSelection(desired.Source);
        FactorySourceValidation.ValidateSelection(new(desired.Telemetry.RuntimeImage, desired.Telemetry.AudienceClientId));
        FactoryModelsValidation.ValidateSelection(desired.Models, identity);
        FactoryPreviewInputValidation.Validate(new(FactoryPreviewInputValidation.Policy, desired.Judgment));
        if (desired.Supplemental is not null) FactorySupplementalValidation.ValidateSelection(desired.Supplemental, identity.SubscriptionId);
        var telemetry = desired.Telemetry;
        var tables = desired.Boundary.EvidenceBackends.SelectMany(b => b.Tables).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (telemetry.FreshnessSeconds is <= 0 or > 31536000 || telemetry.QueryRevision != FactoryTelemetryValidation.QueryRevision
            || !telemetry.Queries.Keys.Order(StringComparer.Ordinal).SequenceEqual(tables)
            || !telemetry.StaleQueries.Keys.Order(StringComparer.Ordinal).SequenceEqual(tables)
            || telemetry.Queries.Any(p => !FactoryTelemetryValidation.SupportedTables.Contains(p.Key) || p.Value != FactoryTelemetryValidation.Query(p.Key))
            || telemetry.StaleQueries.Any(p => !FactoryTelemetryValidation.SupportedTables.Contains(p.Key) || p.Value != FactoryTelemetryValidation.StaleQuery(p.Key)))
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "UnsupportedTelemetrySelection: positive bounded freshness and exact supported table-confined queries required; arbitrary KQL is not approved.");
    }

    private static bool Overlap(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase)
        || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static void RequireRead(HttpResponseMessage response, string step)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ReservationStoreException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                ? FactoryReservationState.Blocked : FactoryReservationState.Unknown,
                "Current " + step + " unavailable. Eligible resource/RBAC administrator must reconcile; no substituted configuration or broader permissions.");
    }
}
