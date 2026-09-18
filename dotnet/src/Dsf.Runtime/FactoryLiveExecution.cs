using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class FactoryLiveExecution(LiveAuthoritySnapshot expected, FactoryLiveRequest request, FactoryLiveAuthority authority)
{
    internal FactoryLiveExecutionScope Scope { get; } = new(request.OperationId, expected.Live.Plan.Fingerprint, expected.Revision);
    internal async Task RequireCurrentAsync(CancellationToken token)
    {
        if ((await authority.CheckAsync(request, false, token)).Revision != expected.Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "LiveAdmissionAuthorityChanged");
    }
}
