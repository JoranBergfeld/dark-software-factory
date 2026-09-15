using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal static class BoundaryPlanCategoriesBuilder
{
    public static BoundaryPlanCategories Build(
        ApplicationBoundarySelection selection, RepositoryAttachmentReview repository, OwnerAdmissionSnapshot admission)
    {
        var group = $"/subscriptions/{selection.SubscriptionId}/resourcegroups/{selection.FactoryResourceGroup.ToLowerInvariant()}";
        var resources = new List<PlannedFactoryResource>
        {
            new("factory:resource-group", "Microsoft.Resources/resourceGroups", "/subscriptions/" + selection.SubscriptionId, group),
            new("factory:app-configuration", "Microsoft.AppConfiguration/configurationStores", group, "Authoritative product manifest and configuration"),
            new("factory:key-vault", "Microsoft.KeyVault/vaults", group, "Factory-only credential custody"),
            new("factory:cosmos", "Microsoft.DocumentDB/databaseAccounts", group, "Factory state, audit and code-intent evidence"),
            new("factory:telemetry", "Microsoft.OperationalInsights/workspaces", group, "Factory-only telemetry; never an application evidence backend"),
            new("factory:application-insights", "Microsoft.Insights/components", group, "Factory-only diagnostics"),
            new("factory:network", "Microsoft.Network/virtualNetworks", group, "Factory-only network additions; no shared network changes"),
            new("factory:hosting", "Microsoft.App/managedEnvironments", group, "Isolated factory hosting, not an application environment"),
            new("factory:council-worker", "Microsoft.App/containerApps", "factory:hosting", "Inert proposal-only council"),
            new("factory:source-worker", "Microsoft.App/containerApps", "factory:hosting", "Azure Monitor source"),
            new("factory:code-intent-worker", "Microsoft.App/containerApps", "factory:hosting", "Read-only code-intent processing"),
            new("factory:models", "Microsoft.CognitiveServices/accounts", group, "Synthesis and embedding deployments; model configuration reviewed separately"),
        };
        string[] identities = ["factory:council", "factory:source-azuremonitor", "factory:code-intent", "factory:github-auth"];
        resources.AddRange(identities.Select(identity =>
            new PlannedFactoryResource(identity, "Microsoft.ManagedIdentity/userAssignedIdentities", group, "Separate workload identity; principal ID not yet created")));
        var grants = new List<PlannedAccessGrant>();
        foreach (var identity in identities)
            Grant(identity, "factory:app-configuration", "App Configuration Data Reader", "516239f1-63e1-4d78-a4de-a74fb236a071");
        Grant("factory:github-auth", "factory:key-vault/secrets/github-app-private-key", "Key Vault Secrets User", "4633458b-17de-408a-b874-0445c86b69e6");
        foreach (var identity in new[] { "factory:council", "factory:code-intent" })
        {
            Grant(identity, "factory:cosmos", "Cosmos DB Built-in Data Contributor", "00000000-0000-0000-0000-000000000002");
            Grant(identity, "factory:models", "Cognitive Services OpenAI User", "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd");
        }
        foreach (var backend in selection.EvidenceBackends)
            Grant("factory:source-azuremonitor", backend.ResourceId.TrimEnd('/').ToLowerInvariant(),
                "Log Analytics Reader", "73c42c96-874c-492b-b04d-ab87d138a893");
        foreach (var model in selection.ModelResourceIds)
            Grant("factory:council", model, "Cognitive Services OpenAI User", "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd");

        var prerequisites = new List<BoundaryPlanFinding>
        {
            new("grant-binding", BoundaryPlanState.Partial,
                "Symbolic factory identities and scopes must be bound to proven created resources and current principal IDs. "
                + "Each proposed grant requires a fresh role/scope/principal/condition and grantor authority check; inspection/deployment rights are not delegation.",
                "Azure grantor", "After identity creation, inspect inherited grants, restrictions and deny/conditional policy before separately approving exact missing grants."),
            new("workload-connectivity", BoundaryPlanState.Unknown,
                "Operator-machine access proves neither effective workload access nor network reachability. No backend query has run.",
                "network / evidence / owner / model administrators",
                "Verify each actual workload identity and endpoint path, including firewall, DNS, private access and AMPLS. Existing application/shared-network changes are outside DSF."),
            new("models", BoundaryPlanState.Partial,
                "Synthesis, embeddings and three distinct-family juror deployments require independent configuration and access review; selecting model accounts is not model readiness.",
                "model administrator", "Provide reviewed Azure-hosted deployments, inference authority, locations and connectivity; shared model accounts are prerequisites, not application/evidence members."),
        };
        prerequisites.AddRange(repository.Findings.Where(f => f.State != RepositoryReviewState.Confirmed)
            .Select(f => new BoundaryPlanFinding("repository:" + f.Check, Map(f.State), f.Detail,
                f.NextActor ?? "repository administrator", f.NextAction ?? "Resolve current repository review before the affected operation.")));
        prerequisites.AddRange(admission.Findings);
        return new(
            [$"repository:{repository.Repository?.Id}:{repository.Repository?.Owner}/{repository.Repository?.Name}",
                .. selection.ApplicationResourceIds, .. selection.EvidenceBackends.Select(b => b.ResourceId)],
            resources,
            [selection.OwnerAppConfigEndpoint, selection.OwnerKeyVaultUri,
                $"GitHub App {repository.App?.Id}; installation {repository.Installation?.Id}; existing owner signing key, no key copy",
                .. selection.ModelResourceIds],
            grants,
            admission.ProposedClaims,
            repository.Automation.Select(review => new RepositoryAdditionPlan(review.Mutation, review.State, review.Mutation switch
            {
                RepositoryMutationClass.ProposalIssue => "After separate activation: create-once unassigned proposal with only dsf:proposal; never edit title/body, assign, close, comment or relabel outcomes.",
                RepositoryMutationClass.CharterPullRequest => "Separately reviewed operator create-once charter-only branch/PR; human merge, no standing factory content-write authority.",
                _ => "Create only absent compatible dsf:proposal, dsf-outcome:approved, dsf-outcome:rejected, dsf-outcome:changes-requested labels; preserve existing IDs/colors/descriptions. No label edits.",
            })).ToArray(),
            [
                $"New factory resources in {selection.FactoryLocation} incur hosting, Cosmos, configuration, vault, telemetry ingestion/retention, networking and model usage costs. No price quote or budget approval is implied.",
                "Owner/model prerequisites remain separately owned; their continuing and inference costs remain. Detachment does not imply deleting retained factory/audit infrastructure.",
                "No SRE/Creation, Control Center, owner broker, application delivery, diagnostics, tags, queries, networking or repository settings changes.",
                "No reservation, resource, grant, secret copy, label, issue or PR is created by preview. No readiness, activation, effective access or global exclusivity is claimed.",
                "Shared App-key custody can mint broader App-authorized tokens; downscoping requested tokens does not eliminate signing authority. Runtime workloads receive no claim-write or delegation rights.",
                "No synthetic evidence, optional web/knowledge sources, telemetry attribution proof, model execution, charter analysis or supervised runtime preview occurs here.",
            ],
            prerequisites);

        void Grant(string identity, string scope, string role, string roleId) =>
            grants.Add(new(identity, null, scope, role, roleId, null, true));
    }

    internal static BoundaryPlanState Map(RepositoryReviewState state) => state switch
    {
        RepositoryReviewState.Blocked or RepositoryReviewState.Denied => BoundaryPlanState.Blocked,
        RepositoryReviewState.Unknown => BoundaryPlanState.Unknown,
        RepositoryReviewState.Pending => BoundaryPlanState.Partial,
        _ => BoundaryPlanState.Reviewed,
    };
}
