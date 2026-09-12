using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>Wire view of one interaction. Loopback-only; never send to third parties.</summary>
public sealed class InteractionDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("key")]
    public string? Key { get; set; }
    [JsonPropertyName("agent")]
    public string Agent { get; set; } = "";
    [JsonPropertyName("agent_instance")]
    public string? AgentInstance { get; set; }
    [JsonPropertyName("project")]
    public string? Project { get; set; }
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }
    [JsonPropertyName("turn_id")]
    public string? TurnId { get; set; }
    [JsonPropertyName("native_request_id")]
    public string? NativeRequestId { get; set; }
    [JsonPropertyName("kind")]
    public InteractionKind Kind { get; set; }
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";
    [JsonPropertyName("choices")]
    public List<InteractionChoice> Choices { get; set; } = [];
    [JsonPropertyName("text_max_length")]
    public int TextMaxLength { get; set; }
    [JsonPropertyName("status")]
    public InteractionStatus Status { get; set; }
    /// <summary>SHA-256 hex binding every response to this exact request.</summary>
    [JsonPropertyName("request_digest")]
    public string RequestDigest { get; set; } = "";
    /// <summary>Single-use secret echoed by relay/mobile responses. Bearer-protected.</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = "";
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("answered_at")]
    public DateTimeOffset? AnsweredAt { get; set; }
    [JsonPropertyName("response")]
    public InteractionResponseDto? Response { get; set; }
}

/// <summary>Wire view of the accepted (first valid) response, if any.</summary>
public sealed class InteractionResponseDto
{
    [JsonPropertyName("response_id")]
    public string ResponseId { get; set; } = "";
    [JsonPropertyName("choice_id")]
    public string? ChoiceId { get; set; }
    [JsonPropertyName("text")]
    public string? Text { get; set; }
    /// <summary>Where the answer came from: desktop, cli, relay, or host id.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
