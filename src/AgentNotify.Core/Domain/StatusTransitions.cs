using AgentNotify.Contracts;

namespace AgentNotify.Core.Domain;

/// <summary>Rules for how a notification's status may change over time.</summary>
public static class StatusTransitions
{
    /// <summary>Returns null if the transition is allowed, otherwise an error message.</summary>
    public static string? Validate(NotificationStatus from, NotificationStatus to)
    {
        if (from == to)
            return null;

        return from switch
        {
            NotificationStatus.Active => null, // -> Dismissed | Resolved
            NotificationStatus.Dismissed => to == NotificationStatus.Active
                ? null
                : "a dismissed notification can only be reopened (active)",
            NotificationStatus.Resolved => to == NotificationStatus.Active
                ? null
                : "a resolved notification can only be reopened (active)",
            _ => null
        };
    }
}
