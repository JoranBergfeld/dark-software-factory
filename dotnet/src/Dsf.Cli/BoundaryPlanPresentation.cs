using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal static class BoundaryPlanPresentation
{
    public static void Write(ICliTerminal terminal, ApplicationBoundaryPlan plan, JsonSerializerOptions json)
    {
        terminal.WriteLine($"[dsf] Application boundary plan: {plan.State}. Not onboarded; no remote changes. Not readiness or activation.");
        if (plan.Categories is not { } categories) return;
        Section("Preserved assets", categories.PreservedAssets);
        Section("New factory resources", categories.NewFactoryResources);
        Section("Reused prerequisites", categories.ReusedPrerequisites);
        Section("Proposed grants (symbolic identities; binding recheck required)", categories.ProposedGrants);
        Section("Registry claims (proposed only; no tags)", categories.RegistryClaims);
        Section("Allowed repository additions (separate later approvals)", categories.AllowedRepositoryAdditions);
        Section("Costs and omissions", categories.CostsAndOmissions);
        Section("Blocked administrator prerequisites for later operations", categories.BlockedAdministratorPrerequisites);
        Section("Current findings", plan.Findings);
        terminal.WriteLine("[dsf] Selection fingerprint: " + plan.SelectionFingerprint);
        terminal.WriteLine("[dsf] Plan fingerprint: " + plan.Fingerprint);

        void Section<T>(string title, IReadOnlyList<T> values)
        {
            terminal.WriteLine("[dsf] " + title + ":");
            foreach (var value in values)
                terminal.WriteLine(JsonSerializer.Serialize(value, json));
        }
    }
}
