namespace AgentNotify.Contracts;

/// <summary>Payload used to update a notification (currently only status).</summary>
public sealed class UpdateNotificationRequest
{
    public NotificationStatus? Status { get; set; }
}
