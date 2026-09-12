using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>Payload used to open an interaction (a waiting question/permission).</summary>
public sealed class CreateInteractionRequest
{
    /// <summary>Optional logical key. A pending interaction with the same key is
    /// returned instead of creating a duplicate (harness re-prompts).</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>Host agent id (e.g. "codex"). Defaults to "unknown".</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    /// <summary>Per-run agent instance identifier.</summary>
    [JsonPropertyName("agent_instance")]
    public string? AgentInstance { get; set; }

    /// <summary>Project/repository name.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; set; }

    /// <summary>Native host session id, when known.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    /// <summary>Native turn/generation id, when known.</summary>
    [JsonPropertyName("turn_id")]
    public string? TurnId { get; set; }

    /// <summary>Opaque host request id needed to answer the live call, when known.</summary>
    [JsonPropertyName("native_request_id")]
    public string? NativeRequestId { get; set; }

    [JsonPropertyName("kind")]
    public InteractionKind Kind { get; set; } = InteractionKind.Permission;

    /// <summary>Exact human-readable question or permission explanation, 1–2000 chars.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    /// <summary>Choices for <see cref="InteractionKind.Permission"/> and
    /// <see cref="InteractionKind.SingleChoice"/> (2–12 entries).</summary>
    [JsonPropertyName("choices")]
    public List<InteractionChoice>? Choices { get; set; }

    /// <summary>Max answer length for <see cref="InteractionKind.Text"/> (1–2000, default 500).</summary>
    [JsonPropertyName("text_max_length")]
    public int? TextMaxLength { get; set; }

    /// <summary>Seconds until expiry (30–3600, default 600).</summary>
    [JsonPropertyName("ttl_seconds")]
    public int? TtlSeconds { get; set; }
}
