using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Runtime workflow invariants shared by CLI and HTTP adapters: live-filing
/// gates, stable run identity, resume rules, and pre-station load failures.
/// </summary>
internal static class RuntimeWorkflow
{
    public static void EnsureLiveFilingConfirmed(bool dryRun, IReadOnlyDictionary<string, string?>? env)
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

    public static async Task<ConveyorRun> LoadOrCreateRunAsync(
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
            run.Status = RunStatus.Error;
            run.FailureReason =
                $"could not resolve a prior run for identity '{runId}' ({exception.GetType().Name}): "
                + exception.Message;
            run.Record(
                "run:load", $"could not load a persisted run ({exception.GetType().Name}): {exception.Message}");
        }

        return run;
    }
}
