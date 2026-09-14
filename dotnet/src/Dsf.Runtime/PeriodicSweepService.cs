using Dsf.Core.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

/// <summary>
/// The orchestrator worker's continuous sweep (<c>serve-orchestrator --loop</c>):
/// reads controls at least once per second while idle, and sweeps only when the
/// current cadence has elapsed. Failed reads skip work, never unpause it.
/// Manual and periodic work share RuntimeVerbs' product-wide ownership guard.
/// </summary>
internal sealed class PeriodicSweepService(
    RuntimeSettings settings,
    RuntimeDependencies dependencies,
    TimeSpan interval,
    IReadOnlyDictionary<string, string?>? env,
    ISweepControlStore sweepControlStore,
    ISweepLease sweepLease,
    ILogger<PeriodicSweepService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan pollInterval = interval >= TimeSpan.FromMilliseconds(1)
        ? (interval < TimeSpan.FromSeconds(1) ? interval : TimeSpan.FromSeconds(1))
        : throw new ArgumentOutOfRangeException(nameof(interval), "Sweep interval must be at least one millisecond.");

    /// <summary>Seconds between sweeps when neither <c>--interval</c> nor the env var is set.</summary>
    public const int DefaultIntervalSeconds = 300;

    /// <summary>Env var that sets the sweep interval, matching the Python runtime.</summary>
    public const string IntervalEnvVar = "DSF_SWEEP_INTERVAL";

    /// <summary>
    /// Resolves the loop interval: an explicit <c>--interval</c> wins, then
    /// <c>DSF_SWEEP_INTERVAL</c>, then 300 seconds; never below one second.
    /// </summary>
    public static TimeSpan ResolveInterval(int? explicitSeconds, IReadOnlyDictionary<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (explicitSeconds is not null)
        {
            return TimeSpan.FromSeconds(Math.Max(1, explicitSeconds.Value));
        }

        var raw = (env.TryGetValue(IntervalEnvVar, out var value) ? value : null)?.Trim();
        return int.TryParse(raw, out var parsed)
            ? TimeSpan.FromSeconds(Math.Max(1, parsed))
            : TimeSpan.FromSeconds(DefaultIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastAttemptEnded = clock.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                var control = await sweepControlStore.ReadAsync(readTimeout.Token).WaitAsync(readTimeout.Token);
                var effectiveInterval = control.IntervalSeconds > 0
                    ? TimeSpan.FromSeconds(control.IntervalSeconds)
                    : interval;

                if (!control.Paused && clock.GetElapsedTime(lastAttemptEnded) >= effectiveInterval)
                {
                    try
                    {
                        var run = await RuntimeVerbs.SweepAsync(
                            settings, dryRun: false, dependencies, stoppingToken, env, sweepLease);
                        if (run is null)
                        {
                            logger.LogInformation(
                                "[dsf] orchestrator tick skipped: another sweep owns the product lease.");
                        }
                        else
                        {
                            foreach (var line in RuntimeRunSummary.From(run).ToLines())
                            {
                                logger.LogInformation("{Line}", line);
                            }
                        }
                    }
                    finally
                    {
                        lastAttemptEnded = clock.GetTimestamp();
                    }
                }
            }
            catch (OperationCanceledException exception) when (stoppingToken.IsCancellationRequested)
            {
                if (exception.InnerException is AggregateException)
                {
                    logger.LogError("[dsf] orchestrator sweep cleanup failed: {Message}", exception.Message);
                }

                return;
            }
            catch (Exception exception)
            {
                logger.LogError("[dsf] orchestrator tick skipped or failed: {Message}", exception.Message);
            }

            try
            {
                await Task.Delay(pollInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
