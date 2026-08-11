using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Domain;

/// <summary>Core notification entity. Lives behind the repository interface;
/// UI and API code operate on this type.</summary>
public sealed class Notification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? Key { get; set; }
    public string Agent { get; set; } = "unknown";
    public string? AgentInstance { get; set; }
    public string? Project { get; set; }
    public string Type { get; set; } = NotificationTypes.Info;
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Cwd { get; set; }
    public long? Pid { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public static Notification Create(CreateNotificationRequest req, DateTimeOffset now)
    {
        return new Notification
        {
            Key = string.IsNullOrWhiteSpace(req.Key) ? null : req.Key.Trim(),
            Agent = string.IsNullOrWhiteSpace(req.Agent) ? "unknown" : req.Agent.Trim(),
            AgentInstance = string.IsNullOrWhiteSpace(req.AgentInstance) ? null : req.AgentInstance.Trim(),
            Project = string.IsNullOrWhiteSpace(req.Project) ? null : req.Project.Trim(),
            Type = req.Type,
            Priority = req.Priority ?? NotificationPriority.Normal,
            Title = req.Title.Trim(),
            Message = req.Message.Trim(),
            Cwd = string.IsNullOrWhiteSpace(req.Cwd) ? null : req.Cwd.Trim(),
            Pid = req.Pid,
            Metadata = req.Metadata,
            Status = NotificationStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
