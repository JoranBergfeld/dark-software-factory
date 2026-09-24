using System.CommandLine;
using System.Globalization;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

/// <summary>
/// `dsf onboard decide preview`: combines the operator's Azure CLI identity with an
/// explicitly selected application environment and prints an exact, categorized
/// onboarding plan. The command is read-only: it creates no reservation, factory
/// resource, grant, secret copy, label, issue, or pull request.
/// </summary>
internal static class OnboardCommand
{
    private const int Success = 0;
    private const int Failure = 1;

    internal static Command Build(
        ICliTerminal terminal,
        IAzureApplicationDiscoveryClient discovery,
        Func<string?, string, string?> resolveConfiguredValue)
    {
        var product = new Option<string>("--product") { Description = "owner-scoped product key for the attached council" };
        product.Required = true;
        var tenant = new Option<string>("--tenant") { Description = "Azure tenant id (defaults to the Azure CLI identity)" };
        var subscription = new Option<string>("--subscription")
        {
            Description = "Azure subscription id (defaults to the Azure CLI identity)",
        };
        var applicationEnvironment = new Option<string>("--app-environment")
        {
            Description = "named application environment to attach to (e.g. 'production')",
        };
        var applicationResource = new Option<string[]>("--application-resource")
        {
            Description = "resource id explicitly inside the application boundary (repeatable)",
            AllowMultipleArgumentsPerToken = false,
        };
        var evidenceBackend = new Option<string[]>("--evidence-backend")
        {
            Description = "dedicated evidence backend resource id, selected separately (repeatable)",
            AllowMultipleArgumentsPerToken = false,
        };
        var dedicated = new Option<bool>("--dedicated")
        {
            Description = "declare this exact selection dedicated to the application",
        };
        var crossOwnerReviewed = new Option<bool>("--cross-owner-reviewed")
        {
            Description = "confirm cross-owner exclusivity was reviewed for this exact selection",
        };
        var factoryEnvironment = new Option<string>("--factory-environment")
        {
            Description = "factory environment moniker, distinct from the application environment",
        };
        factoryEnvironment.DefaultValueFactory = _ => "dev";
        var ownerAppConfigEndpoint = new Option<string>("--owner-appconfig-endpoint")
        {
            Description = "owner App Configuration endpoint",
        };
        var ownerKeyVaultUri = new Option<string>("--owner-keyvault-uri") { Description = "owner Key Vault URI" };
        var expectFingerprint = new Option<string>("--expect-fingerprint")
        {
            Description = "reject the run unless the plan fingerprint matches this reviewed value",
        };

        var preview = new Command(
            "preview",
            "preview the dedicated application boundary for an existing app (read-only)");
        foreach (var option in new Option[]
                 {
                     product, tenant, subscription, applicationEnvironment, applicationResource, evidenceBackend,
                     dedicated, crossOwnerReviewed, factoryEnvironment, ownerAppConfigEndpoint, ownerKeyVaultUri,
                     expectFingerprint,
                 })
        {
            preview.Options.Add(option);
        }

        preview.SetAction(async (parseResult, cancellationToken) =>
        {
            var inputs = new PreviewInputs(
                parseResult.GetValue(product) ?? string.Empty,
                parseResult.GetValue(tenant) ?? string.Empty,
                parseResult.GetValue(subscription) ?? string.Empty,
                parseResult.GetValue(applicationEnvironment) ?? string.Empty,
                parseResult.GetValue(applicationResource) ?? [],
                parseResult.GetValue(evidenceBackend) ?? [],
                parseResult.GetValue(dedicated),
                parseResult.GetValue(crossOwnerReviewed),
                parseResult.GetValue(factoryEnvironment) ?? "dev",
                resolveConfiguredValue(
                    parseResult.GetValue(ownerAppConfigEndpoint), "DSF_OWNER_APPCONFIG_ENDPOINT") ?? string.Empty,
                resolveConfiguredValue(
                    parseResult.GetValue(ownerKeyVaultUri), "DSF_OWNER_KEYVAULT_URI") ?? string.Empty,
                parseResult.GetValue(expectFingerprint) ?? string.Empty);

            return await RunPreviewAsync(terminal, discovery, inputs, cancellationToken);
        });

        var decide = new Command("decide", "attach a proposal-only Feature Council to an existing application");
        decide.Subcommands.Add(preview);

        var onboard = new Command("onboard", "onboard an existing application into a DSF phase");
        onboard.Subcommands.Add(decide);
        return onboard;
    }

    private sealed record PreviewInputs(
        string Product,
        string TenantId,
        string SubscriptionId,
        string ApplicationEnvironment,
        IReadOnlyList<string> ApplicationResourceIds,
        IReadOnlyList<string> EvidenceBackendIds,
        bool Dedicated,
        bool CrossOwnerReviewed,
        string FactoryEnvironment,
        string OwnerAppConfigEndpoint,
        string OwnerKeyVaultUri,
        string ExpectedFingerprint);

