using System.Text.Json;

namespace AgentNotify.Contracts;

/// <summary>Notification as returned by the API.</summary>
public sealed class NotificationDto
{
    public string Id { get; set; } = "";
    public string? Key { get; set; }
    public string Agent { get; set; } = "";
    public string? AgentInstance { get; set; }
    public string? Project { get; set; }
    public string Type { get; set; } = NotificationTypes.Info;
    public NotificationPriority Priority { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Cwd { get; set; }
    public long? Pid { get; set; }
    public NotificationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
