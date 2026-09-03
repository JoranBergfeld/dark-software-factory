using System.CommandLine;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// The .NET runtime host's command grammar and entrypoint dispatch. Mirrors the
/// Python <c>feature-council/src/dsf/runtime/control.py</c> verb surface (<c>run</c>,
/// <c>sweep</c>, <c>serve-orchestrator</c>, <c>serve-agent --kind</c>) so an operator
/// can run either runtime the same way. Every verb composes
/// <see cref="RuntimeSettings"/> from the existing env var names before doing
/// anything else; a missing required setting names every unset requirement and
/// exits non-zero rather than proceeding. Once settings validate, the verb does its
/// real work (see <see cref="RuntimeVerbs"/>): drives the conveyor, sweeps the
/// configured source agent roster, or serves the orchestrator/agent host. A verb
/// whose dependencies cannot be composed reports the settings that are unset
/// rather than running a line that can neither gather, file, nor persist.
/// </summary>
public static class RuntimeCliApplication
{
    private const int Success = 0;
    private const int Failure = 1;

    public static async Task<int> InvokeAsync(string[] args, CancellationToken cancellationToken) =>
        await InvokeAsync(args, RealEnvironment(), Console.Out, Console.Error, cancellationToken);

    public static async Task<int> InvokeAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken) =>
        await InvokeAsync(args, env, stdout, stderr, dependencies: null, cancellationToken);

    public static async Task<int> InvokeAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        RuntimeDependencies? dependencies,
        CancellationToken cancellationToken)
    {
        dependencies ??= RuntimeDependencies.Production(env);
        var root = BuildRootCommand(env, stdout, stderr, dependencies);
        var parseResult = root.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: cancellationToken);
        return parseResult.Errors.Count > 0 ? 2 : exitCode;
    }

    /// <summary>Builds the root command with no wiring, for command-grammar assertions.</summary>
    public static RootCommand BuildRootCommand() => BuildRootCommand(
        new Dictionary<string, string?>(), Console.Out, Console.Error, RuntimeDependencies.Production());

    private static RootCommand BuildRootCommand(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        RuntimeDependencies dependencies)
    {
        var root = new RootCommand("Dark Software Factory — runtime host (run/sweep/serve-orchestrator/serve-agent)");
        root.Subcommands.Add(BuildRunCommand(env, stdout, stderr, dependencies));
        root.Subcommands.Add(BuildSweepCommand(env, stdout, stderr, dependencies));
        root.Subcommands.Add(BuildServeOrchestratorCommand(env, stdout, stderr, dependencies));
        root.Subcommands.Add(BuildServeAgentCommand(env, stdout, stderr, dependencies));
        return root;
    }

    private static Command BuildRunCommand(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        RuntimeDependencies dependencies)
    {
        var signal = StringOption("--signal", "path to a signal JSON file");
        var dryRun = BoolOption("--dry-run", "run the line but skip filing");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("run", "run the intake line for one signal (runtime)");
        AddOptions(command, signal, dryRun, product);
        command.SetAction((parseResult, cancellationToken) => RunVerb(
            env,
            stdout,
            stderr,
            parseResult.GetValue(product),
            dependencies,
            (settings, token) => RuntimeVerbs.RunAsync(
                settings, parseResult.GetValue(signal), parseResult.GetValue(dryRun), dependencies, token),
            cancellationToken));
        return command;
    }

    private static Command BuildSweepCommand(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        RuntimeDependencies dependencies)
    {
        var dryRun = BoolOption("--dry-run", "sweep the line but skip filing");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("sweep", "sweep enabled source agents once (runtime)");
        AddOptions(command, dryRun, product);
        command.SetAction((parseResult, cancellationToken) => RunVerb(
            env,
            stdout,
            stderr,
            parseResult.GetValue(product),
            dependencies,
            (settings, token) => RuntimeVerbs.SweepAsync(
                settings, parseResult.GetValue(dryRun), dependencies, token),
            cancellationToken));
        return command;
    }

    private static Command BuildServeOrchestratorCommand(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        RuntimeDependencies dependencies)
    {
        var loop = BoolOption("--loop", "sweep continuously");
        var interval = IntOption("--interval", "seconds between sweeps");
        var host = StringOption("--host", "bind host", RuntimeVerbs.DefaultHost);
        var port = IntOption("--port", "bind port", RuntimeVerbs.DefaultPort);
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-orchestrator", "run the orchestrator worker (runtime)");
        AddOptions(command, loop, interval, host, port, product);
        command.SetAction((parseResult, cancellationToken) => ServeVerb(
            env,
            stderr,
            parseResult.GetValue(product),
            dependencies,
            (settings, token) => RuntimeVerbs.ServeOrchestratorAsync(
                settings,
                dependencies,
                parseResult.GetValue(host) ?? RuntimeVerbs.DefaultHost,
                parseResult.GetValue(port) ?? RuntimeVerbs.DefaultPort,
                parseResult.GetValue(loop)
                    ? PeriodicSweepService.ResolveInterval(parseResult.GetValue(interval), env)
                    : null,
                token),
            cancellationToken));
        return command;
    }

    private static Command BuildServeAgentCommand(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        RuntimeDependencies dependencies)
    {
        var kind = StringOption("--kind", "source agent kind", "sentry");
        var host = StringOption("--host", "bind host", RuntimeVerbs.DefaultHost);
        var port = IntOption("--port", "bind port", RuntimeVerbs.DefaultPort);
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-agent", "serve a source agent over A2A (runtime)");
        AddOptions(command, kind, host, port, product);
        // serve-agent must still validate required runtime config before validating
        // --kind, exactly like every other verb -- it must never report a kind
        // result for a product that isn't configured yet.
        command.SetAction((parseResult, cancellationToken) => ServeVerb(
            env,
            stderr,
            parseResult.GetValue(product),
            dependencies,
            (settings, token) => RuntimeVerbs.ServeAgentAsync(
                settings,
                parseResult.GetValue(kind) ?? "sentry",
                dependencies,
                parseResult.GetValue(host) ?? RuntimeVerbs.DefaultHost,
                parseResult.GetValue(port) ?? RuntimeVerbs.DefaultPort,
                token),
            cancellationToken));
        return command;
    }

    /// <summary>
    /// Composes <see cref="RuntimeSettings"/> and, once they validate, runs the
    /// verb's conveyor operation, printing the finished run's summary. A run that
    /// ended in <see cref="RunStatus.Error"/> (for example, the filing boundary
    /// reached with no filer wired) still prints everything the line did before
    /// failing, then exits non-zero.
    /// </summary>
    private static async Task<int> RunVerb(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        string? productOption,
        RuntimeDependencies dependencies,
        Func<RuntimeSettings, CancellationToken, Task<ConveyorRun>> operation,
        CancellationToken cancellationToken)
    {
        var settings = await ComposeSettings(env, stderr, productOption, dependencies, cancellationToken);
        if (settings is null)
        {
            return Failure;
        }

        ConveyorRun run;
        try
        {
            run = await operation(settings, cancellationToken);
        }
        catch (RuntimeVerbException exception)
        {
            stderr.WriteLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        var summary = RuntimeRunSummary.From(run);
        foreach (var line in summary.ToLines())
        {
            stdout.WriteLine(line);
        }

        if (run.Status != RunStatus.Error)
        {
            return Success;
        }

        // The station that failed is pinned on the run; a later telemetry or
        // persistence failure may have audited itself afterwards, and reporting
        // that instead would send the operator after the wrong cause.
        var reason = run.FailureReason ?? run.Audit[^1].Message;
        stderr.WriteLine($"[dsf] error: run {run.Id} ended in error: {reason}");
        return Failure;
    }

    /// <summary>Composes settings and, once they validate, serves the verb's host.</summary>
    private static async Task<int> ServeVerb(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stderr,
        string? productOption,
        RuntimeDependencies dependencies,
        Func<RuntimeSettings, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var settings = await ComposeSettings(env, stderr, productOption, dependencies, cancellationToken);
        if (settings is null)
        {
            return Failure;
        }

        try
        {
            await operation(settings, cancellationToken);
        }
        catch (RuntimeVerbException exception)
        {
            stderr.WriteLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        return Success;
    }

    private static async Task<RuntimeSettings?> ComposeSettings(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stderr,
        string? productOption,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RuntimeSettingsComposer.ComposeAsync(
                env, productOption, dependencies.OwnerRuntimeIndexReader, cancellationToken);
        }
        catch (RuntimeConfigurationException exception)
        {
            stderr.WriteLine($"[dsf] error: {exception.Message}");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string?> RealEnvironment()
    {
        var result = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            result[(string)entry.Key] = entry.Value as string;
        }

        return result;
    }

    private static Option<string> StringOption(string name, string description, string? defaultValue = null)
    {
        var option = new Option<string>(name) { Description = description };
        if (defaultValue is not null)
        {
            option.DefaultValueFactory = _ => defaultValue;
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

    private static void AddOptions(Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }
    }
}
