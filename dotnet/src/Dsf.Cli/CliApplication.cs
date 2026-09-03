using System.CommandLine;
using Dsf.Core.Instances;

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
            GitHubRestProvisioningClient.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal)
        => await InvokeAsync(
            args,
            cancellationToken,
            terminal,
            GitHubRestProvisioningClient.FromEnvironment());

    internal static async Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ICliTerminal terminal,
        IGitHubProvisioningClient github)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CanceledExitCode;
        }

        var providedOptions = args
            .Where(arg => arg.StartsWith("--", StringComparison.Ordinal))
            .Select(arg => arg.Split('=', 2)[0])
            .ToHashSet();
        var root = BuildRootCommand(terminal, providedOptions, github);
        var parseResult = root.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: cancellationToken);
        return parseResult.Errors.Count > 0 ? 2 : exitCode;
    }

    internal static RootCommand BuildRootCommand() => BuildRootCommand(
        SystemCliTerminal.Detect(),
        new HashSet<string>(),
        new UnavailableGitHubProvisioningClient());

    private static RootCommand BuildRootCommand(
        ICliTerminal terminal,
        IReadOnlySet<string> providedOptions,
        IGitHubProvisioningClient github)
    {
        var root = new RootCommand("Dark Software Factory — factory CLI (create product instances)");
        root.Options.Remove(root.Options.Single(option => option.Name == "--version"));
        var helpOption = root.Options.Single(option => option.Name == "--help");
        helpOption.Aliases.Remove("-?");
        helpOption.Aliases.Remove("/?");
        helpOption.Aliases.Remove("/h");

        root.Subcommands.Add(BuildNewCommand(terminal, providedOptions, github));
        root.Subcommands.Add(BuildListCommand());
        root.Subcommands.Add(BuildOffboardCommand());
        root.Subcommands.Add(BuildBootstrapCommand());
        root.Subcommands.Add(BuildDeleteCommand("delete"));
        root.Subcommands.Add(BuildDeleteCommand("deprovision"));
        root.Subcommands.Add(BuildRunCommand());
        root.Subcommands.Add(BuildSweepCommand());
        root.Subcommands.Add(BuildServeOrchestratorCommand());
        root.Subcommands.Add(BuildServeAgentCommand());
        root.Subcommands.Add(BuildCharterCommand());

        return root;
    }

    private static Command BuildNewCommand(
        ICliTerminal terminal,
        IReadOnlySet<string> providedOptions,
        IGitHubProvisioningClient github)
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

        command.SetAction(parseResult =>
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

            var definition = BuildPlannedDefinition(
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
                try
                {
                    var result = GitHubProvisioningPlan.Build(definition)
                        .ExecuteAsync(github, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    InstanceDefinitions.Write(result.ApplyTo(definition), configRootValue ?? Directory.GetCurrentDirectory());
                    terminal.WriteLine($"[dsf] GitHub provisioning complete for {definition.GitHub.FullName()}.");
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or InstanceDefinitionException)
                {
                    terminal.WriteErrorLine($"[dsf] error: GitHub provisioning failed: {exception.Message}");
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
        try
        {
            var path = InstanceDefinitions.PathFor(root, product);
            return File.Exists(path) ? InstanceDefinitions.Read(path) : null;
        }
        catch (InstanceDefinitionException)
        {
            return null;
        }
    }

    private static string BuildNamePrefix(string value)
    {
        var chars = value.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(8).ToArray();
        return new string(chars).PadRight(8, 'x') + "0000";
    }

    private static Command BuildListCommand()
    {
        var json = BoolOption("--json", "emit the factory rows as JSON for scripting");
        var ownerAppConfigEndpoint = StringOption("--owner-appconfig-endpoint", "owner App Configuration endpoint");
        var command = new Command("list", "list provisioned product factories from the owner App Config index");
        command.Aliases.Add("ls");
        AddOptions(command, json, ownerAppConfigEndpoint);
        command.SetAction(parseResult =>
        {
            if (parseResult.GetValue(json))
            {
                Console.Out.WriteLine("[]");
            }
            else
            {
                Console.Out.WriteLine("[dsf] no provisioned product factories found.");
            }

            return Success;
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
        command.SetAction(parseResult =>
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
        command.SetAction(parseResult => RuntimeShell(parseResult.GetValue(product), "run"));
        return command;
    }

    private static Command BuildSweepCommand()
    {
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("sweep", "sweep enabled source agents once (runtime)");
        AddOptions(command, product);
        command.SetAction(parseResult => RuntimeShell(parseResult.GetValue(product), "sweep"));
        return command;
    }

    private static Command BuildServeOrchestratorCommand()
    {
        var loop = BoolOption("--loop", "sweep continuously");
        var interval = IntOption("--interval", "seconds between sweeps");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-orchestrator", "run the orchestrator worker (runtime)");
        AddOptions(command, loop, interval, product);
        command.SetAction(parseResult => RuntimeShell(parseResult.GetValue(product), "serve-orchestrator"));
        return command;
    }

    private static Command BuildServeAgentCommand()
    {
        var kind = StringOption("--kind", "source agent kind", "sentry");
        var host = StringOption("--host", "bind host", "0.0.0.0");
        var port = IntOption("--port", "bind port", 8080);
        var command = new Command("serve-agent", "serve a source agent over A2A (runtime)");
        AddOptions(command, kind, host, port);
        command.SetAction(_ =>
        {
            Console.Out.WriteLine("[dsf] serve-agent is not implemented in the .NET migration shell.");
            return Success;
        });
        return command;
    }

    private static Command BuildCharterCommand()
    {
        var command = new Command("charter", "manage the product charter (.dsf/charter.md)");
        command.Subcommands.Add(SimpleCharterCommand("init", "interview to draft a charter and open a PR"));
        command.Subcommands.Add(CharterImplementCommand());
        command.Subcommands.Add(CharterWatchCommand());
        command.Subcommands.Add(CharterSourceCommand("sync", "pull .dsf/charter.md (local file or --ref) into Cosmos"));
        command.Subcommands.Add(CharterSourceCommand("status", "show the stored charter status + drift"));
        return command;
    }

    private static Command SimpleCharterCommand(string name, string description)
    {
        var product = RequiredStringOption("--product", "product key");
        var command = new Command(name, description);
        AddOptions(command, product);
        command.SetAction(_ => CharterShell(name));
        return command;
    }

    private static Command CharterSourceCommand(string name, string description)
    {
        var product = RequiredStringOption("--product", "product key");
        var file = StringOption("--file", "path to a local charter file");
        var refOption = StringOption("--ref", "read the charter from this repo ref via the GitHub App");
        var command = new Command(name, description);
        AddOptions(command, product, file, refOption);
        command.SetAction(parseResult =>
        {
            if (parseResult.GetValue(file) is not null && parseResult.GetValue(refOption) is not null)
            {
                Console.Error.WriteLine("[dsf] error: --file and --ref cannot be used together.");
                return Failure;
            }

            return CharterShell(name);
        });
        return command;
    }

    private static Command CharterImplementCommand()
    {
        var product = RequiredStringOption("--product", "product key");
        var noWait = BoolOption("--no-wait", "file + assign only; do not watch");
        var timeout = DoubleOption("--timeout", "max seconds to wait");
        var pollInterval = DoubleOption("--poll-interval", "seconds between polls");
        var command = new Command("implement", "render the constitution + file the Spec Kit bootstrap issue");
        AddOptions(command, product, noWait, timeout, pollInterval);
        command.SetAction(_ => CharterShell("implement"));
        return command;
    }

    private static Command CharterWatchCommand()
    {
        var product = RequiredStringOption("--product", "product key");
        var issue = IntOption("--issue", "bootstrap issue number");
        var timeout = DoubleOption("--timeout", "max seconds to watch");
        var pollInterval = DoubleOption("--poll-interval", "seconds between polls");
        var command = new Command("watch", "watch the coding agent's build and request Copilot review when ready");
        AddOptions(command, product, issue, timeout, pollInterval);
        command.SetAction(_ => CharterShell("watch"));
        return command;
    }

    private static int MissingManifest(string product, string action)
    {
        Console.Error.WriteLine(
            $"[dsf] error: Instance manifest not found for product '{product}'. {action} config/instances/{product}.json.");
        return Failure;
    }

    private static int RuntimeShell(string? product, string verb)
    {
        product ??= Environment.GetEnvironmentVariable("DSF_PRODUCT");
        if (string.IsNullOrWhiteSpace(product))
        {
            Console.Error.WriteLine("[dsf] error: DSF_PRODUCT is required to scope the factory runtime (set DSF_PRODUCT=<product>).");
            return Failure;
        }

        Console.Out.WriteLine($"[dsf] {verb} is not implemented in the .NET migration shell for product={product}.");
        return Success;
    }

    private static int CharterShell(string verb)
    {
        Console.Out.WriteLine($"[dsf] charter {verb} is not implemented in the .NET migration shell.");
        return Success;
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
