namespace Dsf.ControlCenter;

/// <summary>
/// A governance control the Python Control Center exposed that the current .NET
/// runtime does not yet consume. Rendering these disabled -- with the reason and
/// the supported alternative -- keeps the operator surface honest: a control that
/// cannot take effect is never presented as an active button that silently does
/// nothing.
/// </summary>
internal sealed record UnsupportedControl(string Name, string Reason, string SupportedAlternative);

internal static class UnsupportedControls
{
    public static readonly IReadOnlyList<UnsupportedControl> All =
    [
        new(
            "Critic enablement",
            "The conveyor's S5 council scores every proposal on evidence weight and has no per-critic roster to enable or disable.",
            "Raise or lower this product's confidence threshold to change how selective the council is."),
        new(
            "Critic weights",
            "No weighted critic panel exists in the runtime, so a stored weight would never be read.",
            "Raise or lower this product's confidence threshold to change how selective the council is."),
        new(
            "Trigger pause",
            "The runtime is pull-only: work arrives from scheduled sweeps of the source agents, not from pushed triggers that could be paused.",
            "Disable the source agents that feed the unwanted work."),
        new(
            "Global dry-run switch",
            "Dry run is a per-invocation flag on the runtime verbs, not stored configuration, so flipping it here could not reach a running sweep.",
            "Invoke the runtime with the dry-run flag, or disable every source agent to stop new work."),
    ];
}
