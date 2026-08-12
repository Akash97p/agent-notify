using AgentNotify.Core.Domain;

namespace AgentNotify.Desktop;

/// <summary>How long a notification should stay on screen.</summary>
/// <param name="Seconds">Lifetime in seconds; <c>0</c> means it stays until the user dismisses it.</param>
public readonly record struct NotificationLifetime(int Seconds)
{
    /// <summary>True for attention notifications that must not disappear on their own.</summary>
    public bool IsSticky => Seconds <= 0;
}

/// <summary>
/// Shows a notification on the current desktop session.
/// </summary>
/// <remarks>
/// Implementations are secondary to local persistence: the notification is already committed to
/// SQLite before this is called, so a failure here must never propagate. Every implementation that
/// launches a helper process must pass arguments as a list rather than through a shell, because
/// notification titles and messages are attacker-influenced text from whatever an agent sends.
/// </remarks>
public interface IDesktopNotifier
{
    /// <summary>Short identifier used in diagnostics, for example <c>notify-send</c>.</summary>
    string Name { get; }

    /// <summary>True when this backend can be used in the current session.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Displays <paramref name="notification"/>. Returns false when the backend declined or failed;
    /// implementations must not throw.
    /// </summary>
    Task<bool> ShowAsync(
        Notification notification,
        NotificationLifetime lifetime,
        CancellationToken cancellationToken = default);
}
