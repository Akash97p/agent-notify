namespace AgentNotify.Contracts;

/// <summary>Semantic notification types understood by AgentNotify.</summary>
public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error,
    InputRequired,
    PermissionRequired,
    Completed,
    Blocked
}
