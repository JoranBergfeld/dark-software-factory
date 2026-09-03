namespace Dsf.Core.Runtime;

/// <summary>
/// Raised when a runtime verb's real per-invocation work (signal parsing, source
/// agent kind lookup) succeeds or fails for a genuine, input-dependent reason --
/// as opposed to <see cref="RuntimeConfigurationException"/>, which is raised
/// purely for missing environment settings. Every runtime host (<c>Dsf.Runtime</c>
/// and the <c>dsf</c> front door in <c>Dsf.Cli</c>) surfaces this the same way:
/// printed to stderr and a non-zero exit code.
/// </summary>
public sealed class RuntimeVerbException(string message) : Exception(message);
