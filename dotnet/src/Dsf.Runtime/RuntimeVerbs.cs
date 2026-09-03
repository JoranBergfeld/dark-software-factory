using System.Text.Json;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

/// <summary>
/// What each runtime verb actually does once its settings validate: <c>run</c> and
/// <c>sweep</c> drive the Feature Council conveyor in-process and return the run
/// they produced, and <c>serve-orchestrator</c>/<c>serve-agent</c> build and serve
/// real HTTP hosts. Nothing here fails for a valid invocation on principle; the
/// only failures are real, input- or configuration-dependent ones (an unreadable
/// signal, an unknown source agent kind, an unreachable roster store, an
/// incomplete dependency composition, or the filing boundary reached with nothing
/// wired to file through).
/// </summary>
public static class RuntimeVerbs
{
    /// <summary>Default bind host for the served runtime endpoints.</summary>
    public const string DefaultHost = "0.0.0.0";

    /// <summary>Default bind port for the served runtime endpoints.</summary>
    public const int DefaultPort = 8080;

    /// <summary>
    /// Runs the intake line for one signal file. Parses and validates
    /// <paramref name="signalPath"/> (throwing <see cref="RuntimeVerbException"/>
    /// for a missing path, missing file, or invalid JSON), then drives the whole
    /// conveyor and returns the finished run -- checkpoints, audit trail and all.
    /// </summary>
    public static async Task<ConveyorRun> RunAsync(
        RuntimeSettings settings,
        string? signalPath,
        bool dryRun,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);

        if (string.IsNullOrWhiteSpace(signalPath))
        {
            throw new RuntimeVerbException("--signal <path> is required for run.");
        }

        Signal signal;
        try
        {
            signal = SignalReader.ReadFromFile(signalPath, dryRun);
        }
        catch (Exception exception) when (exception is FileNotFoundException or JsonException)
        {
            throw new RuntimeVerbException(exception.Message);
        }

