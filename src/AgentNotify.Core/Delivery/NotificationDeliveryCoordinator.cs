using AgentNotify.Core.Domain;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Materializes matching routes into durable outbox rows after local notification persistence.
/// It performs no network I/O.
/// </summary>
public sealed class NotificationDeliveryCoordinator
{
    private readonly IDeliveryRepository _repository;
    private readonly Action _signalDispatcher;

    public NotificationDeliveryCoordinator(
        IDeliveryRepository repository,
        Action? signalDispatcher = null)
    {
        _repository = repository;
        _signalDispatcher = signalDispatcher ?? (() => { });
    }

    public async Task<int> EnqueueAsync(
        Notification notification,
        CancellationToken ct = default)
    {
        var routes = await _repository.ListRoutesAsync(ct);
        if (routes.Count == 0)
            return 0;

        var enabledProviderIds = (await _repository.ListProvidersAsync(ct))
            .Where(profile => profile.Enabled)
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;

        foreach (var route in routes.Where(route =>
                     enabledProviderIds.Contains(route.ProviderId) &&
                     DeliveryRouting.Matches(route, notification)))
        {
            var item = new OutboxItem
            {
                NotificationId = notification.Id,
                RouteId = route.Id,
                ProviderId = route.ProviderId,
                PayloadJson = DeliveryRouting.CreatePayload(notification, route.IncludeMessage),
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            if (await _repository.EnqueueAsync(item, ct))
                enqueued++;
        }

        if (enqueued > 0)
            _signalDispatcher();
        return enqueued;
    }
}
