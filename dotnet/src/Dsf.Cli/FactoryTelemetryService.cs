using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryTelemetryService
{
    internal static FactoryTelemetry Preview(FactoryReservationResult admission, string? image, string? audience, int seconds)
    {
        if (admission.Foundation?.State != FactoryFoundationState.Inactive || admission.Plan?.Azure is null || admission.Identity is null)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Completed provenance-proven inactive foundation required.");
        if (image is null || !Regex.IsMatch(image, "^[a-z0-9.-]+/[a-zA-Z0-9_./-]+@sha256:[0-9a-f]{64}$")
            || !Guid.TryParse(audience, out var client) || client == Guid.Empty)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Image administrator: supply implemented --runtime-image pinned by sha256. Entra administrator: supply existing --auth-audience API client ID; private factory connectivity and operator consent are prerequisites, not created here.");
        var plan = admission.Plan;
        var identity = admission.Identity;
        if (plan.Selection.BoundaryReview is not { Dedicated: true, CrossOwnerExclusive: true }
            || plan.Selection.BoundaryReview.SelectionFingerprint != identity.SelectionFingerprint)
            throw new JsonException();
        var source = Proven(o => o.LogicalIdentity == "factory:source-azuremonitor");
        var hosting = Proven(o => o.Target.EndsWith("-hosting", StringComparison.Ordinal)
            && o.Target.Contains("/microsoft.app/managedenvironments/", StringComparison.Ordinal));
        if (!Guid.TryParse(source.PrincipalId, out var principal) || principal == Guid.Empty
            || !Guid.TryParse(source.ClientId, out var sourceClient) || sourceClient == Guid.Empty
            || admission.Foundation.Operations.Any(o => o != source && o.PrincipalId == source.PrincipalId))
            throw new JsonException();
        var backends = plan.Selection.EvidenceBackends.Select(selected =>
        {
            var workspace = plan.Azure.Resources.Single(r => r.Id.Equals(selected.ResourceId, StringComparison.OrdinalIgnoreCase));
            var components = plan.Azure.Resources.Where(r => r.Type == "microsoft.insights/components"
                && plan.Selection.ApplicationResourceIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)
                && r.BackingWorkspaceResourceId == workspace.Id).Select(r => r.Id).Order(StringComparer.Ordinal).ToArray();
            if (workspace.Type != "microsoft.operationalinsights/workspaces" || !Guid.TryParse(workspace.WorkspaceId, out var id)
                || id == Guid.Empty || components.Length == 0 || selected.Tables.Count == 0
                || selected.Tables.Any(t => !FactoryTelemetryValidation.SupportedTables.Contains(t)))
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Unsupported telemetry attribution/table mode: select workspace-based Application Insights components explicitly in the application boundary and dedicated Analytics AppRequests/AppDependencies/AppExceptions/AppTraces/AppEvents backends. No inferred mapping, scope expansion or diagnostic repair.");
            return new TelemetryBackend(workspace.Id, id.ToString(), selected.Tables, components);
        }).ToArray();
        var configuration = new FactoryTelemetryConfiguration(1, identity.FactoryId, identity.PlanFingerprint,
            identity.ApplicationEnvironment, plan.Selection.ApplicationResourceIds, backends, source, hosting,
            admission.Foundation.ProductEndpoint!, image, client.ToString(), seconds, FactoryTelemetryValidation.QueryRevision)
        { HostTemplateSha256 = FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-telemetry.json"))) };
        configuration = configuration with { AccessGrants = FactoryTelemetryValidation.Grants(configuration, identity.SubscriptionId) };
        var preview = new FactoryTelemetry(1, Guid.Empty, FactoryTelemetryValidation.Hash(configuration), configuration, TelemetryState.Partial, "preview");
        CheckCapacity(preview);
        return preview;

        FoundationOperation Proven(Func<FoundationOperation, bool> predicate)
        {
            var found = admission.Foundation.Operations.Where(predicate).ToArray();
            if (found.Length != 1 || found[0].State != FoundationOperationState.Completed
                || found[0].Disposition != FoundationResourceDisposition.Created || found[0].ResourceId != found[0].Target
                || string.IsNullOrEmpty(found[0].Incarnation) || string.IsNullOrEmpty(found[0].ProviderOperationId))
                throw new ReservationStoreException(FactoryReservationState.Blocked, "Source/hosting creation provenance missing; names and preexisting grants are not ownership proof.");
            return found[0];
        }
    }
}
