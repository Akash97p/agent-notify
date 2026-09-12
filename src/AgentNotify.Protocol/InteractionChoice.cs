using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>One stable selectable answer. IDs are host-opaque and echoed back verbatim.</summary>
public sealed class InteractionChoice
{
    /// <summary>Stable machine ID, 1–64 chars: letters, digits, underscore, hyphen.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Human label shown on desktop and mobile, 1–200 chars.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>Optional extra detail (scope, command preview), at most 500 chars.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
