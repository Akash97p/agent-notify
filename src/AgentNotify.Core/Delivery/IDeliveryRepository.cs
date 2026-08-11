namespace AgentNotify.Core.Delivery;

public interface IDeliveryRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task UpsertProviderAsync(StoredProviderProfile profile, CancellationToken ct = default);
    Task<StoredProviderProfile?> GetProviderAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderProfile>> ListProvidersAsync(CancellationToken ct = default);
    Task DeleteProviderAsync(string id, CancellationToken ct = default);
    Task UpsertRouteAsync(DeliveryRoute route, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryRoute>> ListRoutesAsync(CancellationToken ct = default);
    Task DeleteRouteAsync(string id, CancellationToken ct = default);
    Task<bool> EnqueueAsync(OutboxItem item, CancellationToken ct = default);
    Task<OutboxItem?> ClaimDueAsync(DateTimeOffset now, CancellationToken ct = default);
    Task CompleteAttemptAsync(OutboxItem item, DeliveryAttempt attempt, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryAttempt>> ListAttemptsAsync(string outboxId, CancellationToken ct = default);
    Task<int> RecoverInterruptedAsync(
        DateTimeOffset abandonedBefore,
        DateTimeOffset retryAt,
        CancellationToken ct = default);
    Task<IReadOnlyDictionary<OutboxStatus, int>> CountOutboxByStatusAsync(
        CancellationToken ct = default);
    Task<IReadOnlyList<OutboxItem>> ListOutboxAsync(int limit = 100, CancellationToken ct = default);
}
