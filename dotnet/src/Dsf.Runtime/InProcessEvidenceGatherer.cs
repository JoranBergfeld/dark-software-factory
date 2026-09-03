using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Gathers evidence in-process, with no A2A hop to a separately served source
/// agent: calls the kind's upstream integration directly, in the orchestrator's
/// own process. This is the default evidence path (source agents run in-process
/// unless a served agent's endpoint is explicitly configured for that kind, see
/// <see cref="EnvironmentConveyorComposer"/>): the exact same
/// <see cref="ISourceIntegration"/> a served agent's <c>/gather</c> endpoint would
/// call is called here instead, so an unconfigured or unreachable integration
/// fails identically either way -- naming the unset setting or the upstream
/// failure -- rather than the in-process path reporting a silent, empty
/// investigation.
/// </summary>
internal sealed class InProcessEvidenceGatherer(string sourceKind, string product, ISourceIntegration integration)
    : IEvidenceGatherer
{
    public string SourceKind { get; } = sourceKind.Trim().ToLowerInvariant();

    public Task<IReadOnlyList<EvidenceItem>> GatherAsync(ConveyorRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return integration.GatherAsync(SourceKind, product, cancellationToken);
    }
}
