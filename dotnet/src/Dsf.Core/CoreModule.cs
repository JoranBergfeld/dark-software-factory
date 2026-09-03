namespace Dsf.Core;

/// <summary>
/// Marker for the shared core module. Core must never reference application
/// modules (CLI, Runtime, Feature Council, Control Center, Agent Host).
/// </summary>
public static class CoreModule
{
    public const string Name = "Dsf.Core";
}
