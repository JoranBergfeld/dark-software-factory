using Dsf.Core.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

/// <summary>
/// The orchestrator worker's continuous sweep (<c>serve-orchestrator --loop</c>):
/// sweeps the enabled source agents every interval for as long as the host serves,
/// logging each finished run. One failing tick is logged and swallowed so a bad
/// sweep never tears down the long-lived Container App, matching the Python
/// <c>run_orchestrator_loop</c> behaviour.
/// </summary>
internal sealed class PeriodicSweepService(
    RuntimeSettings settings,
    RuntimeDependencies dependencies,
    TimeSpan interval,
    IReadOnlyDictionary<string, string?>? env,
    ILogger<PeriodicSweepService> logger) : BackgroundService
{
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
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var run = await RuntimeVerbs.SweepAsync(settings, dryRun: false, dependencies, stoppingToken, env);
                foreach (var line in RuntimeRunSummary.From(run).ToLines())
                {
                    logger.LogInformation("{Line}", line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError("[dsf] orchestrator tick failed: {Message}", exception.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
