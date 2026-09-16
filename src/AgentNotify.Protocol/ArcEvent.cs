using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>
/// Transport-neutral Attention Request Contract event. ARC describes the lifecycle of a bounded
/// request for human attention — including the human answer, when one is asked for — without
/// prescribing HTTP, CLI, stream, or message-bus transport.
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

    /// <summary>The human answer. Present only on <c>response.submitted</c>.</summary>
    public ArcResponse? Response { get; set; }

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

    /// <summary>Native turn/generation identity, when the producer has one.</summary>
    [JsonPropertyName("turn_id")]
    public string? TurnId { get; set; }

    /// <summary>
    /// Opaque host request id. A host adapter needs this to return an accepted answer
    /// into the live call that is blocking on it.
    /// </summary>
    [JsonPropertyName("native_request_id")]
    public string? NativeRequestId { get; set; }

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

    /// <summary>
    /// Declares that the producer is waiting for a human answer. Its presence is what
    /// makes a request answerable; without it the request is one-way.
    /// </summary>
    public ArcResponseSpec? Response { get; set; }

    /// <summary>Why a condition closed. Resolution only.</summary>
    public string? Outcome { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>The shape of the answer an answerable request is waiting for.</summary>
public sealed class ArcResponseSpec
{
    /// <summary><c>permission</c>, <c>single_choice</c>, or <c>text</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>2–12 stable options. Required for the choice kinds, forbidden for text.</summary>
    public List<ArcChoice>? Choices { get; set; }

    /// <summary>Answer bound for the text kind: 1–2,000, default 500.</summary>
    [JsonPropertyName("text_max_length")]
    public int? TextMaxLength { get; set; }

    /// <summary>Absolute deadline after which the producer stops waiting.</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>One stable selectable answer. IDs are producer-opaque and echoed back verbatim.</summary>
public sealed class ArcChoice
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    public string? Detail { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>A human answer to an answerable request. First valid response wins.</summary>
public sealed class ArcResponse
{
    /// <summary>Responder-generated idempotency key, 1–128 characters.</summary>
    [JsonPropertyName("response_id")]
    public string? ResponseId { get; set; }

    /// <summary>
    /// The digest the consumer bound to the displayed request. An answer that does not
    /// echo the current digest answers a question that has since changed.
    /// </summary>
    public string? Digest { get; set; }

    /// <summary>Single-use secret issued with an off-device request.</summary>
    public string? Nonce { get; set; }

    /// <summary>Selected option, for the choice kinds.</summary>
    [JsonPropertyName("choice_id")]
    public string? ChoiceId { get; set; }

    /// <summary>Answer text, for the text kind.</summary>
    public string? Text { get; set; }

    /// <summary>Where the answer came from.</summary>
    public string? Source { get; set; }

    /// <summary>Responding device, when known.</summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>ARC 0.2 lifecycle events accepted by AgentNotify.</summary>
public static class ArcEventTypes
{
    public const string RequestCreated = "request.created";
    public const string RequestUpdated = "request.updated";
    public const string RequestResolved = "request.resolved";
    public const string ResponseSubmitted = "response.submitted";
}

/// <summary>Why an ARC condition closed.</summary>
public static class ArcOutcomes
{
    public const string Answered = "answered";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Superseded = "superseded";
    public const string NotNeeded = "not_needed";

    public static IReadOnlyList<string> All { get; } =
        [Answered, Expired, Cancelled, Superseded, NotNeeded];
}

/// <summary>The answer shapes an answerable ARC request may ask for.</summary>
public static class ArcResponseKinds
{
    public const string Permission = "permission";
    public const string SingleChoice = "single_choice";
    public const string Text = "text";

    public static IReadOnlyList<string> All { get; } = [Permission, SingleChoice, Text];
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
