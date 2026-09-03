using System.CommandLine;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// The .NET runtime host's command grammar and entrypoint dispatch. Mirrors the
/// Python <c>feature-council/src/dsf/runtime/control.py</c> verb surface (<c>run</c>,
/// <c>sweep</c>, <c>serve-orchestrator</c>, <c>serve-agent --kind</c>) so an operator
/// can run either runtime the same way. Every verb composes
/// <see cref="RuntimeSettings"/> from the existing env var names before doing
/// anything else; a missing required setting names every unset requirement and
/// exits non-zero rather than proceeding. The station pipeline itself (the
/// conveyor, source agents, filing) ships in #142/#143 -- once settings validate,
/// these verbs fail loudly rather than pretend to have run anything.
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
        await InvokeAsync(args, env, stdout, stderr, ownerRuntimeIndexReader: null, cancellationToken);

    public static async Task<int> InvokeAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        IOwnerRuntimeIndexReader? ownerRuntimeIndexReader,
        CancellationToken cancellationToken)
    {
        ownerRuntimeIndexReader ??= new AzureAppConfigurationOwnerRuntimeIndexReader();
        var root = BuildRootCommand(env, stdout, stderr, ownerRuntimeIndexReader);
        var parseResult = root.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: cancellationToken);
        return parseResult.Errors.Count > 0 ? 2 : exitCode;
    }

    /// <summary>Builds the root command with no wiring, for command-grammar assertions.</summary>
    public static RootCommand BuildRootCommand() => BuildRootCommand(
        new Dictionary<string, string?>(), Console.Out, Console.Error, new AzureAppConfigurationOwnerRuntimeIndexReader());

    private static RootCommand BuildRootCommand(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        IOwnerRuntimeIndexReader ownerRuntimeIndexReader)
    {
        var root = new RootCommand("Dark Software Factory — runtime host (run/sweep/serve-orchestrator/serve-agent)");
        root.Subcommands.Add(BuildRunCommand(env, stderr, ownerRuntimeIndexReader));
        root.Subcommands.Add(BuildSweepCommand(env, stderr, ownerRuntimeIndexReader));
        root.Subcommands.Add(BuildServeOrchestratorCommand(env, stderr, ownerRuntimeIndexReader));
        root.Subcommands.Add(BuildServeAgentCommand(env, stderr, ownerRuntimeIndexReader));
        return root;
    }

    private static Command BuildRunCommand(
        IReadOnlyDictionary<string, string?> env, TextWriter stderr, IOwnerRuntimeIndexReader ownerRuntimeIndexReader)
    {
        var signal = StringOption("--signal", "path to a signal JSON file");
        var dryRun = BoolOption("--dry-run", "run the line but skip filing");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("run", "run the intake line for one signal (runtime)");
        AddOptions(command, signal, dryRun, product);
        command.SetAction((parseResult, cancellationToken) => RunVerb(
            env,
            stderr,
            parseResult.GetValue(product),
            ownerRuntimeIndexReader,
            _ => RuntimeVerbs.Run(parseResult.GetValue(signal), parseResult.GetValue(dryRun)),
            cancellationToken));
        return command;
    }

    private static Command BuildSweepCommand(
        IReadOnlyDictionary<string, string?> env, TextWriter stderr, IOwnerRuntimeIndexReader ownerRuntimeIndexReader)
    {
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("sweep", "sweep enabled source agents once (runtime)");
        AddOptions(command, product);
        command.SetAction((parseResult, cancellationToken) => RunVerb(
            env,
            stderr,
            parseResult.GetValue(product),
            ownerRuntimeIndexReader,
            settings => RuntimeVerbs.Sweep(settings.Product),
            cancellationToken));
        return command;
    }

    private static Command BuildServeOrchestratorCommand(
        IReadOnlyDictionary<string, string?> env, TextWriter stderr, IOwnerRuntimeIndexReader ownerRuntimeIndexReader)
    {
        var loop = BoolOption("--loop", "sweep continuously");
        var interval = IntOption("--interval", "seconds between sweeps");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-orchestrator", "run the orchestrator worker (runtime)");
        AddOptions(command, loop, interval, product);
        command.SetAction((parseResult, cancellationToken) => RunVerb(
            env,
            stderr,
            parseResult.GetValue(product),
            ownerRuntimeIndexReader,
            settings => RuntimeVerbs.ServeOrchestrator(settings.Product),
            cancellationToken));
        return command;
    }

    private static Command BuildServeAgentCommand(
        IReadOnlyDictionary<string, string?> env, TextWriter stderr, IOwnerRuntimeIndexReader ownerRuntimeIndexReader)
    {
        var kind = StringOption("--kind", "source agent kind", "sentry");
        var host = StringOption("--host", "bind host", "0.0.0.0");
        var port = IntOption("--port", "bind port", 8080);
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-agent", "serve a source agent over A2A (runtime)");
        AddOptions(command, kind, host, port, product);
        // serve-agent must still validate required runtime config before validating
        // --kind, exactly like every other verb -- it must never report a kind
        // result for a product that isn't configured yet.
        command.SetAction((parseResult, cancellationToken) => RunVerb(
            env,
            stderr,
            parseResult.GetValue(product),
            ownerRuntimeIndexReader,
            _ => RuntimeVerbs.ServeAgent(parseResult.GetValue(kind) ?? "sentry"),
            cancellationToken));
        return command;
    }

    /// <summary>
    /// Composes <see cref="RuntimeSettings"/> and, once they validate, runs
    /// <paramref name="operation"/> -- the verb's real per-invocation work (see
    /// <see cref="RuntimeVerbs"/>). Both a settings failure
    /// (<see cref="RuntimeConfigurationException"/>) and an operation failure
    /// (<see cref="RuntimeVerbException"/>) are printed to stderr and exit
    /// non-zero the same way.
    /// </summary>
    private static async Task<int> RunVerb(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stderr,
        string? productOption,
        IOwnerRuntimeIndexReader ownerRuntimeIndexReader,
        Action<RuntimeSettings> operation,
        CancellationToken cancellationToken)
    {
        RuntimeSettings settings;
        try
        {
            settings = await RuntimeSettingsComposer.ComposeAsync(
                env, productOption, ownerRuntimeIndexReader, cancellationToken);
        }
        catch (RuntimeConfigurationException exception)
        {
            stderr.WriteLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        try
        {
            operation(settings);
        }
        catch (RuntimeVerbException exception)
        {
            stderr.WriteLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        return Success;
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
