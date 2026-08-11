using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery;

public class ProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public IReadOnlyList<string> SecretNames { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Persistence-only record. Never expose through API/UI DTOs.</summary>
public sealed class StoredProviderProfile : ProviderProfile
{
    [JsonIgnore]
    public string EncryptedSecrets { get; set; } = "";
}

public sealed class DeliveryRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public bool Enabled { get; set; }
    public NotificationPriority MinimumPriority { get; set; } = NotificationPriority.Normal;
    public string? TypeId { get; set; }
    public string? Project { get; set; }
    public string? Agent { get; set; }
    public bool IncludeMessage { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum OutboxStatus
{
    Pending,
    Processing,
    Retry,
    Delivered,
    DeadLetter
}

public sealed class OutboxItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NotificationId { get; set; } = "";
    public string RouteId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeliveryAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OutboxId { get; set; } = "";
    public int AttemptNumber { get; set; }
    public bool Succeeded { get; set; }
    public int? StatusCode { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record DeliveryProvider(
    ProviderProfile Profile,
    IReadOnlyDictionary<string, string> Secrets);

public sealed record DeliveryDiagnosticSnapshot(
    int Pending,
    int Processing,
    int Retry,
    int Delivered,
    int DeadLetter,
    IReadOnlyList<string> RegisteredAdapters);
