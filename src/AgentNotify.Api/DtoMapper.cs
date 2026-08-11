using System.Text.Json;
using AgentNotify.Contracts;
using AgentNotify.Core.Domain;

namespace AgentNotify.Api;

/// <summary>Maps the domain model to the API DTO.</summary>
public static class DtoMapper
{
    public static NotificationDto ToDto(Notification n) => new()
    {
        Id = n.Id,
        Key = n.Key,
        Agent = n.Agent,
        AgentInstance = n.AgentInstance,
        Project = n.Project,
        Type = n.Type,
        Priority = n.Priority,
        Title = n.Title,
        Message = n.Message,
        Cwd = n.Cwd,
        Pid = n.Pid,
        Status = n.Status,
        CreatedAt = n.CreatedAt,
        UpdatedAt = n.UpdatedAt,
        ResolvedAt = n.ResolvedAt,
        Metadata = n.Metadata
    };
}