        return await RunSignalAsync(settings, signal, dependencies, cancellationToken);
    }

    /// <summary>Drives the conveyor over an already-parsed signal.</summary>
    public static async Task<ConveyorRun> RunSignalAsync(
        RuntimeSettings settings,
        Signal signal,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(dependencies);

        var services = ComposeServices(settings, dependencies);
        var run = await LoadOrCreateRunAsync(
            services, TriggerKind.Signal, signal.ProductHints, signal.SourceKinds, signal.DryRun, cancellationToken);
        return await ConveyorLine.RunAsync(run, services, cancellationToken);
    }

    /// <summary>
    /// Sweeps the source agents that are actually enabled for the product, reading
    /// the roster from the product's App Configuration store, then drives the
    /// resulting scheduled run through the conveyor. A reachable store with no
    /// enabled agents yields a real, audited empty sweep (nothing to gather); an
    /// unreachable store throws rather than reporting an empty roster it never read.
    /// </summary>
    public static async Task<ConveyorRun> SweepAsync(
        RuntimeSettings settings,
        bool dryRun,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);

        var services = ComposeServices(settings, dependencies);

        IReadOnlyList<string> kinds;
        try
        {
            kinds = await dependencies.SourceAgentRosterReader.ReadEnabledKindsAsync(settings, cancellationToken);
        }
        catch (RuntimeConfigurationException exception)
        {
            throw new RuntimeVerbException(exception.Message);
        }

        var run = await LoadOrCreateRunAsync(
            services, TriggerKind.Scheduled, [settings.Product], kinds, dryRun, cancellationToken);
        run.Record(
            "trigger:scheduled",
            $"scheduled sweep for product '{settings.Product}': enabled sources="
            + $"[{(kinds.Count == 0 ? "(none)" : string.Join(", ", kinds))}] "
            + $"(resolved from {settings.AppConfigEndpoint}).");

        return await ConveyorLine.RunAsync(run, services, cancellationToken);
    }

    /// <summary>
    /// Looks up the run already persisted for this scope's stable identity
    /// (<see cref="RunIdentity"/>) and returns it if found, so a signal or sweep
    /// that matches a prior run resumes it -- with its checkpoints and terminal
    /// status intact -- instead of starting a new run blind to what a previous
    /// process already did. Creates a fresh run, seeded with that same identity as
    /// its id, only when no prior run is found.
    /// </summary>
    /// <remarks>
    /// A resumed, still-open run is forced onto the current invocation's
    /// <paramref name="dryRun"/> before it is returned: a signal or sweep run
    /// today under <c>--dry-run</c> must never reach S7 filing for real just
    /// because the prior process that checkpointed it (through S6 or earlier)
    /// ran without <c>--dry-run</c> and crashed or was killed before filing. A
    /// run already in a terminal status (<see cref="RunStatus.Killed"/>,
    /// <see cref="RunStatus.Previewed"/>, <see cref="RunStatus.Filed"/>,
    /// <see cref="RunStatus.Error"/>) is returned exactly as persisted: it is
    /// never re-driven, so its recorded mode is never altered either -- a run
    /// that already filed for real stays filed, it is not rewritten into a
    /// preview after the fact.
    /// </remarks>
    private static async Task<ConveyorRun> LoadOrCreateRunAsync(
        ConveyorServices services,
        TriggerKind trigger,
        IReadOnlyList<string> productHints,
        IReadOnlyList<string> sourceKinds,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var runId = RunIdentity.Compute(trigger, productHints, sourceKinds);
        var run = new ConveyorRun
        {
            Id = runId,
            Trigger = trigger,
            ProductHints = productHints,
            SourceKinds = sourceKinds,
            DryRun = dryRun,
        };

        try
        {
            var existing = await services.RunStore.LoadAsync(runId, cancellationToken);
            if (existing is not null)
            {
                if (existing.Status == RunStatus.Open && dryRun && !existing.DryRun)
                {
                    existing.DryRun = true;
                    existing.Record(
                        "run:load",
                        "resumed under --dry-run: forcing this run to dry-run so it cannot file for real off "
                        + "checkpoints written by a prior non-dry-run invocation.");
                }

                return existing;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A store that cannot be read is audited as a terminal error on the
            // fresh run rather than thrown: the conveyor's contract is that no
            // per-run failure -- including one before the line even starts --
            // ever propagates past the runtime verb.
            run.Status = RunStatus.Error;
            run.FailureReason =
                $"could not resolve a prior run for identity '{runId}' ({exception.GetType().Name}): "
                + exception.Message;
            run.Record(
                "run:load", $"could not load a persisted run ({exception.GetType().Name}): {exception.Message}");
        }

        return run;
    }

    /// <summary>
    /// Resolves the conveyor's collaborators, translating an incomplete
    /// composition into the operator-facing failure the verb reports. The runtime
    /// never drives a line with dependencies it does not have.
    /// </summary>
    private static ConveyorServices ComposeServices(RuntimeSettings settings, RuntimeDependencies dependencies)
    {
        try
        {
            return dependencies.ConveyorServicesFor(settings);
        }
        catch (RuntimeConfigurationException exception)
        {
            throw new RuntimeVerbException(exception.Message);
        }
    }

    /// <summary>
    /// Builds the orchestrator host: a health endpoint plus a <c>POST /run</c>
    /// endpoint that drives the conveyor over a posted signal payload as a dry run.
    /// Returned unstarted so it can be smoke-tested without serving forever.
    /// </summary>
    public static WebApplication BuildOrchestratorHost(
        RuntimeSettings settings,
        RuntimeDependencies dependencies,
        string host = DefaultHost,
        int port = DefaultPort,
        TimeSpan? sweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);

        var builder = CreateBuilder(host, port);
        if (sweepInterval is not null)
        {
            builder.Services.AddHostedService(provider => new PeriodicSweepService(
                settings,
                dependencies,
                sweepInterval.Value,
                provider.GetRequiredService<ILogger<PeriodicSweepService>>()));
        }

        var app = builder.Build();

        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            verb = "serve-orchestrator",
            product = settings.Product,
            stations = ConveyorLine.StationNames,
        }));

        app.MapPost("/run", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            Signal signal;
            try
            {
                // The served endpoint previews the line; filing is driven by the
                // scheduled sweep, never by an unauthenticated HTTP caller.
                signal = SignalReader.ReadFromJson(body, "POST /run", dryRun: true);
            }
            catch (JsonException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            var run = await RunSignalAsync(settings, signal, dependencies, cancellationToken);
            return Results.Ok(RuntimeRunSummary.From(run));
        });

        return app;
    }

    /// <summary>
    /// Builds the source agent host for <paramref name="kind"/>: its A2A agent card
    /// and its gather endpoint. Throws <see cref="RuntimeVerbException"/> for an
    /// unknown kind. <c>POST /gather</c> reads the kind's configured upstream
    /// integration and answers with the evidence it found; when that integration is
    /// unconfigured it answers 503 naming the setting, and when the integration
    /// itself fails it answers 502 with the reason -- never an empty success.
    /// </summary>
    public static WebApplication BuildSourceAgentHost(
        RuntimeSettings settings,
        string kind,
        RuntimeDependencies dependencies,
        string host = DefaultHost,
        int port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);

        var card = SourceAgentCard.For(kind, settings.Product);
        var integration = dependencies.SourceIntegration;
        var app = CreateBuilder(host, port).Build();

        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            verb = "serve-agent",
            product = settings.Product,
            kind = card.Kind,
        }));
        app.MapGet(SourceAgentCard.CardRoute, () => Results.Ok(card));
        app.MapPost(SourceAgentCard.GatherRoute, async (CancellationToken cancellationToken) =>
        {
            try
            {
                var evidence = await integration.GatherAsync(card.Kind, settings.Product, cancellationToken);
                return Results.Ok(new { kind = card.Kind, product = settings.Product, evidence });
            }
            catch (RuntimeConfigurationException exception)
            {
                return Results.Json(
                    new { kind = card.Kind, error = exception.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new { kind = card.Kind, error = exception.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        return app;
    }

    /// <summary>Builds and serves the orchestrator host until cancelled.</summary>
    public static Task ServeOrchestratorAsync(
        RuntimeSettings settings,
        RuntimeDependencies dependencies,
        string host,
        int port,
        TimeSpan? sweepInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var app = BuildOrchestratorHost(settings, dependencies, host, port, sweepInterval);
        return dependencies.WebHostRunner.RunAsync(app, cancellationToken);
    }

    /// <summary>Builds and serves the source agent host until cancelled.</summary>
    public static Task ServeAgentAsync(
        RuntimeSettings settings,
        string kind,
        RuntimeDependencies dependencies,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var app = BuildSourceAgentHost(settings, kind, dependencies, host, port);
        return dependencies.WebHostRunner.RunAsync(app, cancellationToken);
    }

    private static WebApplicationBuilder CreateBuilder(string host, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        return builder;
    }
}
