using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Dsf.ControlCenter;

/// <summary>
/// Server-issued operator sessions. The browser only ever holds an opaque
/// session id (HttpOnly cookie); the CSRF token it must echo back on every write
/// is minted with the session and kept server-side, so a cross-site form post
/// cannot forge one even though the browser would attach the cookie.
/// </summary>
internal sealed class OperatorSessionStore(TimeProvider clock, TimeSpan lifetime)
{
    public const string SessionCookie = "cc_session";
    public const string CsrfCookie = "cc_csrf";
    public const string CsrfField = "csrf_token";

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public OperatorSessionStore()
        : this(TimeProvider.System, TimeSpan.FromHours(8))
    {
    }

    public (string SessionId, string CsrfToken) Create()
    {
        var sessionId = RandomNumberGenerator.GetHexString(64, lowercase: true);
        var csrfToken = RandomNumberGenerator.GetHexString(64, lowercase: true);
        _sessions[sessionId] = new Session(csrfToken, clock.GetUtcNow() + lifetime);
        return (sessionId, csrfToken);
    }

    public bool TryGetCsrfToken(string? sessionId, out string csrfToken)
    {
        csrfToken = string.Empty;
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        if (session.ExpiresAt <= clock.GetUtcNow())
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        csrfToken = session.CsrfToken;
        return true;
    }

    public void Remove(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    /// <summary>Constant-time comparison, so token checks leak no timing signal.</summary>
    public static bool TokensMatch(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }

    private sealed record Session(string CsrfToken, DateTimeOffset ExpiresAt);
}
