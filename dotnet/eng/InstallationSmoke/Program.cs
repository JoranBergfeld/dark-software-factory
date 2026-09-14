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

    Console.WriteLine("Installed payload smoke passed: five runtime verbs, ARM dependency closure, provisioning preview, bootstrap, preflight.");
    return 0;
}
finally
{
    scratch.Delete(recursive: true);
}

async Task<(int ExitCode, string Output)> Run(params string[] arguments)
{
    var start = new ProcessStartInfo(cli)
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
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not launch {cli}");
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
