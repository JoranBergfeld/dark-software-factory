using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

/// <summary>Read-only original-work binding: a stop closes admission, not access to retained work.</summary>
internal sealed class FactoryInspectionAuthority
{
    internal bool AdmissionClosed { get; private set; }

    internal bool Include(string key)
    {
        if (FactoryReattachment.IsStopRecord(key)
            || key.StartsWith(FactoryReattachment.AccessPrefix, StringComparison.Ordinal)) return false;
        if (!FactoryPause.IsStopRecord(key) && !FactoryOffboarding.IsStopRecord(key)
           && !FactoryReconfigurationOperationRecords.IsRecord(key)) return true;
        AdmissionClosed = true;
        return false;
    }
}
