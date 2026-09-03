namespace Dsf.Core.Runtime;

/// <summary>
/// Raised when the runtime environment is missing one or more required settings.
/// Carries every unset requirement's env var name so callers can report all of
/// them at once instead of failing one setting at a time.
/// </summary>
public sealed class RuntimeConfigurationException : Exception
{
    public RuntimeConfigurationException(string message, IReadOnlyList<string> missingSettings)
        : base(message)
    {
        MissingSettings = missingSettings;
    }

    /// <summary>The env var names that were required but unset, in check order.</summary>
    public IReadOnlyList<string> MissingSettings { get; }
}
