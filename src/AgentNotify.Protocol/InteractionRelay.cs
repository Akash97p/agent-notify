using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>
/// Wire shapes for the Relay/mobile half of interactions. This is the contract
/// the self-hosted Relay server and the mobile app implement; see
/// <c>docs/RELAY_INTERACTIONS.md</c>. Versioned: every payload carries
/// <c>contract_version: "1"</c>.
/// </summary>
public static class InteractionRelayContract
{
    public const string Version = "1";

    /// <summary>Outbox payload marker so the phone can tell questions from notices.</summary>
    public const string RequestPayloadKind = "interaction-request";
}

/// <summary>
/// One answer submitted by mobile, stored by Relay, ingested by desktop.
/// Desktop revalidates everything; Relay is an untrusted store.
/// </summary>
public sealed class RelayInteractionResponse
{
    [JsonPropertyName("contract_version")]
    public string ContractVersion { get; set; } = "";

    /// <summary>Client-generated idempotency id. Retries reuse it.</summary>
    [JsonPropertyName("response_id")]
    public string ResponseId { get; set; } = "";

    [JsonPropertyName("interaction_id")]
    public string InteractionId { get; set; } = "";

    /// <summary>Must equal the request's <c>request_digest</c>.</summary>
    [JsonPropertyName("request_digest")]
    public string RequestDigest { get; set; } = "";

    /// <summary>Echo of the request nonce. Required.</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    [JsonPropertyName("choice_id")]
    public string? ChoiceId { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Echo of the request's sender installation id.</summary>
    [JsonPropertyName("installation_id")]
    public string? InstallationId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

/// <summary>Desktop poll response from the Relay server.</summary>
public sealed class RelayResponsePollResult
{
    [JsonPropertyName("responses")]
    public List<RelayInteractionResponse>? Responses { get; set; }

    /// <summary>Opaque cursor; echo back as <c>since</c> on the next poll.</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }
}
