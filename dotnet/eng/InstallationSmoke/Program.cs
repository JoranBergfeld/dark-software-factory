using System.Diagnostics;
using System.Text.Json;

// This executable doubles as an offline Azure CLI on the child process PATH.
// It never forwards commands to Azure; unexpected calls fail closed.
if (args.FirstOrDefault() != "--cli")
{
    return FakeAzure(args);
}

if (args.Length != 4 || args[2] != "--payload")
{
    throw new ArgumentException("Usage: --cli <installed executable> --payload <installation directory>");
}

var cli = Path.GetFullPath(args[1]);
var payload = Path.GetFullPath(args[3]);
var scratch = Directory.CreateTempSubdirectory("dsf-installed-smoke-");
try
{
    var version = File.ReadAllText(Path.Combine(payload, "dsf-bundle.version")).Trim();
    Require(!string.IsNullOrWhiteSpace(version), "Missing bundle version.");
    foreach (var assembly in new[] { Path.Combine(payload, "dsf.dll"), Path.Combine(payload, "runtime", "dsf-runtime.dll") })
    {
        if (File.Exists(assembly))
        {
            var assemblyVersion = FileVersionInfo.GetVersionInfo(assembly).ProductVersion;
            Require(assemblyVersion?.Split('+')[0] == version.Split('+')[0],
                $"Assembly version does not match bundle: {assembly}");
        }
    }
    var nativeHost = Path.Combine(payload, "runtime", OperatingSystem.IsWindows() ? "dsf-runtime.exe" : "dsf-runtime");

    var templates = new Dictionary<string, int>
    {
        ["owner-keyvault"] = 0,
        ["owner-secrets"] = 0,
        ["main"] = 1,
        ["sre-agent"] = 5,
        ["copy-owner-secret"] = 1,
        ["onboard-foundation"] = 0,
        ["product-github-private-key"] = 0,
        ["onboard-github-auth"] = 0,
        ["onboard-telemetry"] = 0,
        ["onboard-models"] = 0,
        ["onboard-source"] = 0,
        ["onboard-supplemental"] = 0,
        ["onboard-preview-workloads"] = 0,
    };
    foreach (var (name, minimumNestedDeployments) in templates)
    {
        var template = Path.Combine(payload, "assets", "infra", $"{name}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(template));
        Require(document.RootElement.TryGetProperty("$schema", out _), $"ARM schema missing: {template}");
        Require(document.RootElement.TryGetProperty("resources", out _), $"ARM resources missing: {template}");
        var nested = ValidateClosure(document.RootElement);
        Require(nested >= minimumNestedDeployments, $"Missing compiled modules: {template}");
    }

    var reviewHelp = await Run("onboard", "decide", "review", "--help");
    Require(reviewHelp.ExitCode == 0 && reviewHelp.Output.Contains("--issue-automation", StringComparison.Ordinal)
        && reviewHelp.Output.Contains("--owner-appconfig-endpoint", StringComparison.Ordinal),
        $"Installed attachment review is unavailable: {reviewHelp.Output}");
    var incompleteReview = await Run("onboard", "decide", "review", "--json");
    Require(incompleteReview.ExitCode == 1 && incompleteReview.Output.Contains("--repo is required", StringComparison.Ordinal),
        $"Redirected attachment review did not require an explicit repository: {incompleteReview.Output}");

    foreach (var action in new[] { "plan", "reserve", "provision", "github", "status", "resume" })
    {
        var help = await Run("onboard", "decide", action, "--help");
        var required = action is "plan" or "reserve" ? "--reviewed-fingerprint" : "--owner-appconfig-endpoint";
        Require(help.ExitCode == 0 && help.Output.Contains(required, StringComparison.Ordinal),
            $"Installed onboarding command is unavailable: {help.Output}");
        var incomplete = await Run("onboard", "decide", action, "--json");
        Require(incomplete.ExitCode == 1 && incomplete.Output.Contains("\"state\": \"Failed\"", StringComparison.Ordinal),
            $"Installed onboarding command accepted incomplete explicit inputs: {incomplete.Output}");
    }

    var modelsHelp = await Run("onboard", "decide", "models", "--help");
    Require(modelsHelp.ExitCode == 0 && modelsHelp.Output.Contains("--model-topology", StringComparison.Ordinal)
        && modelsHelp.Output.Contains("--verify-only", StringComparison.Ordinal),
        "Installed models preparation/proof grammar is unavailable.");
    var incompleteModels = await Run("onboard", "decide", "models", "--json");
    Require(incompleteModels.ExitCode == 1 && incompleteModels.Output.Contains("--model-topology required", StringComparison.Ordinal)
        && incompleteModels.Output.Contains("\"state\": \"Blocked\"", StringComparison.Ordinal),
        "Installed models command accepted incomplete redirected inputs.");
    var modelsAsset = Path.Combine(payload, "assets", "infra", "onboard-models.json");
    var modelsBytes = File.ReadAllBytes(modelsAsset);
    File.Delete(modelsAsset);
    try
    {
        File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
        var missingModels = await Run("onboard", "decide", "models", "--owner-appconfig-endpoint", "https://owner.azconfig.io",
            "--product", "packaging-smoke", "--tenant", "11111111-1111-1111-1111-111111111111",
            "--subscription", "22222222-2222-2222-2222-222222222222", "--model-topology", "{}", "--json");
        Require(missingModels.ExitCode == 1 && missingModels.Output.Contains("onboard-models.json", StringComparison.Ordinal),
            "Installed model preparation must identify the missing asset.");
        Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0,
            "Model asset failure reached Azure authentication.");
    }
    finally { File.WriteAllBytes(modelsAsset, modelsBytes); }

    var modelsHost = await Run("serve-models");
    Require(modelsHost.ExitCode == 1 && modelsHost.Output.Contains("DSF_FACTORY_PROFILE_PIN", StringComparison.Ordinal)
        && modelsHost.Output.Contains("AZURE_CLIENT_ID", StringComparison.Ordinal)
        && !modelsHost.Output.Contains("AZURE_OPENAI", StringComparison.Ordinal),
        "Installed CLI did not reach the bundled isolated model host.");

    var sourceHelp = await Run("onboard", "decide", "source", "--help");
    Require(sourceHelp.ExitCode == 0 && sourceHelp.Output.Contains("--new-snapshot", StringComparison.Ordinal)
        && sourceHelp.Output.Contains("--source-audience", StringComparison.Ordinal), "Installed source grammar is unavailable.");
    var incompleteSource = await Run("onboard", "decide", "source", "--json");
    Require(incompleteSource.ExitCode == 1 && incompleteSource.Output.Contains("\"state\": \"Blocked\"", StringComparison.Ordinal),
        "Installed source command accepted incomplete redirected inputs.");
    var sourceAsset = Path.Combine(payload, "assets", "infra", "onboard-source.json");
    var sourceBytes = File.ReadAllBytes(sourceAsset);
    File.Delete(sourceAsset);
    try
    {
        File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
        var missingSource = await Run("onboard", "decide", "source", "--prepare", "--owner-appconfig-endpoint", "https://owner.azconfig.io",
            "--product", "packaging-smoke", "--tenant", "11111111-1111-1111-1111-111111111111",
            "--subscription", "22222222-2222-2222-2222-222222222222", "--json");
        Require(missingSource.ExitCode == 1 && missingSource.Output.Contains("onboard-source.json", StringComparison.Ordinal),
            "Installed source preparation must identify its missing asset.");
        Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0, "Source asset failure reached Azure authentication.");
    }
    finally { File.WriteAllBytes(sourceAsset, sourceBytes); }
    var sourceHost = await Run("serve-source");
    Require(sourceHost.ExitCode == 1 && sourceHost.Output.Contains("DSF_FACTORY_PROFILE_PIN", StringComparison.Ordinal)
        && sourceHost.Output.Contains("AZURE_CLIENT_ID", StringComparison.Ordinal)
        && !sourceHost.Output.Contains("AZURE_OPENAI", StringComparison.Ordinal), "Installed source host is not isolated.");

    File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
    foreach (var verb in new[] { "serve-preview", "serve-source-context", "serve-preview-models",
        "serve-preview-telemetry", "serve-preview-supplemental" })
    {
        var previewHost = await Run(verb);
        Require(previewHost.ExitCode == 1 && new[] { "DSF_FACTORY_PROFILE_PIN", "AZURE_CLIENT_ID",
            "DSF_PRODUCT", "AZURE_APPCONFIG_ENDPOINT" }.All(name => previewHost.Output.Contains(name, StringComparison.Ordinal))
            && !previewHost.Output.Contains("AZURE_OPENAI", StringComparison.Ordinal),
            $"Installed CLI did not reach the protected preview runtime: {verb}: {previewHost.Output}");
    }
    Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0,
        "Preview hosts reached Azure before their required settings were present.");

    var previewHelp = await Run("onboard", "decide", "preview", "--help");
    Require(previewHelp.ExitCode == 0 && new[] { "--configure", "--evaluate", "--evaluation", "--operation", "--approve", "--resume" }
        .All(option => previewHelp.Output.Contains(option, StringComparison.Ordinal)), "Installed preview workflow grammar is incomplete.");
    foreach (var action in new[] { "--configure", "--evaluate", "--evaluation" })
    {
        var incompletePreview = await Run("onboard", "decide", "preview", action, "--json");
        Require(incompletePreview.ExitCode == 1 && incompletePreview.Output.Contains("Blocked", StringComparison.Ordinal)
            && incompletePreview.Output.Contains("required", StringComparison.Ordinal),
            $"Installed preview accepted incomplete redirected inputs: {incompletePreview.Output}");
    }
    Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0, "Incomplete preview reached Azure.");

    var auditHelp = await Run("onboard", "decide", "audit", "--help");
    Require(auditHelp.ExitCode == 0 && new[] { "--export", "--verify-export", "--approve-policy", "--expire-analysis", "--page" }
        .All(option => auditHelp.Output.Contains(option, StringComparison.Ordinal)), "Installed audit grammar is incomplete.");
    foreach (var action in new[] { Array.Empty<string>(), new[] { "--export", "https://github.com/public/export" },
        new[] { "--approve-policy" }, new[] { "--verify-export", "/private/export" } })
    {
        var incompleteAudit = await Run(["onboard", "decide", "audit", .. action, "--json"]);
        Require(incompleteAudit.ExitCode == 1 && incompleteAudit.Output.Contains("\"state\": \"Blocked\"", StringComparison.Ordinal)
            && incompleteAudit.Output.Contains("required", StringComparison.Ordinal), "Installed audit accepted incomplete inputs.");
    }
    Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0, "Incomplete audit reached Azure.");

    var pauseHelp = await Run("onboard", "decide", "pause", "--help");
    Require(pauseHelp.ExitCode == 0 && new[] { "--request", "--operation", "--owner-appconfig-endpoint" }
        .All(option => pauseHelp.Output.Contains(option, StringComparison.Ordinal)), "Installed deliberate pause grammar is incomplete.");
    foreach (var action in new[] { Array.Empty<string>(), new[] { "--request" },
        new[] { "--request", "--operation", Guid.Empty.ToString() } })
    {
        var incompletePause = await Run(["onboard", "decide", "pause", .. action, "--json"]);
        Require(incompletePause.ExitCode == 1 && incompletePause.Output.Contains("\"state\": \"Blocked\"", StringComparison.Ordinal)
            && incompletePause.Output.Contains("required", StringComparison.Ordinal), "Installed pause accepted incomplete explicit inputs.");
    }
    Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0, "Incomplete pause reached Azure.");

    var activationHelp = await Run("onboard", "decide", "activate", "--help");
    Require(activationHelp.ExitCode == 0 && new[] { "--operation", "--approve", "--status", "--owner-appconfig-endpoint" }
        .All(option => activationHelp.Output.Contains(option, StringComparison.Ordinal)), "Installed operator activation review grammar is incomplete.");
    foreach (var action in new[] { Array.Empty<string>(), new[] { "--status" },
        new[] { "--operation", Guid.Empty.ToString() }, new[] { "--approve", "true" } })
    {
        var incompleteActivation = await Run(["onboard", "decide", "activate", .. action, "--json"]);
        Require(incompleteActivation.ExitCode == 1 && incompleteActivation.Output.Contains("\"state\": \"Blocked\"", StringComparison.Ordinal)
            && incompleteActivation.Output.Contains("\"isActivated\": false", StringComparison.Ordinal)
            && incompleteActivation.Output.Contains("\"historicallyCompleted\": false", StringComparison.Ordinal),
            "Installed activation accepted incomplete inputs or confused approval with completion.");
    }
    Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0, "Incomplete activation reached Azure.");

    var inferHelp = await Run("onboard", "decide", "infer", "--help");
    Require(inferHelp.ExitCode == 0 && inferHelp.Output.Contains("--new-analysis", StringComparison.Ordinal)
        && inferHelp.Output.Contains("--status-only", StringComparison.Ordinal), "Installed charter inference grammar is unavailable.");
    File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
    var incompleteInfer = await Run("onboard", "decide", "infer", "--json");
    Require(incompleteInfer.ExitCode == 1 && incompleteInfer.Output.Contains("\"state\": \"Blocked\"", StringComparison.Ordinal)
        && incompleteInfer.Output.Contains("required", StringComparison.Ordinal), "Installed inference accepted missing explicit locators.");
    Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0, "Incomplete inference reached Azure.");

    var charterHelp = await Run("onboard", "decide", "charter", "--help");
    Require(charterHelp.ExitCode == 0 && new[] { "--publish", "--adopt", "--existing", "--approve", "--status-only" }
        .All(option => charterHelp.Output.Contains(option, StringComparison.Ordinal)),
        "Installed reviewed-charter publication/adoption grammar is unavailable.");
    File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
    foreach (var action in new[] { "--publish", "--adopt", "--status-only" })
    {
        var incompleteCharter = await Run("onboard", "decide", "charter", action, "--json");
        Require(incompleteCharter.ExitCode == 1 && incompleteCharter.Output.Contains("\"state\": \"Blocked\"", StringComparison.Ordinal)
            && incompleteCharter.Output.Contains("required", StringComparison.Ordinal),
            "Installed charter command accepted incomplete redirected inputs.");
    }
    Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0, "Incomplete charter command reached Azure.");

    var foundationAsset = Path.Combine(payload, "assets", "infra", "onboard-foundation.json");
    var foundationBytes = File.ReadAllBytes(foundationAsset);
    File.Delete(foundationAsset);
    try
    {
        File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
        var missingFoundation = await Run("onboard", "decide", "provision", "--owner-appconfig-endpoint", "https://owner.azconfig.io",
            "--product", "packaging-smoke", "--tenant", "11111111-1111-1111-1111-111111111111",
            "--subscription", "22222222-2222-2222-2222-222222222222", "--reviewed-fingerprint", new string('a', 64), "--json");
        Require(missingFoundation.ExitCode == 1 && missingFoundation.Output.Contains("onboard-foundation.json", StringComparison.Ordinal)
            && missingFoundation.Output.Contains("\"state\": \"Failed\"", StringComparison.Ordinal),
            "Installed foundation asset preflight did not fail explicitly before provider access.");
        Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0,
            "Foundation asset failure reached Azure authentication.");
    }
    finally
    {
        File.WriteAllBytes(foundationAsset, foundationBytes);
    }

    foreach (var name in new[] { "product-github-private-key.json", "onboard-github-auth.json" })
    {
        var asset = Path.Combine(payload, "assets", "infra", name);
        var content = File.ReadAllBytes(asset);
        File.Delete(asset);
        try
        {
            File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
            var missingAuthority = await Run("onboard", "decide", "github", "--owner-appconfig-endpoint", "https://owner.azconfig.io",
                "--product", "packaging-smoke", "--tenant", "11111111-1111-1111-1111-111111111111",
                "--subscription", "22222222-2222-2222-2222-222222222222", "--json");
            Require(missingAuthority.ExitCode == 1 && missingAuthority.Output.Contains(name, StringComparison.Ordinal),
                "Installed GitHub authority must identify the missing asset.");
            Require(File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Length == 0,
                "GitHub authority asset preflight reached Azure authentication.");
        }
        finally { File.WriteAllBytes(asset, content); }
    }

    var authentication = await Run("serve-github-auth");
    Require(authentication.ExitCode == 1 && authentication.Output.Contains("DSF_FACTORY_PROFILE_PIN", StringComparison.Ordinal)
        && authentication.Output.Contains("AZURE_CLIENT_ID", StringComparison.Ordinal),
        "Installed CLI did not reach the bundled dedicated authentication host.");

    var signalPath = Path.Combine(scratch.FullName, "onboarding-signal.json");
    File.WriteAllText(signalPath, """{"product_hints":"packaging-smoke","source_kinds":["azuremonitor"]}""");
    var inactiveEnvironment = new Dictionary<string, string>
    {
        ["AZURE_APPCONFIG_ENDPOINT"] = "https://dsfo-11111111111111111111111111111111.azconfig.io",
        ["AZURE_COSMOS_ENDPOINT"] = "https://unused.documents.azure.com",
        ["AZURE_OPENAI_ENDPOINT"] = "https://unused.openai.azure.com",
        ["AZURE_OPENAI_DEPLOYMENT"] = "unused",
        ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "unused",
        ["AZURE_TOKEN_CREDENTIALS"] = "AzureCliCredential",
        ["DSF_CONFIRM_LIVE_FILING"] = "true",
    };
    foreach (var arguments in new[]
    {
        new[] { "run", "--product", "packaging-smoke", "--signal", signalPath },
        new[] { "sweep", "--product", "packaging-smoke" },
        new[] { "poll-outcomes", "--product", "packaging-smoke", "--dry-run" },
        new[] { "serve-orchestrator", "--product", "packaging-smoke", "--loop" },
        new[] { "sweep", "resume", "--product", "packaging-smoke" },
        new[] { "sweep", "interval", "1", "--product", "packaging-smoke" },
        new[] { "sweep", "pause", "--product", "packaging-smoke" },
    })
    {
        var inactive = arguments is ["sweep", "resume" or "interval" or "pause", ..]
            ? await RunRuntimeWithEnvironment(inactiveEnvironment, arguments)
            : await RunWithEnvironment(inactiveEnvironment, arguments);
        Require(inactive.ExitCode == 1 && inactive.Output.Contains("Runtime admission denied:", StringComparison.Ordinal)
            && inactive.Output.Contains("unreadable", StringComparison.Ordinal),
            $"Installed runtime did not fail closed on unreadable governance: {inactive.Output}");
    }
    var admissionReads = File.ReadAllLines(Path.Combine(scratch.FullName, "azure.log"));
    Require(admissionReads.Length >= 7 && admissionReads.All(line => line.Contains("account get-access-token", StringComparison.Ordinal)),
        "Installed admission must reach only the offline credential transport, never a resource operation.");

    foreach (var verb in new[] { "run", "sweep", "serve-orchestrator", "serve-agent", "poll-outcomes" })
    {
        var result = await Run(verb, "--product", "packaging-smoke");
        Require(result.ExitCode == 1 && result.Output.Contains("missing required Azure runtime configuration:", StringComparison.Ordinal)
            && result.Output.Contains("AZURE_APPCONFIG_ENDPOINT", StringComparison.Ordinal),
            $"{verb} did not reach runtime configuration validation: {result.Output}");
    }

    var host = File.Exists(nativeHost) ? nativeHost : Path.Combine(payload, "runtime", "dsf-runtime.dll");
    var savedHost = host + ".smoke-backup";
    File.Move(host, savedHost);
    try
    {
        var broken = await Run("sweep", "--product", "packaging-smoke");
        Require(broken.ExitCode != 0 && broken.Output.Contains("dsf-runtime", StringComparison.Ordinal)
            && !broken.Output.Contains("missing required Azure runtime configuration:", StringComparison.Ordinal),
            $"Missing runtime was not reported: {broken.Output}");
    }
    finally
    {
        File.Move(savedHost, host);
    }

    var plan = await Run("new", "--product", "packaging-smoke", "--owner", "offline-owner", "--dry-run");
    Require(plan.ExitCode == 0, $"Product preview failed: {plan.Output}");
    Require(plan.Output.Contains(Path.Combine(payload, "assets", "infra", "main.json"), StringComparison.Ordinal),
        $"Product provisioning did not resolve the installed template: {plan.Output}");

    var bootstrap = await Run("bootstrap", "--app-name", "dsf-smoke", "--resource-group", "rg-dsf-smoke",
        "--appconfig-name", "dsf-smoke-config", "--keyvault-name", "dsf-smoke-vault", "--yes");
    Require(bootstrap.ExitCode == 1 && bootstrap.Output.Contains("DSF_SMOKE_STOP_AFTER_TEMPLATE", StringComparison.Ordinal),
        $"Bootstrap did not deploy its installed template through the Azure double: {bootstrap.Output}");
    var log = File.ReadAllLines(Path.Combine(scratch.FullName, "azure.log"));
    Require(log.Any(line => line.Contains("owner-keyvault.json", StringComparison.Ordinal)),
        "Bootstrap never supplied the installed owner Key Vault template.");

    // Remove only a file in this disposable installation, then prove preflight
    // prevents even the first Azure command. Restore before archive/signing steps.
    var missing = Path.Combine(payload, "assets", "infra", "owner-secrets.json");
    var bytes = File.ReadAllBytes(missing);
    File.Delete(missing);
    try
    {
        File.WriteAllText(Path.Combine(scratch.FullName, "azure.log"), "");
        var broken = await Run("bootstrap", "--app-name", "dsf-smoke", "--resource-group", "rg-dsf-smoke",
            "--appconfig-name", "dsf-smoke-config", "--keyvault-name", "dsf-smoke-vault", "--yes");
        Require(broken.ExitCode != 0 && broken.Output.Contains("owner-secrets.json", StringComparison.Ordinal),
            $"Missing asset was not identified: {broken.Output}");
        Require(!File.ReadAllText(Path.Combine(scratch.FullName, "azure.log")).Contains("group create", StringComparison.Ordinal),
            "Bootstrap mutated Azure before validating assets.");
    }
    finally
    {
        File.WriteAllBytes(missing, bytes);
    }

    Console.WriteLine($"Installed payload smoke passed: repository review, boundary plan, reservation/foundation/GitHub/models/source/infer/charter/status/resume, inactive runtime admission, thirteen runtime verbs including five protected preview hosts, {templates.Count} ARM assets, provisioning preview, bootstrap, preflight.");
    return 0;
}
finally
{
    scratch.Delete(recursive: true);
}

