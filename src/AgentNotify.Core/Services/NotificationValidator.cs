using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Services;

/// <summary>Field-level validation for create/update requests. Returns null when valid,
/// otherwise a human-readable problem message.</summary>
public static class NotificationValidator
{
    public const int MaxTitleLength = 200;
    public const int MaxMessageLength = 4000;
    public const int MaxAgentLength = 100;
    public const int MaxAgentInstanceLength = 100;
    public const int MaxProjectLength = 200;
    public const int MaxCwdLength = 1024;
    public const int MaxKeyLength = 100;
    public const int MaxMetadataBytes = 8192;

    public static string? Validate(CreateNotificationRequest request)
    {
        if (request is null)
            return "body is required";

        if (string.IsNullOrWhiteSpace(request.Title))
            return "title is required";
        if (request.Title.Trim().Length > MaxTitleLength)
            return $"title must be at most {MaxTitleLength} characters";

        if (string.IsNullOrWhiteSpace(request.Message))
            return "message is required";
        if (request.Message.Trim().Length > MaxMessageLength)
            return $"message must be at most {MaxMessageLength} characters";

        if (NotificationTypes.Normalize(request.Type) is null)
            return "type must be a lowercase identifier containing letters, numbers, or underscores (maximum 64 characters)";

        if (request.Agent is { Length: > MaxAgentLength })
            return $"agent must be at most {MaxAgentLength} characters";
        if (request.AgentInstance is { Length: > MaxAgentInstanceLength })
            return $"agentInstance must be at most {MaxAgentInstanceLength} characters";
        if (request.Project is { Length: > MaxProjectLength })
            return $"project must be at most {MaxProjectLength} characters";
        if (request.Cwd is { Length: > MaxCwdLength })
            return $"cwd must be at most {MaxCwdLength} characters";
        if (request.Key is { Length: > MaxKeyLength })
            return $"key must be at most {MaxKeyLength} characters";
        if (request.Pid is < 0)
            return "pid must be a non-negative integer";

        if (request.Metadata is not null)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Metadata, Json.Options);
                if (bytes.Length > MaxMetadataBytes)
                    return $"metadata must be at most {MaxMetadataBytes} bytes";
            }
            catch (JsonException)
            {
                return "metadata is not valid JSON";
            }
        }

        return null;
    }

    public static string? Validate(UpdateNotificationRequest request)
    {
        if (request is null)
            return "body is required";
        if (request.Status is null)
            return "status is required";
        return null;
    }
}
