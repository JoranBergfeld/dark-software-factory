using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal static class ApplicationBoundaryPolicy
{
    public static IReadOnlyList<BoundaryPlanFinding> Review(ApplicationBoundarySelection selection, AzureBoundaryDiscovery discovery)
    {
        var findings = new List<BoundaryPlanFinding>();
        var members = selection.ApplicationResourceIds.Concat(selection.EvidenceBackends.Select(b => b.ResourceId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var model in discovery.Resources.Where(r => selection.ModelResourceIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)))
        {
            if (model.Type != "microsoft.cognitiveservices/accounts")
                Block("model-prerequisite", $"{model.Id} is not a model-serving account; shared application/evidence resources cannot be reclassified as model prerequisites.");
        }
        foreach (var resource in discovery.Resources.Where(r => members.Contains(r.Id)))
        {
            if (resource.Signals.GetValueOrDefault("shared") is "true" or "yes"
                || resource.Signals.GetValueOrDefault("dedicated") is "false" or "no")
                Block("dedication-conflict", $"{resource.Id} has a known shared/dedication contradiction; declarations cannot override it.");
            foreach (var env in resource.Signals.Where(t => t.Key is "env" or "environment"))
            {
                if (EnvironmentName(env.Value) != EnvironmentName(selection.ApplicationEnvironment))
                    Block("environment-conflict", $"{resource.Id} has a conflicting application environment signal.");
            }
            if (resource.Signals.Any(t => t.Key.StartsWith("dsf", StringComparison.Ordinal))
                || resource.Signals.GetValueOrDefault("managed-by") == "dsf"
                || resource.Signals.GetValueOrDefault("project") == "dark-software-factory")
                Block("existing-dsf-signal", $"{resource.Id} has existing DSF/claim/factory signals. Matching names/tags, including partial tag pairs, never authorize adoption.");
            if (resource.Type is "microsoft.web/serverfarms" or "microsoft.app/managedenvironments"
                or "microsoft.compute/hostgroups" or "microsoft.compute/hostgroups/hosts")
                Block("shared-host", $"{resource.Id} is a host boundary, not an individually selected application resource; shared hosts are unsupported.");
            if (resource.Type == "microsoft.insights/components")
            {
                if (resource.BackingWorkspaceResourceId is null)
                    findings.Add(AzureBoundaryReader.Unavailable("backing-workspace", $"{resource.Id} has no readable conventional workspace route."));
                else if (!selection.EvidenceBackends.Any(b => b.ResourceId.Equals(resource.BackingWorkspaceResourceId, StringComparison.OrdinalIgnoreCase)))
                    Block("backing-workspace", $"{resource.Id} uses backing workspace {resource.BackingWorkspaceResourceId}, which was not separately selected. It was not added.");
            }
        }

        foreach (var backend in selection.EvidenceBackends)
        {
            var resource = discovery.Resources.FirstOrDefault(r => r.Id.Equals(backend.ResourceId, StringComparison.OrdinalIgnoreCase));
            if (resource is null) continue;
            if (resource.Type != "microsoft.operationalinsights/workspaces")
            {
                Block("evidence-route", $"{resource.Id}: only explicit dedicated Log Analytics workspace routes are supported; component metadata is not dedicated storage.");
                continue;
            }
            if (!Guid.TryParse(resource.WorkspaceId, out _))
                findings.Add(AzureBoundaryReader.Unavailable("workspace-identity", $"{resource.Id} has no valid workspace data-plane identity."));
            foreach (var table in backend.Tables)
            {
                var observations = discovery.EvidenceTables.Where(t =>
                    t.BackendResourceId == resource.Id && t.Name.Equals(table, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (observations.Length != 1)
                    findings.Add(AzureBoundaryReader.Unavailable("evidence-table", $"{resource.Id}: selected table {table} was not observed uniquely; no diagnostics or tables were created."));
                else if (observations[0].Plan != "Analytics" || !SupportedTables.Contains(observations[0].Name))
                    Block("evidence-table", $"{resource.Id}: selected table {table} is not a supported Analytics table. No query/table/diagnostic repair is offered.");
            }
            foreach (var component in discovery.InspectedTopology.Where(c =>
                         c.BackingWorkspaceResourceId?.Equals(resource.Id, StringComparison.OrdinalIgnoreCase) == true
                         && !selection.ApplicationResourceIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase)))
                Block("shared-workspace", $"{resource.Id} also backs unselected component {component.Id}. Component-specific metadata or query filtering cannot establish dedicated shared storage.");
        }
        return findings;

        void Block(string check, string detail) => findings.Add(new(check, BoundaryPlanState.Blocked, detail,
            "application / evidence administrator", "Select a dedicated same-environment boundary or reconcile outside DSF; no automatic repair or adoption."));
    }

    internal static string EnvironmentName(string value) => value.Trim().ToLowerInvariant() switch
    {
        "prod" => "production",
        "stage" or "stg" => "staging",
        "dev" => "development",
        var name => name,
    };

    internal static readonly IReadOnlySet<string> SupportedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AppRequests", "AppExceptions", "AppTraces", "AppDependencies", "AppEvents",
        "AzureDiagnostics", "AzureActivity", "ContainerLogV2", "InsightsMetrics", "Perf", "Heartbeat",
    };
}
