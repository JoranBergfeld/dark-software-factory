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
        CancellationToken cancellationToken)
    {
        var root = BuildRootCommand(env, stdout, stderr);
        var parseResult = root.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: cancellationToken);
        return parseResult.Errors.Count > 0 ? 2 : exitCode;
    }

    /// <summary>Builds the root command with no wiring, for command-grammar assertions.</summary>
    public static RootCommand BuildRootCommand() => BuildRootCommand(
        new Dictionary<string, string?>(), Console.Out, Console.Error);

    private static RootCommand BuildRootCommand(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr)
    {
        var root = new RootCommand("Dark Software Factory — runtime host (run/sweep/serve-orchestrator/serve-agent)");
        root.Subcommands.Add(BuildRunCommand(env, stderr));
        root.Subcommands.Add(BuildSweepCommand(env, stderr));
        root.Subcommands.Add(BuildServeOrchestratorCommand(env, stderr));
        root.Subcommands.Add(BuildServeAgentCommand(stderr));
        return root;
    }

    private static Command BuildRunCommand(IReadOnlyDictionary<string, string?> env, TextWriter stderr)
    {
        var signal = StringOption("--signal", "path to a signal JSON file");
        var dryRun = BoolOption("--dry-run", "run the line but skip filing");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("run", "run the intake line for one signal (runtime)");
        AddOptions(command, signal, dryRun, product);
        command.SetAction(parseResult =>
            RunVerb(env, stderr, "run", parseResult.GetValue(product)));
        return command;
    }

    private static Command BuildSweepCommand(IReadOnlyDictionary<string, string?> env, TextWriter stderr)
    {
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("sweep", "sweep enabled source agents once (runtime)");
        AddOptions(command, product);
        command.SetAction(parseResult =>
            RunVerb(env, stderr, "sweep", parseResult.GetValue(product)));
        return command;
    }

    private static Command BuildServeOrchestratorCommand(IReadOnlyDictionary<string, string?> env, TextWriter stderr)
    {
        var loop = BoolOption("--loop", "sweep continuously");
        var interval = IntOption("--interval", "seconds between sweeps");
        var product = StringOption("--product", "resolve runtime env for this product");
        var command = new Command("serve-orchestrator", "run the orchestrator worker (runtime)");
        AddOptions(command, loop, interval, product);
        command.SetAction(parseResult =>
            RunVerb(env, stderr, "serve-orchestrator", parseResult.GetValue(product)));
        return command;
    }

    private static Command BuildServeAgentCommand(TextWriter stderr)
    {
        var kind = StringOption("--kind", "source agent kind", "sentry");
        var host = StringOption("--host", "bind host", "0.0.0.0");
        var port = IntOption("--port", "bind port", 8080);
        var command = new Command("serve-agent", "serve a source agent over A2A (runtime)");
        AddOptions(command, kind, host, port);
        // serve-agent does not build the per-product Services bundle (parity with
        // the Python _cmd_serve_agent, which serves the ASGI app directly) so it
        // never requires the Azure runtime settings.
        command.SetAction(_ =>
        {
            stderr.WriteLine("[dsf] error: serve-agent is not yet implemented in the .NET runtime host.");
            return Failure;
        });
        return command;
    }

    private static int RunVerb(
        IReadOnlyDictionary<string, string?> env,
        TextWriter stderr,
        string verb,
        string? productOption)
    {
        try
        {
            RuntimeSettingsComposer.FromEnvironment(env, productOption);
        }
        catch (RuntimeConfigurationException exception)
        {
            stderr.WriteLine($"[dsf] error: {exception.Message}");
            return Failure;
        }

        stderr.WriteLine($"[dsf] error: {verb} is not yet implemented in the .NET runtime host.");
        return Failure;
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
