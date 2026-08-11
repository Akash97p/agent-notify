using System.Collections.Concurrent;

namespace AgentNotify.Api.Auth;

/// <summary>Sliding fixed-window rate limiter keyed by an arbitrary string (e.g. the
/// calling token). Roughly bounds abusive traffic; not a security boundary.</summary>
public sealed class RateLimiter
{
    private readonly int _maxPerWindow;
    private readonly long _windowTicks;
    private readonly ConcurrentDictionary<string, WindowState> _states = new();

    public RateLimiter(int maxPerWindow, TimeSpan window)
    {
        _maxPerWindow = maxPerWindow;
        _windowTicks = window.Ticks;
    }

    /// <summary>Returns true when the caller is within the limit for this window.</summary>
    public bool TryAcquire(string key)
    {
        var now = DateTime.UtcNow.Ticks;
        var state = _states.GetOrAdd(key, _ => new WindowState { WindowStartTicks = now });
        lock (state)
        {
            if (now - state.WindowStartTicks >= _windowTicks)
            {
                state.WindowStartTicks = now;
                state.Count = 0;
            }
            if (state.Count >= _maxPerWindow)
                return false;
            state.Count++;
            return true;
        }
    }

    /// <summary>Removes idle entries to avoid unbounded growth.</summary>
    public void Prune(DateTime cutoffUtc)
    {
        var cutoffTicks = cutoffUtc.Ticks;
        foreach (var (key, state) in _states)
        {
            lock (state)
            {
                if (state.WindowStartTicks < cutoffTicks && _states.TryRemove(key, out _))
                {
                }
            }
        }
    }

    private sealed class WindowState
    {
        public long WindowStartTicks;
        public int Count;
    }
}
