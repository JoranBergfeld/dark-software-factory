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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? env = null)
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

        return await RunSignalAsync(settings, signal, dependencies, cancellationToken, env);
    }

    /// <summary>Drives the conveyor over an already-parsed signal.</summary>
    public static async Task<ConveyorRun> RunSignalAsync(
        RuntimeSettings settings,
        Signal signal,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(dependencies);

        EnsureLiveFilingConfirmed(signal.DryRun, env);

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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);

        EnsureLiveFilingConfirmed(dryRun, env);

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
            services, TriggerKind.Scheduled, [settings.Product], kinds, dryRun, cancellationToken,
            resumeTerminal: false);
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
    /// that matches a prior, still <see cref="RunStatus.Open"/> run resumes it --
    /// with its checkpoints intact -- instead of starting a new run blind to
    /// what a previous process already did. Creates a fresh run, seeded with
    /// that same identity as its id, only when no prior run is found.
    /// </summary>
    /// <remarks>
    /// A resumed, still-open run is forced onto the current invocation's
    /// <paramref name="dryRun"/> before it is returned, in both directions: a
    /// signal or sweep run today under <c>--dry-run</c> must never reach S7
    /// filing for real just because the prior process that checkpointed it
    /// (through S6 or earlier) ran without <c>--dry-run</c> and crashed or was
    /// killed before filing; conversely, a non-dry-run invocation resuming a
    /// run only ever checkpointed under <c>--dry-run</c> must not be stuck
    /// forever previewing -- it clears the stale dry-run flag so the run can
    /// still file for real.
    ///
    /// A run already in a terminal status (<see cref="RunStatus.Killed"/>, <see
    /// cref="RunStatus.Previewed"/>, <see cref="RunStatus.Filed"/>, <see
    /// cref="RunStatus.Error"/>) behaves differently depending on
    /// <paramref name="resumeTerminal"/>: when <c>true</c> (a <c>--signal</c>
    /// invocation, which represents one concrete, possibly-redelivered event) it
    /// is returned exactly as persisted -- never re-driven, its recorded mode
    /// never altered, so a run that already filed for real stays filed and one
    /// that already previewed stays previewed. When <c>false</c> (a scheduled
    /// sweep, which is a recurring tick and never a retry of the same concrete
    /// occurrence) the scope's terminal result is left untouched but is not
    /// returned: a fresh run is minted under a new identity so the new tick is
    /// actually driven through the line instead of being suppressed by the
    /// scope's first-ever terminal result.
    /// </remarks>
    /// <summary>
    /// Refuses a live (non-dry-run) <c>run</c> or <c>sweep</c> invocation unless the
    /// manual gate <see cref="RuntimeIntegrationSettings.ConfirmLiveFiling"/> is
    /// explicitly set to <c>true</c> in <paramref name="env"/>. This mirrors the
    /// gate <see cref="PollOutcomesAsync"/> already enforces for
    /// <see cref="RuntimeIntegrationSettings.ConfirmLiveOutcomes"/>: an accidental
    /// live filing invocation must fail loudly before any station runs, never be
    /// silently downgraded to a preview. A dry run is always allowed through
    /// untouched.
    /// </summary>
    private static void EnsureLiveFilingConfirmed(bool dryRun, IReadOnlyDictionary<string, string?>? env)
    {
        if (dryRun)
        {
            return;
        }

        var confirmed = (env is not null
            && env.TryGetValue(RuntimeIntegrationSettings.ConfirmLiveFiling, out var value)
                ? value
                : null)
            ?.Trim();
        if (!string.Equals(confirmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeVerbException(
                "refusing to file live without an explicit manual gate: set "
                + $"{RuntimeIntegrationSettings.ConfirmLiveFiling}=true to confirm this run may file real GitHub "
                + "issues instead of previewing them.");
        }
    }

    private static async Task<ConveyorRun> LoadOrCreateRunAsync(
        ConveyorServices services,
        TriggerKind trigger,
        IReadOnlyList<string> productHints,
        IReadOnlyList<string> sourceKinds,
        bool dryRun,
        CancellationToken cancellationToken,
        bool resumeTerminal = true)
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
                if (existing.Status != RunStatus.Open && !resumeTerminal)
                {
                    var fresh = new ConveyorRun
                    {
                        Id = Guid.NewGuid().ToString("n"),
                        Trigger = trigger,
                        ProductHints = productHints,
                        SourceKinds = sourceKinds,
                        DryRun = dryRun,
                    };
                    fresh.Record(
                        "run:load",
                        $"prior run '{existing.Id}' for this scope already reached terminal status "
                        + $"'{existing.Status}': starting a new run '{fresh.Id}' for this sweep instead of "
                        + "resuming it.");
                    return fresh;
                }

                if (existing.Status == RunStatus.Open && dryRun != existing.DryRun)
                {
                    existing.DryRun = dryRun;
                    existing.Record(
                        "run:load",
                        dryRun
                            ? "resumed under --dry-run: forcing this run to dry-run so it cannot file for real "
                              + "off checkpoints written by a prior non-dry-run invocation."
                            : "resumed without --dry-run: clearing this run's stale dry-run flag so it can file "
                              + "for real off checkpoints written by a prior --dry-run invocation.");
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
    /// Polls human outcomes on filed issues and records audited learning data.
    /// Exactly one of <paramref name="dryRun"/>/<paramref name="live"/> must be
    /// <c>true</c> -- an operator must say which they mean, rather than the verb
    /// guessing. A dry run polls and reports every outcome found without
    /// recording any of it. A live run additionally requires the manual gate
    /// <see cref="RuntimeIntegrationSettings.ConfirmLiveOutcomes"/> to be set in
    /// <paramref name="env"/>; without it, an accidental live invocation is
    /// refused before anything is recorded, never silently downgraded to a
    /// preview. Recording is idempotent: an outcome already recorded on a prior
    /// poll is reported as such rather than written again.
    /// </summary>
    public static async Task<OutcomeSweepResult> PollOutcomesAsync(
        RuntimeSettings settings,
        bool dryRun,
        bool live,
        IReadOnlyDictionary<string, string?> env,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(dependencies);

        if (dryRun && live)
        {
            throw new RuntimeVerbException(
                "--dry-run and --live are mutually exclusive for poll-outcomes; pass exactly one.");
        }

        if (!dryRun && !live)
        {
            throw new RuntimeVerbException(
                "poll-outcomes requires exactly one of --dry-run or --live: pass --dry-run to preview outcomes "
                + "without recording, or --live to record them for real.");
        }

        LearningServices services;
        try
        {
            services = dependencies.LearningServicesFor(settings);
        }
        catch (RuntimeConfigurationException exception)
        {
            throw new RuntimeVerbException(exception.Message);
        }

        if (live)
        {
            var confirmed = (env.TryGetValue(RuntimeIntegrationSettings.ConfirmLiveOutcomes, out var value) ? value : null)
                ?.Trim();
            if (!string.Equals(confirmed, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeVerbException(
                    "refusing to poll outcomes live without an explicit manual gate: set "
                    + $"{RuntimeIntegrationSettings.ConfirmLiveOutcomes}=true to confirm this run may record real "
                    + "learning data against a live GitHub repository and Cosmos account.");
            }
        }

        IReadOnlyList<OutcomeSignal> signals;
        try
        {
            signals = await services.OutcomeSource.PollAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RuntimeVerbException(
                $"could not poll human outcomes for product '{settings.Product}': {exception.Message}");
        }

        var outcomes = new List<OutcomeRecord>();
        foreach (var signal in signals)
        {
            if (dryRun)
            {
                outcomes.Add(new OutcomeRecord(signal.IntentKey, signal.Verdict, signal.IssueUrl, signal.Title, Recorded: false));
                continue;
            }

            bool recorded;
            try
            {
                recorded = await services.LearningStore.RecordAsync(
                    new LearningRecord(
                        signal.IntentKey, signal.Verdict, signal.IssueUrl, signal.Title, DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new RuntimeVerbException(
                    $"could not record the learning outcome for intent '{signal.IntentKey}' verdict "
                    + $"'{signal.Verdict}': {exception.Message}");
            }

            outcomes.Add(new OutcomeRecord(signal.IntentKey, signal.Verdict, signal.IssueUrl, signal.Title, recorded));
        }

        return new OutcomeSweepResult(outcomes, dryRun);
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
        TimeSpan? sweepInterval = null,
        IReadOnlyDictionary<string, string?>? env = null)
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
                env,
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
            var summary = RuntimeRunSummary.From(run);
            return run.Status == RunStatus.Error
                ? Results.Json(summary, statusCode: StatusCodes.Status500InternalServerError)
                : Results.Ok(summary);
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

    /// <summary>
    /// Builds and serves the orchestrator host until cancelled. When
    /// <paramref name="sweepInterval"/> is set (<c>--loop</c>), the manual
    /// live-filing gate (<see cref="RuntimeIntegrationSettings.ConfirmLiveFiling"/>)
    /// is checked once here, before the host -- and its ticking background sweep --
    /// ever starts: the scheduled sweep always files live, so an unconfirmed gate
    /// is a permanent config/safety failure, not a transient tick error. Catching
    /// it here throws <see cref="RuntimeVerbException"/> synchronously, which
    /// exits the process non-zero, instead of letting the loop swallow it forever
    /// as a per-tick failure and keep the process running anyway.
    /// </summary>
    public static Task ServeOrchestratorAsync(
        RuntimeSettings settings,
        RuntimeDependencies dependencies,
        string host,
        int port,
        TimeSpan? sweepInterval,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        if (sweepInterval is not null)
        {
            EnsureLiveFilingConfirmed(dryRun: false, env);
        }

        var app = BuildOrchestratorHost(settings, dependencies, host, port, sweepInterval, env);
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