    private static async Task<int> RunPreviewAsync(
        ICliTerminal terminal,
        IAzureApplicationDiscoveryClient discovery,
        PreviewInputs inputs,
        CancellationToken cancellationToken)
    {
        AzureIdentityContext? identity;
        try
        {
            identity = await discovery.GetSignedInContextAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        if (identity is null)
        {
            terminal.WriteErrorLine(
                "[dsf] error: no Azure CLI identity is available. Run `az login` and select one tenant/subscription; onboarding never invents an authority.");
            return Failure;
        }

        var tenantId = Coalesce(inputs.TenantId, identity.TenantId);
        var subscriptionId = Coalesce(inputs.SubscriptionId, identity.SubscriptionId);
        terminal.WriteLine(
            $"[dsf] azure identity: user={Describe(identity.User)} tenant={tenantId} subscription={subscriptionId} ({Describe(identity.SubscriptionName)})");

        var interactive = terminal.Capabilities.IsInteractive;
        var applicationEnvironment = inputs.ApplicationEnvironment;
        var resourceIds = inputs.ApplicationResourceIds.ToList();
        var backendIds = inputs.EvidenceBackendIds.ToList();
        var dedicated = inputs.Dedicated;
        var crossOwnerReviewed = inputs.CrossOwnerReviewed;

        AzureApplicationInventory inventory;
        try
        {
            inventory = await discovery.DiscoverAsync(tenantId, subscriptionId, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        if (applicationEnvironment.Length == 0 || resourceIds.Count == 0 || backendIds.Count == 0)
        {
            if (!interactive)
            {
                terminal.WriteErrorLine(
                    "[dsf] error: explicit input is incomplete. Provide --app-environment, at least one --application-resource, and at least one --evidence-backend. Nothing is inferred, and a generic --yes flag is not selection authority.");
                return Failure;
            }

            if (applicationEnvironment.Length == 0)
            {
                applicationEnvironment = (terminal.Prompt("Application environment: ") ?? string.Empty).Trim();
                if (applicationEnvironment.Length == 0)
                {
                    terminal.WriteErrorLine("[dsf] error: an application environment is required.");
                    return Failure;
                }
            }

            if (resourceIds.Count == 0)
            {
                var selectedResourceIds = SelectIds(
                    terminal,
                    "application resources",
                    inventory.Resources.Select(resource => (
                        resource.ResourceId,
                        $"{resource.Name} ({resource.Type}) rg={resource.ResourceGroup} environment={Describe(resource.Environment)}")).ToList(),
                    "Application resource numbers (comma-separated): ");
                if (selectedResourceIds is null)
                {
                    return Failure;
                }

                if (selectedResourceIds.Count == 0)
                {
                    terminal.WriteErrorLine(
                        "[dsf] error: no application resource was selected; group siblings, parents, children, and dependencies are never added implicitly.");
                    return Failure;
                }

                resourceIds = selectedResourceIds;
            }

            if (backendIds.Count == 0)
            {
                var selectedBackendIds = SelectIds(
                    terminal,
                    "evidence backends",
                    inventory.EvidenceBackends.Select(backend => (
                        backend.ResourceId,
                        $"{backend.Name} ({backend.Kind}) environment={Describe(backend.Environment)}")).ToList(),
                    "Evidence backend numbers (comma-separated): ");
                if (selectedBackendIds is null)
                {
                    return Failure;
                }

                if (selectedBackendIds.Count == 0)
                {
                    terminal.WriteErrorLine(
                        "[dsf] error: no evidence backend was selected; application membership never authorizes a telemetry backend.");
                    return Failure;
                }

                backendIds = selectedBackendIds;
            }

            dedicated = dedicated || Confirm(
                terminal,
                "Declare this exact selection dedicated to the application? [y/N]: ");
            crossOwnerReviewed = crossOwnerReviewed || Confirm(
                terminal,
                "Cross-owner exclusivity reviewed for this exact selection? [y/N]: ");
        }

        var selection = new ApplicationBoundarySelection
        {
            Product = inputs.Product,
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            ApplicationEnvironment = applicationEnvironment,
            ApplicationResourceIds = resourceIds,
            EvidenceBackendIds = backendIds,
            DedicationDeclared = dedicated,
            CrossOwnerExclusivityReviewed = crossOwnerReviewed,
            OwnerAppConfigEndpoint = inputs.OwnerAppConfigEndpoint,
            OwnerKeyVaultUri = inputs.OwnerKeyVaultUri,
            FactoryEnvironment = inputs.FactoryEnvironment,
        };

        var plan = ApplicationBoundaryPlanner.Plan(selection, inventory);

        if (interactive)
        {
            terminal.WriteLine(CliPresentation.EquivalentCommand(terminal.Capabilities, Replay(plan.Selection)));
        }

        if (inputs.ExpectedFingerprint.Length > 0
            && !string.Equals(inputs.ExpectedFingerprint, plan.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            terminal.WriteErrorLine(
                $"[dsf] error: the reviewed plan {inputs.ExpectedFingerprint} is stale; this selection fingerprints as {plan.Fingerprint}. Review the new plan instead of widening the old approval.");
            return Failure;
        }

        Render(terminal, plan);
        return plan.Outcome == BoundaryPlanOutcome.Reviewable ? Success : Failure;
    }

    private static void Render(ICliTerminal terminal, ApplicationBoundaryPlan plan)
    {
        var selection = plan.Selection;
        terminal.WriteLine(
            $"[dsf] onboarding boundary preview for product={selection.Product} application-environment={selection.ApplicationEnvironment} factory-environment={selection.FactoryEnvironment} (READ-ONLY)");
        terminal.WriteLine($"[dsf] plan fingerprint: {plan.Fingerprint}");
        terminal.WriteLine(
            $"[dsf] outcome: {(plan.Outcome == BoundaryPlanOutcome.Reviewable ? "REVIEWABLE" : "BLOCKED")}");

        Section(terminal, "preserved application/repository assets", plan.PreservedAssets);
        Section(terminal, "newly owned factory resources (proposed)", plan.NewFactoryResources);
        Section(terminal, "reused owner/model prerequisites", plan.ReusedPrerequisites);
        Section(terminal, "proposed grant scopes and factory identities", plan.ProposedGrants);
        Section(terminal, "owner registry claims", plan.RegistryClaims);
        Section(terminal, "allowed repository additions", plan.RepositoryAdditions);
        Section(terminal, "costs and omissions", plan.CostsAndOmissions);
        Section(terminal, "blocked administrator prerequisites", plan.AdministratorPrerequisites);

        if (plan.Rejections.Count > 0)
        {
            terminal.WriteLine("[dsf] rejected selections:");
            foreach (var rejection in plan.Rejections)
            {
                terminal.WriteLine($"[dsf]   - {rejection.Subject}: {rejection.Reason}");
            }
        }

        terminal.WriteLine(
            "[dsf] no side effects: this preview created no reservation, factory resource, role assignment, secret copy, label, issue, or pull request.");
        terminal.WriteLine(
            "[dsf] next actor: the operator reviews this exact plan; applying it is a separate, explicitly approved command.");
    }

    private static void Section(
        ICliTerminal terminal,
        string title,
        IReadOnlyList<BoundaryPlanEntry> entries)
    {
        terminal.WriteLine($"[dsf] {title}: {entries.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (var entry in entries)
        {
            terminal.WriteLine($"[dsf]   - {entry.Subject}: {entry.Detail}");
        }
    }

    /// <summary>Returns the selected ids, or <c>null</c> when the answer named something unlisted.</summary>
    private static List<string>? SelectIds(
        ICliTerminal terminal,
        string title,
        IReadOnlyList<(string Id, string Label)> candidates,
        string prompt)
    {
        terminal.WriteLine($"[dsf] discovered {title}: {candidates.Count.ToString(CultureInfo.InvariantCulture)}");
        for (var index = 0; index < candidates.Count; index++)
        {
            terminal.WriteLine(
                $"[dsf]   {(index + 1).ToString(CultureInfo.InvariantCulture)}. {candidates[index].Label} [{candidates[index].Id}]");
        }

        var answer = terminal.Prompt(prompt) ?? string.Empty;
        var selected = new List<string>();
        foreach (var token in answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                && number >= 1
                && number <= candidates.Count)
            {
                selected.Add(candidates[number - 1].Id);
                continue;
            }

            terminal.WriteErrorLine($"[dsf] error: '{token}' is not one of the listed {title}.");
            return null;
        }

        return selected;
    }

    private static bool Confirm(ICliTerminal terminal, string prompt)
    {
        var answer = terminal.Prompt(prompt) ?? string.Empty;
        var normalized = answer.Trim();
        return string.Equals(normalized, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string Replay(ApplicationBoundarySelection selection)
    {
        var arguments = new List<string>
        {
            "dsf", "onboard", "decide", "preview",
            "--product", selection.Product,
            "--tenant", selection.TenantId,
            "--subscription", selection.SubscriptionId,
            "--app-environment", selection.ApplicationEnvironment,
        };
        foreach (var resourceId in selection.ApplicationResourceIds)
        {
            arguments.Add("--application-resource");
            arguments.Add(resourceId);
        }

        foreach (var backendId in selection.EvidenceBackendIds)
        {
            arguments.Add("--evidence-backend");
            arguments.Add(backendId);
        }

        if (selection.DedicationDeclared)
        {
            arguments.Add("--dedicated");
        }

        if (selection.CrossOwnerExclusivityReviewed)
        {
            arguments.Add("--cross-owner-reviewed");
        }

        arguments.Add("--factory-environment");
        arguments.Add(selection.FactoryEnvironment);
        return string.Join(' ', arguments);
    }

    private static string Coalesce(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Describe(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
