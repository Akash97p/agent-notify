using System.Text.Json;
using AgentNotify.Contracts;
using AgentNotify.Core.Domain;

namespace AgentNotify.Core.Persistence;

public sealed class NotificationQuery
{
    /// <summary>When true, only active (unresolved, undismissed) notifications.</summary>
    public bool? Unresolved { get; set; }

    public NotificationType? Type { get; set; }
    public NotificationStatus? Status { get; set; }
    public string? Project { get; set; }
    public string? Agent { get; set; }

    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; set; } = 100;
}

public interface INotificationRepository
{
    Task<Notification> CreateAsync(Notification notification, CancellationToken ct = default);
    Task<Notification?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<Notification?> FindActiveByKeyAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<Notification>> QueryAsync(NotificationQuery query, CancellationToken ct = default);
    Task<Notification?> UpdateAsync(Notification notification, CancellationToken ct = default);
    Task<Notification?> UpdateStatusAsync(string id, NotificationStatus status, DateTimeOffset now, CancellationToken ct = default);
    Task<int> CountActiveAsync(CancellationToken ct = default);
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);
    Task InitializeAsync(CancellationToken ct = default);
}

public static class NotificationRow
{
    public static Dictionary<string, JsonElement>? ParseMetadata(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json.Options);

    public static string SerializeMetadata(Dictionary<string, JsonElement>? metadata) =>
        metadata is null ? "null" : JsonSerializer.Serialize(metadata, Json.Options);
}