Task<(int ExitCode, string Output)> Run(params string[] arguments) =>
    RunWithEnvironment(new Dictionary<string, string>(), arguments);

Task<(int ExitCode, string Output)> RunWithEnvironment(IReadOnlyDictionary<string, string> environment, params string[] arguments) =>
    RunExecutable(cli, environment, arguments);

Task<(int ExitCode, string Output)> RunRuntimeWithEnvironment(IReadOnlyDictionary<string, string> environment, params string[] arguments)
{
    var runtimeHost = Path.Combine(payload, "runtime", OperatingSystem.IsWindows() ? "dsf-runtime.exe" : "dsf-runtime");
    return File.Exists(runtimeHost)
        ? RunExecutable(runtimeHost, environment, arguments)
        : RunExecutable("dotnet", environment, [Path.Combine(payload, "runtime", "dsf-runtime.dll"), .. arguments]);
}

async Task<(int ExitCode, string Output)> RunExecutable(
    string executable, IReadOnlyDictionary<string, string> environment, string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        WorkingDirectory = scratch.FullName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    foreach (var key in start.Environment.Keys.ToArray())
    {
        if (key.StartsWith("DSF_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase)
            || key is "GH_TOKEN" or "GITHUB_TOKEN")
        {
            start.Environment.Remove(key);
        }
    }
    start.Environment["PATH"] = AppContext.BaseDirectory + Path.PathSeparator + start.Environment["PATH"];
    start.Environment["DSF_SMOKE_LOG"] = Path.Combine(scratch.FullName, "azure.log");
    start.Environment["HOME"] = scratch.FullName;
    start.Environment["USERPROFILE"] = scratch.FullName;
    start.Environment["DSF_SMOKE_ASSET_ROOT"] = Path.Combine(payload, "assets", "infra");
    foreach (var (key, value) in environment) start.Environment[key] = value;
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not launch {executable}");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
    try
    {
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}

static int FakeAzure(string[] arguments)
{
    var log = Environment.GetEnvironmentVariable("DSF_SMOKE_LOG")
        ?? throw new InvalidOperationException("The Azure double must only run inside InstallationSmoke.");
    File.AppendAllText(log, string.Join(" ", arguments) + Environment.NewLine);
    var command = string.Join(" ", arguments.Take(3));
    if (arguments.FirstOrDefault() is "--version" or "version")
    {
        Console.WriteLine("{\"azure-cli\":\"2.80.0\"}");
        return 0;
    }
    if (command.StartsWith("account show", StringComparison.Ordinal)
        || command == "ad signed-in-user show")
    {
        Console.WriteLine("00000000-0000-0000-0000-000000000001");
        return 0;
    }
    if (command.StartsWith("account get-access-token", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Offline governance credential denied; no Azure access token is returned.");
        return 1;
    }
    if (command.StartsWith("group create", StringComparison.Ordinal)
        || command.StartsWith("appconfig create", StringComparison.Ordinal))
    {
        Console.WriteLine("{}");
        return 0;
    }
    if (command == "deployment group create")
    {
        var index = Array.IndexOf(arguments, "--template-file");
        Require(index >= 0, "Expected --template-file in bootstrap deployment.");
        var path = arguments[index + 1];
        Require(Path.GetDirectoryName(path) == Environment.GetEnvironmentVariable("DSF_SMOKE_ASSET_ROOT"),
            $"Template resolved outside installed assets: {path}");
        using var template = JsonDocument.Parse(File.ReadAllText(path));
        ValidateClosure(template.RootElement);
        Console.WriteLine("{}");
        return 0;
    }
    if (command == "role assignment create")
    {
        Console.Error.WriteLine("DSF_SMOKE_STOP_AFTER_TEMPLATE");
        return 42;
    }
    Console.Error.WriteLine($"Unexpected offline Azure invocation: {command}");
    return 99;
}

static int ValidateClosure(JsonElement element)
{
    var nested = 0;
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            Require(property.Name != "templateLink", "Compiled ARM must not require external template links.");
            if (property.Name == "type" && property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() == "Microsoft.Resources/deployments")
            {
                nested++;
            }
            nested += ValidateClosure(property.Value);
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            nested += ValidateClosure(item);
        }
    }
    return nested;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
