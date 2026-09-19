using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class FactoryLiveExecution(LiveAuthoritySnapshot expected, FactoryLiveRequest request, FactoryLiveAuthority authority,
    FactoryDecisionExecution? decision = null)
{
    internal FactoryDecisionExecution? Decision => decision;
    internal LiveAuthoritySnapshot Authority => expected;
    internal FactoryLiveExecutionScope Scope { get; } = new(request.OperationId, expected.Live.Plan.Fingerprint, expected.Revision);
    internal async Task RequireCurrentAsync(CancellationToken token)
    {
        if (decision is not null) await decision.RequireCurrentAsync(token);
        if ((await authority.CheckAsync(request, false, token)).Revision != expected.Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "LiveAdmissionAuthorityChanged");
    }
}
