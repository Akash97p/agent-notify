using AgentNotify.Protocol;

namespace AgentNotify.Core.Domain;

/// <summary>Core interaction entity: one waiting question/permission and,
/// once answered, its first valid response. Lives behind the repository interface.</summary>
public sealed class Interaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? Key { get; set; }
    public string Agent { get; set; } = "unknown";
    public string? AgentInstance { get; set; }
    public string? Project { get; set; }
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    public string? NativeRequestId { get; set; }
    public InteractionKind Kind { get; set; } = InteractionKind.Permission;
    public string Prompt { get; set; } = "";
    public List<InteractionChoice> Choices { get; set; } = [];
    public int TextMaxLength { get; set; } = 500;
    public InteractionStatus Status { get; set; } = InteractionStatus.Pending;
    public string RequestDigest { get; set; } = "";
    public string Nonce { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnsweredAt { get; set; }
    public InteractionResponse? Response { get; set; }
}

/// <summary>The accepted (first valid) answer. At most one per interaction.</summary>
public sealed class InteractionResponse
{
    public string ResponseId { get; set; } = "";
    public string InteractionId { get; set; } = "";
    public string? ChoiceId { get; set; }
    public string? Text { get; set; }
    public string Source { get; set; } = "";
    public string? DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
