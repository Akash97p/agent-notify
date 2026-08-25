using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>
/// Transport-neutral Agent Event Protocol envelope. AgentNotify currently consumes the
/// <c>notification.sent</c> and <c>question.asked</c> events defined by the public AEP 0.1 draft.
/// </summary>
public sealed class AepEvent
{
    [JsonPropertyName("aep_version")]
    public string AepVersion { get; set; } = "";

    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset? Time { get; set; }
    public string? Hook { get; set; }
    public AepAgent? Agent { get; set; }
    public AepSession? Session { get; set; }
    public AepWorkspace? Workspace { get; set; }
    public AepAction? Action { get; set; }
    public List<AepContent> Content { get; set; } = [];
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class AepAgent
{
    public string Slug { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    public string? Version { get; set; }

    [JsonPropertyName("instance_id")]
    public string? InstanceId { get; set; }

    public string? Surface { get; set; }
}

public sealed class AepSession
{
    public string? Id { get; set; }

    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("turn_id")]
    public string? TurnId { get; set; }
}

public sealed class AepWorkspace
{
    public string? Cwd { get; set; }

    [JsonPropertyName("project_path")]
    public string? ProjectPath { get; set; }

    public List<string>? Roots { get; set; }
}

public sealed class AepAction
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Status { get; set; }
    public AepError? Error { get; set; }
}

public sealed class AepError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}

public sealed class AepContent
{
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Style { get; set; }
    public bool? Truncated { get; set; }

    [JsonPropertyName("original_length")]
    public int? OriginalLength { get; set; }
}

/// <summary>Canonical AEP event types consumed by the AgentNotify attention profile.</summary>
public static class AepEventTypes
{
    public const string NotificationSent = "notification.sent";
    public const string QuestionAsked = "question.asked";
}

/// <summary>Canonical AEP content discriminators consumed by the AgentNotify attention profile.</summary>
public static class AepContentTypes
{
    public const string Notification = "notification";
    public const string Question = "question";
}

/// <summary>Optional values carried under <c>extensions.x-agentnotify</c>.</summary>
public sealed class AgentNotifyAepExtension
{
    public string? Title { get; set; }

    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; set; }

    public NotificationPriority? Priority { get; set; }
    public string? Key { get; set; }
    public string? Project { get; set; }
    public long? Pid { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}
