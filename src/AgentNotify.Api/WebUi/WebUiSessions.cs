using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentNotify.Api.WebUi;

/// <summary>
/// Browser sessions for the local web UI.
/// </summary>
/// <remarks>
/// <para>
/// A browser never holds the broker's bearer token. It gets in one of two ways, and both end in the
/// same opaque, server-side session referenced by an HttpOnly cookie:
/// </para>
/// <list type="bullet">
/// <item>a <b>launch code</b>, minted by an already-authenticated caller (<c>agentnotify ui</c> or
/// the tray menu), single-use and valid for two minutes; or</item>
/// <item>the bearer token pasted into the sign-in form once, which is compared in constant time and
/// throttled.</item>
/// </list>
/// <para>
/// Everything is in memory. Restarting the broker signs every browser out, which is the right
/// failure direction for a credential that grants control of delivery settings.
/// </para>
/// </remarks>
public sealed class WebUiSessions
{
    public static readonly TimeSpan LaunchCodeLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SessionIdleLifetime = TimeSpan.FromDays(7);
    private const int MaximumSessions = 64;
    private const int MaximumFailedSignInsPerMinute = 10;

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _launchCodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly object _failureGate = new();
    private readonly Queue<DateTimeOffset> _failures = new();

    public WebUiSessions(Func<DateTimeOffset>? utcNow = null) => _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public string CreateLaunchCode()
    {
        Sweep();
        var code = NewSecret();
        _launchCodes[Hash(code)] = _utcNow() + LaunchCodeLifetime;
        return code;
    }

    /// <summary>Consumes a launch code and returns a new session id, or null when the code is unknown, used, or expired.</summary>
    public string? RedeemLaunchCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128)
            return null;
        if (!_launchCodes.TryRemove(Hash(code), out var expiresAt) || expiresAt < _utcNow())
            return null;
        return CreateSession();
    }

    /// <summary>
    /// Signs in with the bearer token. Returns null for a wrong token; throws
    /// <see cref="WebUiThrottledException"/> once too many wrong tokens arrived in the last minute.
    /// </summary>
    public string? SignInWithToken(string? presented, string expected)
    {
        lock (_failureGate)
        {
            var cutoff = _utcNow() - TimeSpan.FromMinutes(1);
            while (_failures.Count > 0 && _failures.Peek() < cutoff) _failures.Dequeue();
            if (_failures.Count >= MaximumFailedSignInsPerMinute)
                throw new WebUiThrottledException();
        }

        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim())),
                SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
        {
            lock (_failureGate) _failures.Enqueue(_utcNow());
            return null;
        }

        return CreateSession();
    }

    /// <summary>True when the session exists and is still live; a live session is extended.</summary>
    public bool Validate(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
            return false;
        var key = Hash(sessionId);
        if (!_sessions.TryGetValue(key, out var expiresAt))
            return false;
        var now = _utcNow();
        if (expiresAt < now)
        {
            _sessions.TryRemove(key, out _);
            return false;
        }

        _sessions[key] = now + SessionIdleLifetime;
        return true;
    }

    public void End(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && sessionId.Length <= 128)
            _sessions.TryRemove(Hash(sessionId), out _);
    }

    private string CreateSession()
    {
        Sweep();
        // A bounded table: the oldest sessions give way rather than letting a loop grow memory.
        while (_sessions.Count >= MaximumSessions)
        {
            var oldest = _sessions.OrderBy(pair => pair.Value).First();
            _sessions.TryRemove(oldest.Key, out _);
        }

        var id = NewSecret();
        _sessions[Hash(id)] = _utcNow() + SessionIdleLifetime;
        return id;
    }

    private void Sweep()
    {
        var now = _utcNow();
        foreach (var (key, expiresAt) in _launchCodes)
            if (expiresAt < now) _launchCodes.TryRemove(key, out _);
        foreach (var (key, expiresAt) in _sessions)
            if (expiresAt < now) _sessions.TryRemove(key, out _);
    }

    // Only digests are kept, so a memory dump of the table yields nothing presentable.
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class WebUiThrottledException : Exception
{
    public WebUiThrottledException() : base("Too many failed sign-in attempts. Wait a minute and try again.") { }
}
