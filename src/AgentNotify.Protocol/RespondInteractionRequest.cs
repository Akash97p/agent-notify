using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>Payload used to answer a pending interaction. First valid response wins.</summary>
public sealed class RespondInteractionRequest
{
    /// <summary>Client-generated idempotency id (UUID-ish, 1–128 chars). Retrying with
    /// the same id returns the original outcome instead of a conflict.</summary>
    [JsonPropertyName("response_id")]
    public string ResponseId { get; set; } = "";

    /// <summary>Must equal the interaction's current <c>request_digest</c>.</summary>
    [JsonPropertyName("request_digest")]
    public string RequestDigest { get; set; } = "";

    /// <summary>Single-use secret issued with the request (relay/mobile path).</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>Selected choice for permission / single_choice kinds.</summary>
    [JsonPropertyName("choice_id")]
    public string? ChoiceId { get; set; }

    /// <summary>Answer text for the text kind.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Where the answer came from: desktop, cli, relay, or host id.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Responding device, when known (relay/mobile path).</summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}
