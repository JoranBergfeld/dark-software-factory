using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed class BoundaryPlanningCanceledException : Exception;

internal sealed class BoundaryPlanInteraction(ICliTerminal terminal, AzureBoundaryReader azure)
{
    public async Task<ApplicationBoundarySelection> SelectAsync(CancellationToken cancellationToken)
    {
        var repository = Ask("Existing repository (OWNER/REPO): ");
        var endpoint = Ask("Prepared owner App Configuration endpoint: ");
        var vault = Ask("Prepared owner Key Vault URI: ");
        var product = Ask("Owner-scoped product key (not factory ID): ");
        var subscriptions = await azure.SubscriptionsAsync(cancellationToken);
        for (var index = 0; index < subscriptions.Count; index++)
            terminal.WriteLine($"[dsf] {index + 1}: tenant {subscriptions[index].TenantId}; subscription {subscriptions[index].SubscriptionId}");
        var accountNumber = Ask("Choose ONE tenant/subscription row number: ");
        if (!int.TryParse(accountNumber, out var selected) || selected < 1 || selected > subscriptions.Count)
            throw new BoundaryInputException("Choose an explicitly listed tenant/subscription row; no default account is inferred.");
        var account = subscriptions[selected - 1];
        var environment = Ask("Named application environment (not factory environment): ");
        var inventory = await azure.InventoryAsync(account.SubscriptionId, cancellationToken);
        foreach (var resource in inventory.OrderBy(r => r.Id, StringComparer.Ordinal))
            terminal.WriteLine("[dsf] Discovered resource (not selected): " + resource.Id);
        var apps = Ids("Individually selected application ARM IDs, comma-separated (no groups or implicit dependencies): ");
        var backendIds = Ids("Separately selected dedicated Log Analytics workspace ARM IDs, comma-separated: ");
        var backends = backendIds.Select(id => new EvidenceBackendSelection
        {
            ResourceId = id,
            Tables = Ids($"Explicit Analytics tables for {id}, comma-separated (no KQL): "),
        }).ToArray();
        var group = Ask("NEW isolated factory resource group name: ");
        var location = Ask("Factory Azure location: ");
        var models = Ids("Explicit reused model-serving account ARM IDs, comma-separated (blank = none; model prerequisites remain blocked): ");
        var automationOwner = Ask("Accountable external automation owner/team: ");
        var declarations = Enum.GetValues<RepositoryMutationClass>().ToDictionary(m => m, m =>
            Ask($"{m} external automation declaration (unknown/clear/conflict): ").ToLowerInvariant() switch
            {
                "unknown" => RepositoryAutomationDeclaration.Unknown,
                "clear" => RepositoryAutomationDeclaration.Clear,
                "conflict" => RepositoryAutomationDeclaration.Conflict,
                _ => throw new BoundaryInputException("Every automation declaration must explicitly be unknown, clear or conflict."),
            });
        return new()
        {
            SchemaVersion = 1, Repository = repository, OwnerAppConfigEndpoint = endpoint, OwnerKeyVaultUri = vault,
            ProductKey = product, TenantId = account.TenantId, SubscriptionId = account.SubscriptionId,
            ApplicationEnvironment = environment, ApplicationResourceIds = apps, EvidenceBackends = backends,
            FactoryResourceGroup = group, FactoryLocation = location, ModelResourceIds = models,
            AutomationOwner = automationOwner, AutomationDeclarations = declarations, BoundaryReview = null,
        };
    }

    public ApplicationBoundarySelection Review(ApplicationBoundaryPlan draft)
    {
        terminal.WriteLine("[dsf] Review CURRENT EXACT selection: " + draft.SelectionFingerprint);
        terminal.WriteLine("[dsf] Owner-scoped reservation is not a global lock. Absent tags are not dedication proof. "
            + "Review indirect/shared backing storage and cross-owner attachments; neither is established by names or query filters.");
        var owner = Ask("Accountable application / evidence owner: ");
        var dedication = Declaration("Type dedicated or not-dedicated for ALL selected application resources and separate evidence backends: ",
            "dedicated", "not-dedicated");
        var exclusivity = Declaration("Type exclusive or not-exclusive after CURRENT cross-owner exclusivity review: ",
            "exclusive", "not-exclusive");
        return draft.Selection with
        {
            BoundaryReview = new()
            {
                SelectionFingerprint = draft.SelectionFingerprint, AccountableOwner = owner,
                Dedicated = dedication, CrossOwnerExclusive = exclusivity,
            },
        };
    }

    private bool Declaration(string prompt, string yes, string no) => Ask(prompt).ToLowerInvariant() switch
    {
        var value when value == yes => true,
        var value when value == no => false,
        _ => throw new BoundaryInputException($"Declaration requires '{yes}' or '{no}'; generic yes is not authority."),
    };
    private string[] Ids(string prompt) => Ask(prompt).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private string Ask(string prompt) => terminal.Prompt(prompt)?.Trim() ?? throw new BoundaryPlanningCanceledException();
}
