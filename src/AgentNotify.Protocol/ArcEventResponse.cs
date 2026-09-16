using System.Text.Json.Serialization;

namespace AgentNotify.Protocol;

/// <summary>
/// What an ARC 0.2 event produced locally. An answerable request yields both halves: the
/// notification a person sees and the interaction the producer waits on. A one-way request
/// yields only a notification, and a submitted response only an interaction.
/// </summary>
public sealed class ArcEventResponse
{
    [JsonPropertyName("notification")]
    public NotificationDto? Notification { get; set; }

    [JsonPropertyName("interaction")]
    public InteractionDto? Interaction { get; set; }
}
