using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using Dsf.Core.Charters;
using Dsf.Core.Instances;
using Dsf.Core.Products;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

public static class CliApplication
{
    private const int Success = 0;
    private const int Failure = 1;

    /// <summary>Canonical exit code for a canceled invocation (e.g. Ctrl+C / SIGINT).</summary>
    public const int CanceledExitCode = 130;

    public static async Task<int> InvokeAsync(string[] args, CancellationToken cancellationToken)
        => await InvokeAsync(
            args,
            cancellationToken,
            SystemCliTerminal.Detect(),
            GitHubRestProvisioningClient.FromEnvironment(),
            AzureCliProvisioningClient.FromEnvironment(),
            new AzureCliAppConfigurationClient(new SystemAzureCliRunner()),
            GitHubCharterRepositoryClient.FromEnvironment(),
            CosmosCharterStore.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal)
        => await InvokeAsync(
            args,
            cancellationToken,
            terminal,
            GitHubRestProvisioningClient.FromEnvironment(),
            AzureCliProvisioningClient.FromEnvironment(),
            new AzureCliAppConfigurationClient(new SystemAzureCliRunner()),
            GitHubCharterRepositoryClient.FromEnvironment(),
            CosmosCharterStore.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal,
        IGitHubProvisioningClient github)
        => await InvokeAsync(
            args,
            cancellationToken,
            terminal,
            github,
            AzureCliProvisioningClient.FromEnvironment(),
            new AzureCliAppConfigurationClient(new SystemAzureCliRunner()),
            GitHubCharterRepositoryClient.FromEnvironment(),
            CosmosCharterStore.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal,
        IGitHubProvisioningClient github,
        IAzureProvisioningClient azure)
        => await InvokeAsync(
            args,
            cancellationToken,
            terminal,
            github,
            azure,
            new AzureCliAppConfigurationClient(new SystemAzureCliRunner()),
            GitHubCharterRepositoryClient.FromEnvironment(),
            CosmosCharterStore.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository)
        => await InvokeAsync(
            args,
            cancellationToken,
            terminal,
            appConfig,
            charterRepository,
            CosmosCharterStore.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore)
        => await InvokeAsync(
            args,
            cancellationToken,
            terminal,
            GitHubRestProvisioningClient.FromEnvironment(),
            AzureCliProvisioningClient.FromEnvironment(),
            appConfig,
            charterRepository,
            charterStore);

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal,
        IGitHubProvisioningClient github,
        IAzureProvisioningClient azure,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository)
        => await InvokeAsync(
            args,
            cancellationToken,
            terminal,
            github,
            azure,
            appConfig,
            charterRepository,
            CosmosCharterStore.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal,
        IGitHubProvisioningClient github,
        IAzureProvisioningClient azure,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CanceledExitCode;
        }

        var providedOptions = args
            .Where(arg => arg.StartsWith("--", StringComparison.Ordinal))
            .Select(arg => arg.Split('=', 2)[0])
            .ToHashSet();
        var root = BuildRootCommand(
            terminal, providedOptions, github, azure, appConfig, charterRepository, charterStore);
        var parseResult = root.Parse(args);
        try
        {
            var exitCode = await parseResult.InvokeAsync(cancellationToken: cancellationToken);
            return parseResult.Errors.Count > 0 ? 2 : exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CanceledExitCode;
        }
        catch (Exception exception)
        {
            terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
            return Failure;
        }
    }

    internal static RootCommand BuildRootCommand() => BuildRootCommand(
        SystemCliTerminal.Detect(),
        new HashSet<string>(),
        GitHubRestProvisioningClient.FromEnvironment(),
        AzureCliProvisioningClient.FromEnvironment(),
        new AzureCliAppConfigurationClient(new SystemAzureCliRunner()),
        GitHubCharterRepositoryClient.FromEnvironment(),
        CosmosCharterStore.FromEnvironment());

    private static RootCommand BuildRootCommand(
        ICliTerminal terminal,
        IReadOnlySet<string> providedOptions,
        IGitHubProvisioningClient github,
        IAzureProvisioningClient azure,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore)
    {
        var root = new RootCommand("Dark Software Factory — factory CLI (create product instances)");
        root.Options.Remove(root.Options.Single(option => option.Name == "--version"));
        var helpOption = root.Options.Single(option => option.Name == "--help");
        helpOption.Aliases.Remove("-?");
        helpOption.Aliases.Remove("/?");
        helpOption.Aliases.Remove("/h");

        root.Subcommands.Add(BuildNewCommand(terminal, providedOptions, github, azure, appConfig));
        root.Subcommands.Add(BuildListCommand(terminal, appConfig));
        root.Subcommands.Add(BuildOffboardCommand());
        root.Subcommands.Add(BuildBootstrapCommand());
        root.Subcommands.Add(BuildDeleteCommand("delete"));
        root.Subcommands.Add(BuildDeleteCommand("deprovision"));
        root.Subcommands.Add(BuildRunCommand());
        root.Subcommands.Add(BuildSweepCommand());
        root.Subcommands.Add(BuildServeOrchestratorCommand());
        root.Subcommands.Add(BuildServeAgentCommand());
        root.Subcommands.Add(BuildCharterCommand(terminal, appConfig, charterRepository, charterStore));

        return root;
    }

    private static Command BuildNewCommand(
        ICliTerminal terminal,
        IReadOnlySet<string> providedOptions,
        IGitHubProvisioningClient github,
        IAzureProvisioningClient azure,
        IAppConfigurationClient appConfig)
    {
        var product = StringOption("--product", "product key (e.g. 'microbi')");
        var owner = StringOption("--owner", "GitHub owner/org for the product repo", string.Empty);
        var repo = StringOption("--repo", "repo name (defaults to product key)", string.Empty);
        var visibility = StringOption("--visibility", "product repo visibility", "private", "private", "public", "internal");
        var runtimeTarget = StringOption("--runtime-target", "where the factory runtime is hosted", "aca", "aca");
        var namePrefix = StringOption("--name-prefix", "base Azure resource name prefix", string.Empty);
        var environment = StringOption("--environment", "Azure environment moniker", "dev");
        var location = StringOption("--location", "Azure region", "swedencentral");
        var creationMaturity = StringOption("--creation-maturity", "creation-phase autonomy", "low", "low", "high");
        var dryRun = BoolOption("--dry-run", "preview only: print the what-if plan without running steps");
        var noCharter = BoolOption("--no-charter", "skip the post-provision charter prompt");
        var writePlan = BoolOption("--write-plan", "with --dry-run, still write the instance manifest");
        var configRoot = StringOption("--config-root", "override repo root where config/instances/ is written");
        var ownerKeyVaultUri = StringOption("--owner-keyvault-uri", "owner Key Vault URI", string.Empty);
        var ownerAppConfigEndpoint = StringOption("--owner-appconfig-endpoint", "owner App Configuration endpoint");
        var adminPrincipalId = StringOption("--admin-principal-id", "human owner/governance principal object id", string.Empty);
        var githubAppId = StringOption("--github-app-id", "owner DSF GitHub App id", string.Empty);
        var githubInstallationId = StringOption(
            "--github-installation-id",
            "owner DSF GitHub App installation id",
            string.Empty);

        var command = new Command("new", "create a new isolated product factory instance");
        AddOptions(
            command,
            product,
            owner,
            repo,
            visibility,
            runtimeTarget,
            namePrefix,
            environment,
            location,
            creationMaturity,
            dryRun,
            noCharter,
            writePlan,
            configRoot,
            ownerKeyVaultUri,
            ownerAppConfigEndpoint,
            adminPrincipalId,
            githubAppId,
            githubInstallationId);

        var newOptions = new Option[]
        {
            product,
            owner,
            repo,
            visibility,
            runtimeTarget,
            namePrefix,
            environment,
            location,
            creationMaturity,
            dryRun,
            noCharter,
            writePlan,
            configRoot,
            ownerKeyVaultUri,
            ownerAppConfigEndpoint,
            adminPrincipalId,
            githubAppId,
            githubInstallationId,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!ResolveNewInteraction(
                    parseResult,
                    terminal,
                    product,
                    newOptions,
                    providedOptions,
                    out var interaction))
            {
                return Failure;
            }

            var prefix = parseResult.GetValue(namePrefix) ?? string.Empty;
            if (prefix.Length == 0)
            {
                prefix = interaction.Product ?? parseResult.GetValue(product) ?? string.Empty;
            }
            if (prefix.Length > 0 && !char.IsAsciiLetter(prefix[0]))
            {
                terminal.WriteLine(
                    $"[dsf] error: cannot derive an Azure name prefix from '{prefix}': name prefix base must start with a letter: '{prefix}' Pass --name-prefix explicitly.");
                return Failure;
            }

            var productValue = (interaction.Product ?? parseResult.GetValue(product) ?? string.Empty).Trim();
            var ownerValue = parseResult.GetValue(owner) ?? string.Empty;
            var repoValue = parseResult.GetValue(repo) ?? string.Empty;
            var visibilityValue = parseResult.GetValue(visibility) ?? "private";
            var environmentValue = parseResult.GetValue(environment) ?? "dev";
            var locationValue = parseResult.GetValue(location) ?? "swedencentral";
            var configRootValue = parseResult.GetValue(configRoot);
            var githubAppIdValue = FirstConfiguredValue(
                parseResult.GetValue(githubAppId),
                "DSF_GITHUB_APP_ID");
            var githubInstallationIdValue = FirstConfiguredValue(
                parseResult.GetValue(githubInstallationId),
                "DSF_GITHUB_INSTALLATION_ID");
            if (!ValidateGitHubIdentifier(
                    terminal,
                    "--github-app-id",
                    githubAppIdValue)
                || !ValidateGitHubIdentifier(
                    terminal,
                    "--github-installation-id",
                    githubInstallationIdValue))
            {
                return Failure;
            }
            var effectivePrefix = BuildNamePrefix(prefix.Length > 0 ? prefix : productValue);
            try
            {
                InstanceDefinitions.EnsureSafeProductKey(productValue);
            }
            catch (InstanceDefinitionException exception)
            {
                terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
                return Failure;
            }

            InstanceDefinition definition;
            try
            {
                definition = BuildPlannedDefinition(
                    productValue,
                    ownerValue,
                    repoValue,
                    visibilityValue,
                    parseResult.GetValue(runtimeTarget) ?? "aca",
                    environmentValue,
                    locationValue,
                    parseResult.GetValue(creationMaturity) ?? "low",
                    effectivePrefix,
                    parseResult.GetValue(ownerKeyVaultUri),
                    parseResult.GetValue(ownerAppConfigEndpoint),
                    parseResult.GetValue(adminPrincipalId),
                    githubAppIdValue,
                    githubInstallationIdValue,
                    configRootValue);
            }
            catch (InstanceDefinitionException exception)
            {
                terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
                return Failure;
            }

            if (parseResult.GetValue(dryRun))
            {
                PrintDryRunPlan(
                    terminal,
                    productValue,
                    ownerValue,
                    repoValue,
                    visibilityValue,
                    locationValue,
                    environmentValue,
                    effectivePrefix,
                    parseResult.GetValue(creationMaturity) ?? "low",
                    definition.GitHub.AppId,
                    definition.GitHub.InstallationId,
                    configRootValue);

                if (parseResult.GetValue(writePlan)
                    && !WritePlannedDefinition(
                        terminal,
                        definition,
                        configRootValue))
                {
                    return Failure;
                }
            }
            else
            {
                var ownerEndpoint = FirstConfiguredValue(
                    definition.Azure.OwnerAuthority.AppConfigEndpoint,
                    "DSF_OWNER_APPCONFIG_ENDPOINT");
                if (string.IsNullOrWhiteSpace(ownerEndpoint))
                {
                    terminal.WriteErrorLine(
                        "[dsf] error: DSF_OWNER_APPCONFIG_ENDPOINT or --owner-appconfig-endpoint is required to publish the product index.");
                    return Failure;
                }

                try
                {
                    var githubResult = await GitHubProvisioningPlan.Build(definition)
                        .ExecuteAsync(github, cancellationToken);
                    var afterGitHub = githubResult.ApplyTo(definition);

                    var azureRoot = configRootValue ?? Directory.GetCurrentDirectory();
                    var azureResult = await AzureProvisioningPlan.Build(afterGitHub, azureRoot)
                        .ExecuteAsync(azure, cancellationToken);
                    var updated = azureResult.ApplyTo(afterGitHub) with
                    {
                        Status = afterGitHub.Status with { State = InstanceState.Executed },
                    };

                    var productEndpoint = updated.Azure.Outputs.GetValueOrDefault("appConfigEndpoint");
                    if (string.IsNullOrWhiteSpace(productEndpoint))
                    {
                        throw new InvalidOperationException(
                            "provision_azure returned no appConfigEndpoint; cannot seed product record.");
                    }

                    await appConfig.SeedProductRecordAsync(
                        productEndpoint,
                        ProductRecordFor(updated),
                        cancellationToken);
                    await appConfig.PublishRuntimeIndexAsync(
                        ownerEndpoint,
                        updated.Product.Key,
                        RuntimeIndexValues(updated, productEndpoint),
                        cancellationToken);
                    InstanceDefinitions.Write(updated, azureRoot);
                    terminal.WriteLine($"[dsf] GitHub provisioning complete for {updated.GitHub.FullName()}.");
                    terminal.WriteLine($"[dsf] Azure provisioning complete for {updated.Product.Key} ({updated.Azure.ResourceGroup}).");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return CanceledExitCode;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or InstanceDefinitionException)
                {
                    terminal.WriteErrorLine($"[dsf] error: provisioning failed: {exception.Message}");
                    return Failure;
                }
            }

            return Success;
        });

        return command;
    }

    private static bool ResolveNewInteraction(
        ParseResult parseResult,
        ICliTerminal terminal,
        Option<string> product,
        IReadOnlyList<Option> options,
        IReadOnlySet<string> providedOptions,
        out NewInteraction interaction)
    {
        var explicitArguments = ExplicitArguments(parseResult, providedOptions, options);
        interaction = new NewInteraction(null, explicitArguments);

        if (WasProvided(providedOptions, product))
        {
            return true;
        }

        if (!terminal.Capabilities.IsInteractive)
        {
            terminal.WriteErrorLine(
                $"[dsf] error: --product is required when prompts are unavailable. Run: {RenderNewCommand(interaction, productPlaceholder: true)}");
            return false;
        }

        ShowEquivalentCommand(terminal, interaction);

        var answer = terminal.Prompt("Product key: ");
        if (string.IsNullOrWhiteSpace(answer))
        {
            terminal.WriteErrorLine("[dsf] error: product key is required.");
            return false;
        }

        interaction = interaction with { Product = answer.Trim() };
        ShowEquivalentCommand(terminal, interaction);
        return true;
    }

    /// <summary>
    /// Replays every option the caller actually passed, in declaration order, so the
    /// equivalent command reproduces the same invocation rather than a defaults-only shape.
    /// </summary>
    private static List<string> ExplicitArguments(
        ParseResult parseResult,
        IReadOnlySet<string> providedOptions,
        IReadOnlyList<Option> options)
    {
        var arguments = new List<string>();
        foreach (var option in options)
        {
            if (!WasProvided(providedOptions, option))
            {
                continue;
            }

            switch (option)
            {
                case Option<bool> flag:
                    if (parseResult.GetValue(flag))
                    {
                        arguments.Add(flag.Name);
                    }

                    break;
                case Option<string> text:
                    AddValue(arguments, text.Name, parseResult.GetValue(text));
                    break;
            }
        }

        return arguments;
    }

    private static bool WasProvided(IReadOnlySet<string> providedOptions, Option option) =>
        providedOptions.Contains(option.Name) || option.Aliases.Any(providedOptions.Contains);

    private static void ShowEquivalentCommand(ICliTerminal terminal, NewInteraction interaction) =>
        terminal.WriteLine(
            CliPresentation.EquivalentCommand(terminal.Capabilities, RenderNewCommand(interaction)));

    private static string RenderNewCommand(NewInteraction interaction, bool productPlaceholder = false)
    {
        var args = new List<string> { "dsf", "new" };
        if (productPlaceholder)
        {
            args.Add("--product");
            args.Add("<product>");
        }
        else if (!string.IsNullOrWhiteSpace(interaction.Product))
        {
            args.Add("--product");
            args.Add(interaction.Product);
        }

        args.AddRange(interaction.ExplicitArguments);
        return string.Join(' ', args);
    }

    private static void AddValue(List<string> args, string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        args.Add(option);
        args.Add(value);
    }

    private sealed record NewInteraction(string? Product, IReadOnlyList<string> ExplicitArguments);

    private static void PrintDryRunPlan(
        ICliTerminal terminal,
        string product,
        string owner,
        string repo,
        string visibility,
        string location,
        string environment,
        string namePrefix,
        string creationMaturity,
        string? githubAppId,
        string? githubInstallationId,
        string? configRoot)
    {
        var repoName = string.IsNullOrWhiteSpace(repo) ? product : repo;
        var repoFull = string.IsNullOrWhiteSpace(owner) ? repoName : $"{owner}/{repoName}";
        var visibilityFlag = visibility == "public" ? "--public" : visibility == "internal" ? "--internal" : "--private";
        var root = configRoot ?? Directory.GetCurrentDirectory();
        var manifestPath = InstanceDefinitions.PathFor(root, product);
        var bicepPath = Path.Combine(root, "infra", "main.bicep");

        if (string.IsNullOrWhiteSpace(githubInstallationId))
        {
            terminal.WriteLine("[dsf] WARNING: DSF_OWNER_KEYVAULT_URI is unset and --owner-keyvault-uri was not passed.");
            terminal.WriteLine("[dsf] WARNING: install_app, seed_app_key, seed_webiq_key, publish_runtime_index will be SKIPPED.");
            terminal.WriteLine("[dsf] WARNING: the GitHub App won't be wired; `dsf charter init` and runtime GitHub access will fail.");
            terminal.WriteLine("[dsf] WARNING: fix: run `dsf bootstrap` once, then export DSF_OWNER_KEYVAULT_URI and DSF_OWNER_APPCONFIG_ENDPOINT, then re-run `dsf new`.");
        }
        terminal.WriteLine($"[dsf] instance plan for product={product} (DRY-RUN)");
        terminal.WriteLine($"[dsf]  1. create_repo    [dry-run] Create GitHub repo {repoFull} ({visibility})");
        terminal.WriteLine($"[dsf]       $ gh repo create {repoFull} {visibilityFlag}");
        terminal.WriteLine($"[dsf]  2. seed_repo      [seeded (dry-run)] Seed {repoFull} with the Spec Kit scaffold (specify init) and a baseline ci workflow so the required 'ci' check is producible before branch protection");
        terminal.WriteLine($"[dsf]  3. create_labels  [dry-run] Create the label taxonomy + handoff label in {repoFull}");
        if (string.IsNullOrWhiteSpace(githubInstallationId))
        {
            terminal.WriteLine($"[dsf]  4. install_app    [skipped (no owner App configured)] Add {repoFull} to the DSF App installation <installation>");
        }
        else
        {
            terminal.WriteLine($"[dsf]  4. install_app    [app binding planned (dry-run)] Add {repoFull} to the DSF App {githubAppId ?? "<app>"} installation {githubInstallationId}");
        }
        terminal.WriteLine($"[dsf]  5. create_resource_group [dry-run] Create dedicated Azure resource group rg-dsf-{product}");
        terminal.WriteLine($"[dsf]       $ az group create --name rg-dsf-{product} --location {location} --tags project=dark-software-factory managed-by=dsf product={product} component=backing-services");
        terminal.WriteLine("[dsf]  6. provision_azure [dry-run] Deploy backing services into rg-dsf-" + product + " from infra/main.bicep");
        terminal.WriteLine($"[dsf]       $ az deployment group create -g rg-dsf-{product} -n dsf-{product} -f {bicepPath} -p namePrefix={namePrefix} environmentName={environment} location={location} product={product} runtimeImage=ghcr.io/joranbergfeld/dsf-runtime:latest githubAppId= githubInstallationId= githubRepository={repoFull} allowPublicNetworkAccess=true --no-wait");
        terminal.WriteLine($"[dsf]  7. seed_appconfig [seeded (dry-run)] Seed the canonical config/defaults.json into App Configuration for {product} (critic/agent flags + thresholds)");
        terminal.WriteLine($"[dsf]  8. seed_app_key   [skipped (no owner App configured)] Seed the DSF App private key from the owner Key Vault into the product Key Vault for {product}");
        terminal.WriteLine($"[dsf]  9. seed_webiq_key [skipped (no owner App configured)] Seed the WebIQ API key from the owner Key Vault into the product Key Vault for {product}");
        terminal.WriteLine($"[dsf]  10. seed_product_record [seeded (dry-run)] Seed the {product} Product record (repo, taxonomy, source scopes, threshold) into its per-product App Configuration");
        terminal.WriteLine($"[dsf]  11. publish_runtime_index [skipped (no owner App Config configured)] Publish {product} runtime env (endpoints + pointers) to the owner App Configuration index");
        terminal.WriteLine($"[dsf]  12. deploy_council [rendered (dry-run)] Render + bring up the feature-council runtime scoped to {product}");
        terminal.WriteLine($"[dsf]  13. branch_protection [ruleset planned (dry-run)] Apply the '{creationMaturity}' creation maturity dial to {repoFull} as a branch-protection ruleset (required reviews + green 'ci' check)");
        terminal.WriteLine($"[dsf]  14. deploy_sre_agent [deployed (dry-run)] Provision the Azure SRE Agent for {product} (agent + RBAC on rg-dsf-{product} + Azure Monitor)");
        terminal.WriteLine($"[dsf]  15. write_config   [{manifestPath}] Write instance manifest to config/instances/{product}.json");
    }

    /// <summary>
    /// Persists the clean planned instance definition for `--dry-run --write-plan`.
    /// Only configuration is written: no command log, no execution plan, no secret values.
    /// </summary>
    private static bool WritePlannedDefinition(
        ICliTerminal terminal,
        InstanceDefinition definition,
        string? configRoot)
    {
        try
        {
            InstanceDefinitions.Write(definition, configRoot ?? Directory.GetCurrentDirectory());
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InstanceDefinitionException)
        {
            terminal.WriteErrorLine(
                $"[dsf] error: could not write the instance definition for '{definition.Product.Key}': {exception.Message}");
            return false;
        }
    }

    private static InstanceDefinition BuildPlannedDefinition(
        string product,
        string owner,
        string repo,
        string visibility,
        string runtimeTarget,
        string environment,
        string location,
        string creationMaturity,
        string namePrefix,
        string? ownerKeyVaultUri,
        string? ownerAppConfigEndpoint,
        string? adminPrincipalId,
        string? githubAppId,
        string? githubInstallationId,
        string? configRoot)
    {
        var root = configRoot ?? Directory.GetCurrentDirectory();
        var existing = ReadExistingDefinition(root, product);
        return PlannedInstanceDefinition.Build(
            product,
            owner,
            repo,
            visibility,
            runtimeTarget,
            environment,
            location,
            creationMaturity,
            namePrefix,
            ownerKeyVaultUri,
            ownerAppConfigEndpoint,
            adminPrincipalId,
            githubAppId,
            githubInstallationId,
            DateTimeOffset.UtcNow,
            existing);
    }

    private static string? FirstConfiguredValue(string? optionValue, string environmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(optionValue))
        {
            return optionValue.Trim();
        }

        var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(environmentValue) ? null : environmentValue.Trim();
    }

    private static bool ValidateGitHubIdentifier(
        ICliTerminal terminal,
        string optionName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.All(char.IsAsciiDigit) && value.Any(character => character != '0'))
        {
            return true;
        }

        terminal.WriteErrorLine(
            $"[dsf] error: {optionName} must be a positive numeric GitHub identifier.");
        return false;
    }

    private static InstanceDefinition? ReadExistingDefinition(string root, string product)
    {
        var path = InstanceDefinitions.PathFor(root, product);
        return File.Exists(path) ? InstanceDefinitions.Read(path) : null;
    }

    private static string BuildNamePrefix(string value)
    {
        var chars = value.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(8).ToArray();
        return new string(chars).PadRight(8, 'x') + "0000";
    }

    private static Command BuildListCommand(ICliTerminal terminal, IAppConfigurationClient appConfig)
    {
        var json = BoolOption("--json", "emit the factory rows as JSON for scripting");
        var ownerAppConfigEndpoint = StringOption("--owner-appconfig-endpoint", "owner App Configuration endpoint");
        var command = new Command("list", "list provisioned product factories from the owner App Config index");
        command.Aliases.Add("ls");
        AddOptions(command, json, ownerAppConfigEndpoint);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var endpoint = FirstConfiguredValue(
                parseResult.GetValue(ownerAppConfigEndpoint),
                "DSF_OWNER_APPCONFIG_ENDPOINT");
            if (endpoint is null)
            {
                terminal.WriteErrorLine(
                    "[dsf] error: DSF_OWNER_APPCONFIG_ENDPOINT or --owner-appconfig-endpoint is required to list products.");
                return Failure;
            }

            try
            {
                var products = await appConfig.ListProductsAsync(endpoint, cancellationToken);
                if (parseResult.GetValue(json))
                {
                    terminal.WriteLine(System.Text.Json.JsonSerializer.Serialize(products));
                }
                else if (products.Count == 0)
                {
                    terminal.WriteLine("[dsf] no provisioned product factories found.");
                }
                else
                {
                    foreach (var product in products)
                    {
                        terminal.WriteLine($"{product.Key}  {product.GitHubRepository}  {product.AppConfigEndpoint}");
                    }
                }

                return Success;
            }
            catch (InvalidOperationException exception)
            {
                terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
                return Failure;
            }
        });
        return command;
    }

    private static Command BuildOffboardCommand()
    {
        var product = new Argument<string>("product") { Description = "product key to offboard" };
        var dryRun = BoolOption("--dry-run", "preview only: print the teardown plan without side effects");
        var yes = BoolOption("--yes", "skip interactive confirmation for destructive delete steps");
        var purge = BoolOption("--purge", "also purge soft-deleted resources for name reuse");
        var configRoot = StringOption("--config-root", "override repo root where config/instances/ lives");
        var ownerAppConfigEndpoint = StringOption("--owner-appconfig-endpoint", "owner App Configuration endpoint");
        var command = new Command("offboard", "remove Azure/runtime artifacts for a product");
        command.Arguments.Add(product);
        AddOptions(command, dryRun, yes, purge, configRoot, ownerAppConfigEndpoint);
        command.SetAction(parseResult => MissingManifest(parseResult.GetRequiredValue(product), "Offboard requires"));
        return command;
    }

    private static Command BuildBootstrapCommand()
    {
        var appName = RequiredStringOption("--app-name", "GitHub App name");
        var keyVaultName = RequiredStringOption("--keyvault-name", "owner Key Vault name for App credentials");
        var appConfigName = RequiredStringOption("--appconfig-name", "owner App Configuration store name");
        var resourceGroup = StringOption("--resource-group", "resource group for the owner Key Vault", "rg-dsf-app");
        var location = StringOption("--location", "Azure region for the owner Key Vault", "swedencentral");
        var command = new Command("bootstrap", "one-time: create the DSF GitHub App and store it in the owner Key Vault");
        AddOptions(command, appName, keyVaultName, appConfigName, resourceGroup, location);
        command.SetAction(_ =>
        {
            Console.Out.WriteLine("[dsf] bootstrap is not implemented in the .NET migration shell.");
            return Success;
        });
        return command;
    }

    private static Command BuildDeleteCommand(string name)
    {
        var product = new Argument<string>("product") { Description = "product key to destroy" };
        var yes = BoolOption("--yes", "skip the interactive confirmation prompt");
        var dryRun = BoolOption("--dry-run", "preview only: print the full teardown plan");
        var purge = BoolOption("--purge", "purge soft-deleted resources for name reuse");
        var configRoot = StringOption("--config-root", "override repo root where config/instances/ is read");
        var ownerAppConfigEndpoint = StringOption("--owner-appconfig-endpoint", "owner App Configuration endpoint");
        var command = new Command(name, "permanently destroy a product factory instance");
        command.Arguments.Add(product);
        AddOptions(command, yes, dryRun, purge, configRoot, ownerAppConfigEndpoint);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var productValue = parseResult.GetRequiredValue(product);
            Console.Error.WriteLine(
                $"[dsf] error: no manifest found for product '{productValue}'. Run 'dsf new' first or check the product name.");
            return Failure;
        });
        return command;
    }

    private static Command BuildRunCommand()
    {
        var signal = StringOption("--signal", "path to a signal JSON file");
        var dryRun = BoolOption("--dry-run", "run the line but skip filing");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("run", "run the intake line for one signal (runtime)");
        AddOptions(command, signal, dryRun, product);
        command.SetAction(parseResult => RuntimeShell(
            parseResult.GetValue(product), _ => RuntimeVerbs.Run(parseResult.GetValue(signal), parseResult.GetValue(dryRun))));
        return command;
    }

    private static Command BuildSweepCommand()
    {
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("sweep", "sweep enabled source agents once (runtime)");
        AddOptions(command, product);
        command.SetAction(parseResult => RuntimeShell(
            parseResult.GetValue(product), settings => RuntimeVerbs.Sweep(settings.Product)));
        return command;
    }

    private static Command BuildServeOrchestratorCommand()
    {
        var loop = BoolOption("--loop", "sweep continuously");
        var interval = IntOption("--interval", "seconds between sweeps");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-orchestrator", "run the orchestrator worker (runtime)");
        AddOptions(command, loop, interval, product);
        command.SetAction(parseResult => RuntimeShell(
            parseResult.GetValue(product), settings => RuntimeVerbs.ServeOrchestrator(settings.Product)));
        return command;
    }

    private static Command BuildServeAgentCommand()
    {
        var kind = StringOption("--kind", "source agent kind", "sentry");
        var host = StringOption("--host", "bind host", "0.0.0.0");
        var port = IntOption("--port", "bind port", 8080);
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-agent", "serve a source agent over A2A (runtime)");
        AddOptions(command, kind, host, port, product);
        // serve-agent must still validate required runtime config before validating
        // --kind, exactly like every other runtime verb.
        command.SetAction(parseResult => RuntimeShell(
            parseResult.GetValue(product), _ => RuntimeVerbs.ServeAgent(parseResult.GetValue(kind) ?? "sentry")));
        return command;
    }

    private static Command BuildCharterCommand(
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore)
    {
        var command = new Command("charter", "manage the product charter (.dsf/charter.md)");
        command.Subcommands.Add(SimpleCharterCommand(
            "init", "interview to draft a charter and open a PR", terminal, appConfig, charterRepository, charterStore));
        command.Subcommands.Add(CharterImplementCommand(terminal, appConfig, charterRepository, charterStore));
        command.Subcommands.Add(CharterWatchCommand(terminal, appConfig, charterRepository));
        command.Subcommands.Add(CharterSourceCommand(
            "sync",
            "pull .dsf/charter.md (local file or --ref) into Cosmos",
            terminal,
            appConfig,
            charterRepository,
            charterStore));
        command.Subcommands.Add(CharterSourceCommand(
            "status",
            "show the stored charter status + drift",
            terminal,
            appConfig,
            charterRepository,
            charterStore));
        return command;
    }

    private static Command SimpleCharterCommand(
        string name,
        string description,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore)
    {
        var product = RequiredStringOption("--product", "product key");
        var command = new Command(name, description);
        AddOptions(command, product);
        command.SetAction(async (parseResult, cancellationToken) => await RunCharterAsync(
            name,
            parseResult.GetRequiredValue(product),
            null,
            null,
            terminal,
            appConfig,
            charterRepository,
            charterStore,
            cancellationToken));
        return command;
    }

    private static Command CharterSourceCommand(
        string name,
        string description,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore)
    {
        var product = RequiredStringOption("--product", "product key");
        var file = StringOption("--file", "path to a local charter file");
        var refOption = StringOption("--ref", "read the charter from this repo ref via the GitHub App");
        var command = new Command(name, description);
        AddOptions(command, product, file, refOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (parseResult.GetValue(file) is not null && parseResult.GetValue(refOption) is not null)
            {
                Console.Error.WriteLine("[dsf] error: --file and --ref cannot be used together.");
                return Failure;
            }

            return await RunCharterAsync(
                name,
                parseResult.GetRequiredValue(product),
                parseResult.GetValue(file),
                parseResult.GetValue(refOption),
                terminal,
                appConfig,
                charterRepository,
                charterStore,
                cancellationToken);
        });
        return command;
    }

    private static Command CharterImplementCommand(
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore)
    {
        var product = RequiredStringOption("--product", "product key");
        var noWait = BoolOption("--no-wait", "file + assign only; do not watch");
        var timeout = DoubleOption("--timeout", "max seconds to wait");
        var pollInterval = DoubleOption("--poll-interval", "seconds between polls");
        var command = new Command("implement", "render the constitution + file the Spec Kit bootstrap issue");
        AddOptions(command, product, noWait, timeout, pollInterval);
        command.SetAction(async (parseResult, cancellationToken) => await RunCharterImplementAsync(
            parseResult.GetRequiredValue(product),
            parseResult.GetValue(noWait),
            parseResult.GetValue(timeout),
            parseResult.GetValue(pollInterval),
            terminal,
            appConfig,
            charterRepository,
            charterStore,
            cancellationToken));
        return command;
    }

    private static Command CharterWatchCommand(
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository)
    {
        var product = RequiredStringOption("--product", "product key");
        var issue = IntOption("--issue", "bootstrap issue number");
        var timeout = DoubleOption("--timeout", "max seconds to watch");
        var pollInterval = DoubleOption("--poll-interval", "seconds between polls");
        var command = new Command("watch", "watch the coding agent's build and request Copilot review when ready");
        AddOptions(command, product, issue, timeout, pollInterval);
        command.SetAction(async (parseResult, cancellationToken) => await RunCharterWatchAsync(
            parseResult.GetRequiredValue(product),
            parseResult.GetValue(issue),
            parseResult.GetValue(timeout),
            parseResult.GetValue(pollInterval),
            terminal,
            appConfig,
            charterRepository,
            cancellationToken));
        return command;
    }

    private static int MissingManifest(string product, string action)
    {
        Console.Error.WriteLine(
            $"[dsf] error: Instance manifest not found for product '{product}'. {action} config/instances/{product}.json.");
        return Failure;
    }

    /// <summary>
    /// Composes <see cref="RuntimeSettings"/> and, once they validate, runs
    /// <paramref name="operation"/> -- the verb's real per-invocation work (see
    /// <see cref="RuntimeVerbs"/>). Both a settings failure
    /// (<see cref="RuntimeConfigurationException"/>) and an operation failure
    /// (<see cref="RuntimeVerbException"/>) are printed to stderr and exit
    /// non-zero the same way.
    /// </summary>
    private static int RuntimeShell(string? product, Action<Dsf.Core.Runtime.RuntimeSettings> operation)
    {
        Dsf.Core.Runtime.RuntimeSettings settings;
        try
        {
            settings = RuntimeSettingsComposer.FromEnvironment(product);
        }
        catch (RuntimeConfigurationException exception)
        {
            Console.Error.WriteLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        try
        {
            operation(settings);
        }
        catch (RuntimeVerbException exception)
        {
            Console.Error.WriteLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        return Success;
    }

    private const string ConstitutionPath = ".specify/memory/constitution.md";
    private const string HandoffLabel = "creation:ready";
    private const string CopilotModelRequest = "Claude Opus 4.8";
    private const double DefaultWatchTimeout = 1800.0;
    private const double DefaultWatchPollInterval = 20.0;
    private const double MinimumWatchPollInterval = 1.0;
    private const string WatchPollIntervalEnvironmentVariable = "DSF_WATCH_POLL_INTERVAL";

    private static async Task<int> RunCharterImplementAsync(
        string product,
        bool noWait,
        double? timeout,
        double? pollInterval,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore,
        CancellationToken cancellationToken)
    {
        var location = await ResolveProductLocationAsync(product, terminal, appConfig, cancellationToken);
        if (location is null)
        {
            return Failure;
        }

        try
        {
            var source = await ReadMainCharterAsync(location, charterRepository, cancellationToken);
            var syncCode = await RunCharterSyncAsync(
                product,
                location,
                source,
                "main",
                terminal,
                charterStore,
                cancellationToken);
            var stored = await charterStore.GetCharterAsync(product, cancellationToken);
            if (syncCode != Success || stored is null || stored.Status != CharterStatus.Ok || stored.Charter is null)
            {
                var status = stored?.Status.ToString().ToLowerInvariant() ?? "missing";
                terminal.WriteErrorLine(
                    $"[dsf] error: charter for {product} on main is {status}; merge the charter PR (and fix any errors) before implementing.");
                if (!string.IsNullOrWhiteSpace(stored?.LastError))
                {
                    terminal.WriteErrorLine($"[dsf]   note: {stored.LastError}");
                }

                return Failure;
            }

            var charter = stored.Charter;
            var constitution = RenderConstitution(charter);
            var (pullRequestUrl, alreadyCurrent) = await EnsureConstitutionPullRequestAsync(
                location.GitHubRepository,
                product,
                charter,
                constitution,
                terminal,
                charterRepository,
                cancellationToken);
            if (!alreadyCurrent)
            {
                var merged = await WaitForConstitutionOnMainAsync(
                    location.GitHubRepository,
                    product,
                    charter,
                    timeout,
                    pollInterval,
                    terminal,
                    charterRepository,
                    cancellationToken);
                if (!merged)
                {
                    terminal.WriteErrorLine(
                        "[dsf] error: the constitution PR has not merged within the timeout; not filing the bootstrap issue.");
                    terminal.WriteErrorLine(
                        $"[dsf]   approve + merge the constitution PR ({pullRequestUrl}) then re-run `dsf charter implement --product {product}`; it resumes and skips the already-merged constitution.");
                    return 2;
                }
            }

            var (title, body) = RenderBootstrapIssue(charter);
            var issue = await charterRepository.CreateIssueAsync(
                location.GitHubRepository,
                title,
                body,
                [HandoffLabel],
                cancellationToken);
            var assigned = await charterRepository.AssignCopilotWithAppAsync(
                    location.GitHubRepository,
                    issue.NodeId,
                    cancellationToken)
                || await charterRepository.AssignCopilotWithGhAsync(
                    location.GitHubRepository,
                    issue.NodeId,
                    cancellationToken);
            if (assigned)
            {
                terminal.WriteLine($"[dsf] filed bootstrap issue + assigned Copilot: {issue.HtmlUrl}");
            }
            else
            {
                terminal.WriteLine($"[dsf] filed bootstrap issue: {issue.HtmlUrl}");
                terminal.WriteErrorLine(
                    "[dsf] warning: could not assign the Copilot coding agent; assign it manually (ensure `gh auth login` and that the Copilot coding agent is enabled for the repo).");
                return Success;
            }

            if (noWait)
            {
                terminal.WriteLine(
                    $"[dsf] not waiting; run `dsf charter watch --product {product}` to hand off to Copilot review once the build is ready.");
                return Success;
            }

            return await RunWatchLoopAsync(
                location.GitHubRepository,
                IssueNumberFromUrl(issue.HtmlUrl),
                product,
                timeout,
                pollInterval,
                terminal,
                charterRepository,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CanceledExitCode;
        }
        catch (InvalidOperationException exception)
        {
            terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
            return Failure;
        }
    }

    private static async Task<int> RunCharterWatchAsync(
        string product,
        int? issueNumber,
        double? timeout,
        double? pollInterval,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        CancellationToken cancellationToken)
    {
        var location = await ResolveProductLocationAsync(product, terminal, appConfig, cancellationToken);
        if (location is null)
        {
            return Failure;
        }

        try
        {
            var issue = issueNumber ?? await charterRepository.NewestReadyIssueAsync(
                location.GitHubRepository,
                HandoffLabel,
                cancellationToken);
            if (issue is null)
            {
                terminal.WriteErrorLine(
                    $"[dsf] error: no open '{HandoffLabel}' issue found for {product}; pass --issue N.");
                return Failure;
            }

            return await RunWatchLoopAsync(
                location.GitHubRepository,
                issue.Value,
                product,
                timeout,
                pollInterval,
                terminal,
                charterRepository,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CanceledExitCode;
        }
        catch (Exception exception)
        {
            terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
            return Failure;
        }
    }

    private static async Task<ProductLocation?> ResolveProductLocationAsync(
        string product,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        CancellationToken cancellationToken)
    {
        var ownerEndpoint = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        if (string.IsNullOrWhiteSpace(ownerEndpoint))
        {
            terminal.WriteErrorLine(
                "[dsf] error: DSF_OWNER_APPCONFIG_ENDPOINT is required to resolve the product repository.");
            return null;
        }

        try
        {
            return await appConfig.ResolveProductAsync(ownerEndpoint, product, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            terminal.WriteErrorLine($"[dsf] error: product '{product}' is not in registry");
            return null;
        }
    }

    private static async Task<CharterSource?> ReadMainCharterAsync(
        ProductLocation location,
        ICharterRepositoryClient charterRepository,
        CancellationToken cancellationToken)
    {
        var file = await charterRepository.ReadAsync(
            location.GitHubRepository,
            CharterMarkdown.CharterPath,
            "main",
            cancellationToken);
        return file is null
            ? null
            : new CharterSource(
                file.Content,
                string.IsNullOrWhiteSpace(file.Sha) ? CharterMarkdown.GitBlobSha(file.Content) : file.Sha,
                "main");
    }

    private static async Task<(string? PullRequestUrl, bool AlreadyCurrent)> EnsureConstitutionPullRequestAsync(
        string repository,
        string product,
        Charter charter,
        string constitution,
        ICliTerminal terminal,
        ICharterRepositoryClient charterRepository,
        CancellationToken cancellationToken)
    {
        var existing = await charterRepository.ReadAsync(repository, ConstitutionPath, "main", cancellationToken);
        if (IsConstitutionCurrent(existing?.Content, charter))
        {
            terminal.WriteLine($"[dsf] constitution already on main for {product}; skipping PR");
            return (null, true);
        }

        var sha8 = (charter.SourceSha ?? "unknown")[..Math.Min((charter.SourceSha ?? "unknown").Length, 8)];
        var headPrefix = $"charter/constitution-{sha8}-";
        var current = await charterRepository.LatestPullRequestWithHeadPrefixAsync(
            repository,
            headPrefix,
            cancellationToken);
        if (current is { State: "open" or "OPEN" })
        {
            terminal.WriteLine($"[dsf] reusing open constitution PR: {current.HtmlUrl}");
            return (current.HtmlUrl, false);
        }

        var branch = $"{headPrefix}{Guid.NewGuid():N}"[..(headPrefix.Length + 8)];
        var url = await charterRepository.OpenFilePullRequestAsync(
            repository,
            ConstitutionPath,
            constitution,
            branch,
            $"Add Spec Kit constitution for {product}",
            "Constitution derived from the product charter by `dsf charter implement`. Auto-merge is requested: on repos where it is enabled this merges once the `ci` check is green, otherwise it awaits a human review. (Creation-maturity gating is future scope.)",
            $"docs: add spec kit constitution for {product}",
            true,
            existing?.Sha,
            cancellationToken);
        terminal.WriteLine($"[dsf] opened constitution PR (auto-merge requested): {url}");
        return (url, false);
    }

    private static async Task<bool> WaitForConstitutionOnMainAsync(
        string repository,
        string product,
        Charter charter,
        double? timeout,
        double? pollInterval,
        ICliTerminal terminal,
        ICharterRepositoryClient charterRepository,
        CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        var lastStatus = string.Empty;
        var seconds = ResolveTimeout(timeout);
        while (true)
        {
            try
            {
                var existing = await charterRepository.ReadAsync(repository, ConstitutionPath, "main", cancellationToken);
                if (IsConstitutionCurrent(existing?.Content, charter))
                {
                    terminal.WriteLine($"[dsf] constitution merged to main: {repository}");
                    return true;
                }

                WriteStatusOnce(terminal, ref lastStatus, "waiting for the constitution PR to merge...");
            }
            catch (Exception exception) when (IsTransientGitHubError(exception))
            {
                WriteStatusOnce(terminal, ref lastStatus, $"transient GitHub error ({exception.GetType().Name}); retrying...");
            }

            if (TimedOut(start, seconds))
            {
                return false;
            }

            await DelayAsync(start, seconds, pollInterval, cancellationToken);
        }
    }

    private static async Task<int> RunWatchLoopAsync(
        string repository,
        int issueNumber,
        string product,
        double? timeout,
        double? pollInterval,
        ICliTerminal terminal,
        ICharterRepositoryClient charterRepository,
        CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        var seconds = ResolveTimeout(timeout);
        var lastStatus = string.Empty;
        while (true)
        {
            try
            {
                var pullRequest = await charterRepository.FindCodingAgentPullRequestAsync(
                    repository,
                    issueNumber,
                    cancellationToken);
                if (pullRequest is null)
                {
                    WriteStatusOnce(terminal, ref lastStatus, "waiting for the coding agent to open its PR...");
                }
                else if (pullRequest.State is "MERGED" or "CLOSED")
                {
                    terminal.WriteLine(
                        $"[dsf] {repository}#{pullRequest.Number} is {pullRequest.State.ToLowerInvariant()}; nothing to review.");
                    return Success;
                }
                else if (!pullRequest.IsDraft)
                {
                    return await HandOffForReviewAsync(repository, pullRequest, terminal, charterRepository, cancellationToken);
                }
                else if (await charterRepository.AgentWorkFinishedAsync(repository, pullRequest.Number, cancellationToken))
                {
                    await charterRepository.MarkPullRequestReadyAsync(repository, pullRequest.Number, cancellationToken);
                    terminal.WriteLine($"[dsf] {repository}#{pullRequest.Number} marked ready for review");
                    return await HandOffForReviewAsync(repository, pullRequest, terminal, charterRepository, cancellationToken);
                }
                else
                {
                    WriteStatusOnce(terminal, ref lastStatus, $"{repository}#{pullRequest.Number} building (draft)...");
                }
            }
            catch (Exception exception) when (IsTransientGitHubError(exception))
            {
                WriteStatusOnce(terminal, ref lastStatus, $"transient GitHub error ({exception.GetType().Name}); retrying...");
            }

            if (TimedOut(start, seconds))
            {
                terminal.WriteLine(
                    $"[dsf] still building after {(int)seconds!.Value}s; re-run `dsf charter watch --product {product}` to resume.");
                return 2;
            }

            await DelayAsync(start, seconds, pollInterval, cancellationToken);
        }
    }

    private static async Task<int> HandOffForReviewAsync(
        string repository,
        CodingAgentPullRequest pullRequest,
        ICliTerminal terminal,
        ICharterRepositoryClient charterRepository,
        CancellationToken cancellationToken)
    {
        if (await charterRepository.HasCopilotReviewRequestAsync(repository, pullRequest.Number, cancellationToken))
        {
            terminal.WriteLine($"[dsf] Copilot review already requested: {pullRequest.Url}");
            return Success;
        }

        await charterRepository.RequestCopilotReviewAsync(repository, pullRequest.Url, cancellationToken);
        terminal.WriteLine($"[dsf] requested Copilot review: {pullRequest.Url}");
        return Success;
    }

    private static string RenderConstitution(Charter charter)
    {
        var sourceSha = charter.SourceSha ?? string.Empty;
        var sourceRef = charter.SourceRef ?? string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine($"<!-- dsf:constitution schema_version=1 source_sha={sourceSha} source_ref={sourceRef} -->");
        builder.AppendLine($"# Spec Kit Constitution: {charter.Product}");
        builder.AppendLine();
        builder.AppendLine("## Product Charter");
        builder.AppendLine(charter.Vision);
        builder.AppendLine();
        builder.AppendLine("## Principles");
        foreach (var goal in charter.Goals)
        {
            builder.AppendLine($"- {goal}");
        }
        builder.AppendLine();
        builder.AppendLine("## Constraints");
        builder.AppendLine(charter.Constraints);
        return builder.ToString();
    }

    private static bool IsConstitutionCurrent(string? text, Charter charter) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains("dsf:constitution schema_version=1", StringComparison.Ordinal)
        && text.Contains($"source_sha={charter.SourceSha}", StringComparison.Ordinal)
        && text.Contains($"source_ref={charter.SourceRef}", StringComparison.Ordinal);

    private static (string Title, string Body) RenderBootstrapIssue(Charter charter)
    {
        var title = $"Build {charter.Product} from its charter (Spec Kit)";
        var body = $"""
            Bootstrap the **{charter.Product}** product from its accepted charter using the GitHub Spec Kit lifecycle, in a single session.

            ## What to do (one session)
            1. `/speckit.specify` — derive the product specification from the charter below and `{CharterMarkdown.CharterPath}`.
            2. `/speckit.plan` — choose a sensible tech stack and architecture (a paved-road default is not wired yet — your choice for now).
            3. `/speckit.tasks` — break the plan into actionable tasks.
            4. Implement the tasks and open pull request(s); keep the `ci` check green.

            ## Governing documents
            - Constitution: `{ConstitutionPath}` (derived from the charter — your principles and quality gates).
            - Charter: `{CharterMarkdown.CharterPath}` (the human-owned source of truth).

            ## Product charter (reference)
            <untrusted-product-charter>
            Product: {charter.Product}
            Vision: {charter.Vision}
            Target users: {charter.TargetUsers}
            Goals:
            {BulletList(charter.Goals)}
            Non-goals:
            {BulletList(charter.NonGoals)}
            Success metrics:
            {BulletList(charter.SuccessMetrics)}
            Constraints: {charter.Constraints}
            </untrusted-product-charter>

            ---
            _Model request: this build is intended to run as {CopilotModelRequest}. Copilot's model is a repository/account setting, so treat this as a request, not a guarantee._
            """;
        return (title, body);
    }

    private static string BulletList(IReadOnlyList<string> values) =>
        values.Count == 0 ? "- (none)" : string.Join(Environment.NewLine, values.Select(value => $"- {value}"));

    private static int IssueNumberFromUrl(string url) =>
        int.Parse(url.TrimEnd('/').Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);

    private static double? ResolveTimeout(double? explicitSeconds)
    {
        var seconds = explicitSeconds ?? DefaultWatchTimeout;
        return seconds <= 0 ? null : seconds;
    }

    /// <summary>Poll cadence: explicit flag (floored at 1s) &gt; DSF_WATCH_POLL_INTERVAL (floored at 1s) &gt; 20s.</summary>
    internal static double ResolveWatchPollInterval(double? explicitSeconds)
    {
        if (explicitSeconds is not null)
        {
            return Math.Max(MinimumWatchPollInterval, explicitSeconds.Value);
        }

        var raw = Environment.GetEnvironmentVariable(WatchPollIntervalEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(MinimumWatchPollInterval, parsed);
        }

        return DefaultWatchPollInterval;
    }

    private static bool TimedOut(DateTimeOffset start, double? seconds) =>
        seconds is not null && (DateTimeOffset.UtcNow - start).TotalSeconds >= seconds.Value;

    private static async Task DelayAsync(
        DateTimeOffset start,
        double? timeoutSeconds,
        double? pollInterval,
        CancellationToken cancellationToken)
    {
        var delay = ResolveWatchPollInterval(pollInterval);
        if (timeoutSeconds is not null)
        {
            var remaining = timeoutSeconds.Value - (DateTimeOffset.UtcNow - start).TotalSeconds;
            delay = Math.Min(delay, Math.Max(0.01, remaining));
        }

        await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
    }

    private static void WriteStatusOnce(ICliTerminal terminal, ref string lastStatus, string status)
    {
        if (status == lastStatus)
        {
            return;
        }

        terminal.WriteLine($"[dsf] {status}");
        lastStatus = status;
    }

    private static bool IsTransientGitHubError(Exception exception) =>
        exception is HttpRequestException
            or JsonException
            or KeyNotFoundException
            or GhCommandException
            or GitHubGraphQlException
            or GitHubApiException { StatusCode: HttpStatusCode.TooManyRequests }
            or GitHubApiException { StatusCode: >= HttpStatusCode.InternalServerError };

    private static async Task<int> RunCharterAsync(
        string verb,
        string product,
        string? file,
        string? reference,
        ICliTerminal terminal,
        IAppConfigurationClient appConfig,
        ICharterRepositoryClient charterRepository,
        ICharterStore charterStore,
        CancellationToken cancellationToken)
    {
        var ownerEndpoint = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        if (string.IsNullOrWhiteSpace(ownerEndpoint))
        {
            terminal.WriteErrorLine(
                "[dsf] error: DSF_OWNER_APPCONFIG_ENDPOINT is required to resolve the product repository.");
            return Failure;
        }

        try
        {
            var location = await appConfig.ResolveProductAsync(ownerEndpoint, product, cancellationToken);
            if (verb == "init")
            {
                return await RunCharterInitAsync(product, location, terminal, charterRepository, cancellationToken);
            }

            var sourceRef = reference ?? "main";
            CharterSource source;
            if (file is not null)
            {
                if (!File.Exists(file))
                {
                    terminal.WriteErrorLine($"[dsf] error: cannot read {file}: file does not exist.");
                    return Failure;
                }

                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                source = new CharterSource(
                    Encoding.UTF8.GetString(bytes), CharterMarkdown.GitBlobSha(bytes), $"file:{file}");
            }
            else
            {
                var charterFile = await charterRepository.ReadAsync(
                    location.GitHubRepository, CharterMarkdown.CharterPath, sourceRef, cancellationToken);
                source = charterFile is null
                    ? null!
                    : new CharterSource(
                        charterFile.Content,
                        string.IsNullOrWhiteSpace(charterFile.Sha)
                            ? CharterMarkdown.GitBlobSha(charterFile.Content)
                            : charterFile.Sha,
                        sourceRef);
            }

            return verb == "sync"
                ? await RunCharterSyncAsync(
                    product, location, source, sourceRef, terminal, charterStore, cancellationToken)
                : await RunCharterStatusAsync(product, source, terminal, charterStore, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CanceledExitCode;
        }
        catch (InvalidOperationException exception)
        {
            terminal.WriteErrorLine($"[dsf] error: {exception.Message}");
            return Failure;
        }
    }

    /// <summary>The live charter as read from the product repo (or a local file).</summary>
    private sealed record CharterSource(string Content, string Sha, string Reference);

    private static async Task<int> RunCharterInitAsync(
        string product,
        ProductLocation location,
        ICliTerminal terminal,
        ICharterRepositoryClient charterRepository,
        CancellationToken cancellationToken)
    {
        if (!terminal.Capabilities.IsInteractive)
        {
            terminal.WriteErrorLine(
                "[dsf] error: charter init requires an interactive terminal to collect product intent.");
            return Failure;
        }

        var content = InitialCharter(product, terminal);
        var url = await charterRepository.OpenInitialPullRequestAsync(
            location.GitHubRepository, product, content, cancellationToken);
        terminal.WriteLine($"[dsf] opened charter PR: {url}");
        return Success;
    }

    /// <summary>
    /// Parses the live charter and persists a <see cref="StoredCharter"/>. Idempotent on the
    /// source blob SHA; a missing or unparseable file becomes stored state (MISSING/INVALID)
    /// that preserves the last known-good charter rather than losing it.
    /// </summary>
    private static async Task<int> RunCharterSyncAsync(
        string product,
        ProductLocation location,
        CharterSource? source,
        string sourceRef,
        ICliTerminal terminal,
        ICharterStore charterStore,
        CancellationToken cancellationToken)
    {
        var prior = await charterStore.GetCharterAsync(product, cancellationToken);
        var lastGood = prior?.Charter;
        StoredCharter stored;
        if (source is null)
        {
            stored = new StoredCharter(
                product,
                location.GitHubRepository,
                lastGood,
                CharterStatus.Missing,
                prior?.SourceSha,
                sourceRef,
                prior?.Content,
                prior?.LastSyncedAt,
                $"{CharterMarkdown.CharterPath} not found on {sourceRef}");
        }
        else if (prior is { Status: CharterStatus.Ok, Charter: not null } && prior.SourceSha == source.Sha)
        {
            ReportSync(terminal, product, prior);
            return Success; // idempotent: unchanged blob SHA since the last good sync
        }
        else
        {
            try
            {
                var charter = CharterMarkdown.Parse(source.Content, product) with
                {
                    SourceSha = source.Sha,
                    SourceRef = source.Reference,
                };
                stored = new StoredCharter(
                    product,
                    location.GitHubRepository,
                    charter,
                    CharterStatus.Ok,
                    source.Sha,
                    source.Reference,
                    source.Content,
                    DateTimeOffset.UtcNow,
                    null);
            }
            catch (CharterParseException exception)
            {
                stored = new StoredCharter(
                    product,
                    location.GitHubRepository,
                    lastGood,
                    CharterStatus.Invalid,
                    prior?.SourceSha,
                    source.Reference,
                    prior?.Content,
                    prior?.LastSyncedAt,
                    exception.Message);
            }
        }

        await charterStore.PutCharterAsync(stored, cancellationToken);
        ReportSync(terminal, product, stored);
        return stored.Status == CharterStatus.Invalid ? Failure : Success;
    }

    private static void ReportSync(ICliTerminal terminal, string product, StoredCharter stored)
    {
        terminal.WriteLine($"[dsf] synced charter for {product}: {StatusValue(stored.Status)}");
        if (!string.IsNullOrWhiteSpace(stored.LastError))
        {
            terminal.WriteLine($"[dsf]   {stored.LastError}");
        }
    }

    /// <summary>Compares the stored charter against the live file and reports the drift.</summary>
    private static async Task<int> RunCharterStatusAsync(
        string product,
        CharterSource? source,
        ICliTerminal terminal,
        ICharterStore charterStore,
        CancellationToken cancellationToken)
    {
        var stored = await charterStore.GetCharterAsync(product, cancellationToken);
        terminal.WriteLine($"[dsf] charter {product}: {DriftLabel(stored, source?.Sha)}");
        if (stored is not null)
        {
            if (stored.LastSyncedAt is not null)
            {
                terminal.WriteLine($"[dsf]   last_synced_at={stored.LastSyncedAt:O}");
            }

            if (stored.Charter?.SourceSha is { Length: > 0 } storedSha)
            {
                terminal.WriteLine($"[dsf]   stored_sha={storedSha} ref={stored.Charter.SourceRef}");
            }

            if (!string.IsNullOrWhiteSpace(stored.LastError))
            {
                terminal.WriteLine($"[dsf]   last_error={stored.LastError}");
            }
        }

        if (source is null)
        {
            terminal.WriteLine($"[dsf]   note: {CharterMarkdown.CharterPath} could not be read.");
        }
        else
        {
            terminal.WriteLine($"[dsf]   file_sha={source.Sha}");
        }

        return Success;
    }

    private static string DriftLabel(StoredCharter? stored, string? liveSha) => (stored, liveSha) switch
    {
        (_, null) => "missing",
        (null, _) => "stale", // file present but nothing good stored yet -> run sync
        ({ Charter: null }, _) => "stale",
        ({ Status: CharterStatus.Invalid }, _) => "invalid",
        var (record, sha) when record!.Charter!.SourceSha != sha => "stale",
        _ => "ok",
    };

    private static string StatusValue(CharterStatus status) => status switch
    {
        CharterStatus.Ok => "OK",
        CharterStatus.Stale => "STALE",
        CharterStatus.Missing => "MISSING",
        _ => "INVALID",
    };

    private static string InitialCharter(string product, ICliTerminal terminal)
    {
        var vision = RequiredAnswer(terminal, "Vision: ");
        var targetUsers = RequiredAnswer(terminal, "Target users: ");
        var goals = RequiredAnswer(terminal, "Goals: ");
        var nonGoals = RequiredAnswer(terminal, "Non-goals: ");
        var metrics = RequiredAnswer(terminal, "Success metrics: ");
        var constraints = RequiredAnswer(terminal, "Constraints: ");
        return $"""
            <!-- dsf:charter schema_version=1 -->
            # Product Charter: {product}

            ## Vision
            {vision}

            ## Target Users
            {targetUsers}

            ## Goals
            - {goals}

            ## Non-Goals
            - {nonGoals}

            ## Success Metrics
            - {metrics}

            ## Constraints
            {constraints}

            ## Glossary
            - Charter: this product's human-owned intent document
            """;
    }

    private static string RequiredAnswer(ICliTerminal terminal, string prompt) =>
        terminal.Prompt(prompt)?.Trim() is { Length: > 0 } answer
            ? answer
            : throw new InvalidOperationException($"charter init requires an answer for '{prompt[..^2]}'.");

    private static ProductRecord ProductRecordFor(InstanceDefinition definition) =>
        new(
            definition.Product.Key,
            definition.GitHub.FullName(),
            GovernanceSettings.DefaultLabelTaxonomy,
            string.Empty,
            [],
            [],
            string.Empty,
            definition.Governance.ConfidenceThreshold);

    /// <summary>
    /// Builds the runtime index entries published to owner App Configuration for
    /// this instance. Must carry every setting <c>RuntimeSettingsComposer</c> (the
    /// .NET runtime host, in <c>Dsf.Core.Runtime</c>) requires -- including the
    /// Azure OpenAI deployment names and the GitHub App identifiers -- so a runtime
    /// command resolving configuration through the owner authority (rather than
    /// local env vars) can compose a complete <c>RuntimeSettings</c> for this
    /// product. Internal (not private) so its contents are covered by a focused
    /// unit test instead of only exercised indirectly through the full GitHub
    /// provisioning flow.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RuntimeIndexValues(
        InstanceDefinition definition,
        string productEndpoint)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DSF_PRODUCT"] = definition.Product.Key,
            ["GITHUB_REPOSITORY"] = definition.GitHub.FullName(),
            ["AZURE_APPCONFIG_ENDPOINT"] = productEndpoint,
        };
        foreach (var (key, value) in definition.Azure.Outputs)
        {
            if (key.EndsWith("Endpoint", StringComparison.Ordinal))
            {
                values[$"AZURE_{key[..^"Endpoint".Length].ToUpperInvariant()}_ENDPOINT"] = value;
            }
        }

        if (definition.Azure.Outputs.TryGetValue("openaiDeployment", out var openAiDeployment)
            && !string.IsNullOrWhiteSpace(openAiDeployment))
        {
            values["AZURE_OPENAI_DEPLOYMENT"] = openAiDeployment;
        }

        if (definition.Azure.Outputs.TryGetValue("openaiEmbeddingDeployment", out var openAiEmbeddingDeployment)
            && !string.IsNullOrWhiteSpace(openAiEmbeddingDeployment))
        {
            values["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = openAiEmbeddingDeployment;
        }

        if (definition.Azure.Outputs.TryGetValue("keyVaultUri", out var keyVaultUri)
            && !string.IsNullOrWhiteSpace(keyVaultUri))
        {
            values["AZURE_KEYVAULT_URI"] = keyVaultUri;
        }

        if (!string.IsNullOrWhiteSpace(definition.GitHub.AppId))
        {
            values["GITHUB_APP_ID"] = definition.GitHub.AppId;
        }

        if (!string.IsNullOrWhiteSpace(definition.GitHub.InstallationId))
        {
            values["GITHUB_INSTALLATION_ID"] = definition.GitHub.InstallationId;
        }

        if (!string.IsNullOrWhiteSpace(definition.GitHub.PrivateKeySecretName))
        {
            values["GITHUB_APP_PRIVATE_KEY_SECRET"] = definition.GitHub.PrivateKeySecretName;
        }

        return values;
    }

    private static Option<string> RequiredStringOption(string name, string description)
    {
        var option = StringOption(name, description);
        option.Required = true;
        return option;
    }

    private static Option<string> StringOption(string name, string description, string? defaultValue = null, params string[] acceptedValues)
    {
        var option = new Option<string>(name)
        {
            Description = description,
        };
        if (defaultValue is not null)
        {
            option.DefaultValueFactory = _ => defaultValue;
        }
        if (acceptedValues.Length > 0)
        {
            option.AcceptOnlyFromAmong(acceptedValues);
        }
        return option;
    }

    private static Option<bool> BoolOption(string name, string description) =>
        new(name) { Description = description };

    private static Option<int?> IntOption(string name, string description, int? defaultValue = null)
    {
        var option = new Option<int?>(name) { Description = description };
        if (defaultValue is not null)
        {
            option.DefaultValueFactory = _ => defaultValue;
        }
        return option;
    }

    private static Option<double?> DoubleOption(string name, string description) =>
        new(name) { Description = description };

    private static void AddOptions(Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }
    }
}
