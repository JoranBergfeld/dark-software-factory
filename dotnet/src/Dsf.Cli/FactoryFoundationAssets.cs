using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed record FoundationResourceTarget(string Id, string ApiVersion, string? LogicalIdentity = null, string? ManagedBy = null);

internal sealed class FactoryFoundationAssets(FactoryReservationIdentity identity, ApplicationBoundarySelection selection)
{
    public string Group { get; } = $"/subscriptions/{identity.SubscriptionId}/resourcegroups/{selection.FactoryResourceGroup.ToLowerInvariant()}";
    public string Prefix { get; } = "df-" + identity.FactoryId.ToString("N")[..14];
    public string HostingPlatformGroup => $"/subscriptions/{identity.SubscriptionId}/resourcegroups/{Prefix}-hosting-infra";
    public string HostingEnvironment => Group + "/providers/microsoft.app/managedenvironments/" + Prefix + "-hosting";

    public string Deployment(Guid operationId) => Group + "/providers/microsoft.resources/deployments/df-" + operationId.ToString("N");

    public string Request()
    {
        using var template = JsonDocument.Parse(File.ReadAllText(new ProvisioningAssets().Resolve("onboard-foundation.json")));
        return JsonSerializer.Serialize(new
        {
            properties = new
            {
                mode = "Incremental",
                template = template.RootElement,
                parameters = new
                {
                    namePrefix = new { value = Prefix },
                    location = new { value = selection.FactoryLocation },
                    appConfigName = new { value = "dsfo-" + identity.FactoryId.ToString("N") },
                    tenantId = new { value = identity.TenantId },
                    productKey = new { value = selection.ProductKey },
                },
            },
        });
    }

    public IReadOnlyList<FoundationResourceTarget> Targets()
    {
        List<FoundationResourceTarget> result = [];
        Add("microsoft.keyvault/vaults", Prefix + "-vault", "2024-11-01");
        Add("microsoft.documentdb/databaseaccounts", Prefix + "-cosmos", "2024-11-15");
        var database = Prefix + "-cosmos/sqldatabases/" + selection.ProductKey;
        Add("microsoft.documentdb/databaseaccounts", database, "2024-11-15");
        foreach (var container in new[] { "working", "records", "lessons", "charters", "runs", "learning" })
            Add("microsoft.documentdb/databaseaccounts", database + "/containers/" + container, "2024-11-15");
        Add("microsoft.operationalinsights/workspaces", Prefix + "-logs", "2025-07-01");
        Add("microsoft.insights/components", Prefix + "-insights", "2020-02-02");
        foreach (var (name, logical) in new[] { ("council", "factory:council"), ("source", "factory:source-azuremonitor"),
                     ("code", "factory:code-intent"), ("github", "factory:github-auth") })
            Add("microsoft.managedidentity/userassignedidentities", Prefix + "-" + name, "2024-11-30", logical);
        Add("microsoft.network/networksecuritygroups", Prefix + "-nsg", "2024-05-01");
        Add("microsoft.network/virtualnetworks", Prefix + "-network", "2024-05-01");
        foreach (var (suffix, zone) in new[] { ("vault", "privatelink.vaultcore.azure.net"),
                     ("cosmos", "privatelink.documents.azure.com"), ("config", "privatelink.azconfig.io") })
        {
            Add("microsoft.network/privatednszones", zone, "2024-06-01");
            Add("microsoft.network/privatednszones", zone + "/virtualnetworklinks/" + Prefix, "2024-06-01");
            Add("microsoft.network/privateendpoints", Prefix + "-" + suffix + "-private", "2024-05-01");
            Add("microsoft.network/privateendpoints", Prefix + "-" + suffix + "-private/privatednszonegroups/default", "2024-05-01");
        }
        Add("microsoft.app/managedenvironments", Prefix + "-hosting", "2025-01-01");
        result.Add(new(HostingPlatformGroup, "2021-04-01", ManagedBy: HostingEnvironment));
        return result;

        void Add(string type, string name, string version, string? logical = null) =>
            result.Add(new(Group + "/providers/" + type + "/" + name, version, logical));
    }
}
