namespace AgentNotify.Protocol;

/// <summary>Payload used to update a notification (currently only status).</summary>
public sealed class UpdateNotificationRequest
{
    public NotificationStatus? Status { get; set; }
}
