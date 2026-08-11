namespace AgentNotify.App;

/// <summary>Ensures only one AgentNotify process runs per user session and lets a second
/// invocation signal the first to show the Notification Center.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Local\\AgentNotify.SingleInstance.v1";
    private const string ShowCenterEventName = "Local\\AgentNotify.ShowCenter.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showCenterEvent;

    public bool IsFirstInstance { get; private init; }

    /// <summary>Returns an instance that owns the singleton (IsFirstInstance true), or a
    /// non-owner the caller should use only to signal the owner.</summary>
    public static SingleInstance TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            EventWaitHandle? existingEvent = null;
            for (var attempt = 0; attempt < 5 && existingEvent is null; attempt++)
            {
                try { existingEvent = EventWaitHandle.OpenExisting(ShowCenterEventName); }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The owner may still be between acquiring the mutex and
                    // creating the event. Retry briefly before exiting safely.
                    Thread.Sleep(40);
                }
            }
            return new SingleInstance { IsFirstInstance = false, _showCenterEvent = existingEvent };
        }

        return new SingleInstance
        {
            IsFirstInstance = true,
            _mutex = mutex,
            _showCenterEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowCenterEventName)
        };
    }

    public void SignalShowCenter()
    {
        try { _showCenterEvent?.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Blocks the calling thread until the owner is asked to show the center.
    /// Returns false when the wait failed (e.g. owner shutting down).</summary>
    public bool WaitForShowCenter()
    {
        try { return _showCenterEvent is not null && _showCenterEvent.WaitOne(Timeout.Infinite); }
        catch (ObjectDisposedException) { return false; }
    }

    public void Dispose()
    {
        if (IsFirstInstance)
            try { _showCenterEvent?.Set(); } catch { }
        _showCenterEvent?.Dispose();
        _showCenterEvent = null;
        _mutex?.Dispose();
        _mutex = null;
    }
}
