using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public static class FactoryPublicationWire
{
    // The result repeats its journal's receipt and attempts; admission and inspection
    // repeat subsets of the journal. Count serialized/escaped bytes, not raw issue text.
    // Native revision, reason, protocol and JSON framing fit the separate 16 KiB reserve.
    public const int MaximumResponseBytes = 2 * NativeCosmosDocumentLimits.MaximumCheckpointBytes + 16 * 1024;

    // A decision also returns the captured evaluation and run, plus publication results.
    // Each eligible candidate requires distinct synthesis, grounding and three jury calls.
    public static int MaximumDecisionBytes => checked((2 + FactoryPreviewExecution.Policy.MaximumDispatches / 5) * MaximumResponseBytes);
}
