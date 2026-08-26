using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>
/// Transport-neutral Attention Request Contract event. ARC describes the lifecycle of a bounded
/// request for human attention without prescribing HTTP, CLI, stream, or message-bus transport.
/// </summary>
public sealed class ArcEvent
{
    [JsonPropertyName("arc_version")]
    public string ArcVersion { get; set; } = "";

    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset? OccurredAt { get; set; }

    public ArcSender? Sender { get; set; }
    public ArcContext? Context { get; set; }
    public ArcRequest? Request { get; set; }
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

public sealed class ArcSender
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Version { get; set; }

    [JsonPropertyName("instance_id")]
    public string? InstanceId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

public sealed class ArcContext
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    public string? Project { get; set; }
    public string? Cwd { get; set; }
    public long? Pid { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

public sealed class ArcRequest
{
    /// <summary>
    /// Stable condition identity. Optional for <c>request.created</c>; required for updates and
    /// resolution. It is separate from the immutable event identifier.
    /// </summary>
    public string? Key { get; set; }

    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Message { get; set; }
    public NotificationPriority? Priority { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>ARC 0.1 lifecycle events accepted by AgentNotify.</summary>
public static class ArcEventTypes
{
    public const string RequestCreated = "request.created";
    public const string RequestUpdated = "request.updated";
    public const string RequestResolved = "request.resolved";
}

/// <summary>Portable reasons an agent may request human attention.</summary>
public static class ArcRequestKinds
{
    public const string Information = "information";
    public const string Question = "question";
    public const string Permission = "permission";
    public const string Blocked = "blocked";
    public const string Failure = "failure";
    public const string Completion = "completion";

    public static IReadOnlyList<string> All { get; } =
        [Information, Question, Permission, Blocked, Failure, Completion];
}

/// <summary>Optional AgentNotify presentation values under <c>extensions.x-agentnotify</c>.</summary>
public sealed class AgentNotifyArcExtension
{
    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}
