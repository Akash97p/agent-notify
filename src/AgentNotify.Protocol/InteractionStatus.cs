namespace AgentNotify.Protocol;

/// <summary>Lifecycle of one interaction. Terminal states never change again.</summary>
public enum InteractionStatus
{
    Pending,
    Answered,
    Expired,
    Cancelled,
    Superseded
}
