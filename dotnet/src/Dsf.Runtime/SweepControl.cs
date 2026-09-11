using System.Globalization;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// The sweep loop's operator-governable state: whether it is paused, and the
/// cadence it ticks at. <c>IntervalSeconds</c> of <c>0</c> means "unset" -- the
/// loop falls back to its own constructed/env-resolved default
/// (<see cref="PeriodicSweepService.ResolveInterval"/>) instead of a stored value.
/// </summary>
public sealed record SweepControlState(bool Paused, int IntervalSeconds)
{
    /// <summary>The state before any operator has ever written to the store.</summary>
    public static readonly SweepControlState Unset = new(Paused: false, IntervalSeconds: 0);
}

/// <summary>
/// Operator controls for the in-process sweep loop: <c>dsf sweep pause/resume</c>
/// flip a paused flag the loop reads on every tick, and <c>dsf sweep interval
/// &lt;n&gt;</c> changes the cadence it reads, all without a redeploy.
/// </summary>
public interface ISweepControlStore
{
    Task<SweepControlState> ReadAsync(CancellationToken cancellationToken);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken);

    Task SetIntervalSecondsAsync(int seconds, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes the sweep control state on the product's own App
/// Configuration store (<c>AZURE_APPCONFIG_ENDPOINT</c>), using the same
/// managed-identity-capable gateway (and the same unlabeled-key convention) the
/// confidence threshold reader already relies on: <c>sweep-paused</c> and
/// <c>sweep-interval-seconds</c>. A store with neither key yields
/// <see cref="SweepControlState.Unset"/>, so the loop falls back to its own
/// constructed default rather than failing outright.
/// </summary>
internal sealed class AzureAppConfigurationSweepControlStore(
    IConfigurationSettingsGateway gateway,
    RuntimeSettings settings) : ISweepControlStore
{
    private const string PausedKey = ProductConfigurationKeys.SweepPaused;
    private const string IntervalKey = ProductConfigurationKeys.SweepIntervalSeconds;

    public AzureAppConfigurationSweepControlStore(RuntimeSettings settings)
        : this(new AzureConfigurationSettingsGateway(), settings)
    {
    }

    public async Task<SweepControlState> ReadAsync(CancellationToken cancellationToken)
    {
        var paused = false;
        var intervalSeconds = 0;
        try
        {
            await foreach (var (key, value) in gateway.ListAsync(
                               settings.AppConfigEndpoint,
                               ProductConfigurationKeys.NoLabel,
                               cancellationToken))
            {
                if (string.Equals(key, PausedKey, StringComparison.Ordinal))
                {
                    if (!bool.TryParse(value, out paused))
                    {
                        throw new FormatException($"'{PausedKey}' must be 'true' or 'false'.");
                    }
                }
                else if (string.Equals(key, IntervalKey, StringComparison.Ordinal))
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intervalSeconds)
                        || intervalSeconds < 1)
                    {
                        throw new FormatException($"'{IntervalKey}' must be a positive integer number of seconds.");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RuntimeConfigurationException(
                $"failed to read the sweep control state for product '{settings.Product}' from the App "
                + $"Configuration store at '{settings.AppConfigEndpoint}': {exception.Message}",
                [RuntimeSettingsComposer.AzureAppConfigEndpoint]);
        }

        return new SweepControlState(paused, intervalSeconds);
    }

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken) =>
        WriteAsync(PausedKey, paused ? "true" : "false", cancellationToken);

    public Task SetIntervalSecondsAsync(int seconds, CancellationToken cancellationToken)
    {
        if (seconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "sweep interval must be at least 1 second.");
        }

        return WriteAsync(IntervalKey, seconds.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    private async Task WriteAsync(string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            await gateway.SetAsync(settings.AppConfigEndpoint, key, value, label: null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RuntimeConfigurationException(
                $"failed to write the sweep control state for product '{settings.Product}' to the App "
                + $"Configuration store at '{settings.AppConfigEndpoint}': {exception.Message}",
                [RuntimeSettingsComposer.AzureAppConfigEndpoint]);
        }
    }
}
