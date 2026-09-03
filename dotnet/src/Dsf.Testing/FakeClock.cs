namespace Dsf.Testing;

/// <summary>
/// Deterministic double for a clock dependency. Lives only in the
/// testing-support module; production modules must not reference it.
/// </summary>
public sealed class FakeClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
}
