using System.Text.Json;
using AgentNotify.Protocol;
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

    public static InteractionDto ToDto(Interaction n) => new()
    {
        Id = n.Id,
        Key = n.Key,
        Agent = n.Agent,
        AgentInstance = n.AgentInstance,
        Project = n.Project,
        SessionId = n.SessionId,
        TurnId = n.TurnId,
        NativeRequestId = n.NativeRequestId,
        Kind = n.Kind,
        Prompt = n.Prompt,
        Choices = n.Choices.Select(c => new InteractionChoice { Id = c.Id, Label = c.Label, Detail = c.Detail }).ToList(),
        TextMaxLength = n.TextMaxLength,
        Status = n.Status,
        RequestDigest = n.RequestDigest,
        Nonce = n.Nonce,
        CreatedAt = n.CreatedAt,
        UpdatedAt = n.UpdatedAt,
        ExpiresAt = n.ExpiresAt,
        AnsweredAt = n.AnsweredAt,
        Response = n.Response is null ? null : new InteractionResponseDto
        {
            ResponseId = n.Response.ResponseId,
            ChoiceId = n.Response.ChoiceId,
            Text = n.Response.Text,
            Source = n.Response.Source,
            DeviceId = n.Response.DeviceId,
            CreatedAt = n.Response.CreatedAt
        }
    };
}
