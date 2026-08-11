using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery;

public sealed class DeliveryRouteService
{
    private readonly IDeliveryRepository _repository;

    public DeliveryRouteService(IDeliveryRepository repository) => _repository = repository;

    public Task<IReadOnlyList<DeliveryRoute>> ListAsync(CancellationToken ct = default) =>
        _repository.ListRoutesAsync(ct);

    public async Task<DeliveryRoute> SaveAsync(
        string? id,
        string name,
        string providerId,
        bool enabled,
        NotificationPriority minimumPriority,
        string? typeId,
        string? project,
        string? agent,
        bool includeMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException("Route name is required and must be at most 100 characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(providerId) ||
            await _repository.GetProviderAsync(providerId, ct) is null)
            throw new ArgumentException("Select an existing provider.", nameof(providerId));

        var normalizedType = string.IsNullOrWhiteSpace(typeId)
            ? null
            : NotificationTypes.Normalize(typeId) ??
              throw new ArgumentException("Route notification type is invalid.", nameof(typeId));
        var normalizedProject = NormalizeOptional(project, nameof(project));
        var normalizedAgent = NormalizeOptional(agent, nameof(agent));
        var existing = (await _repository.ListRoutesAsync(ct))
            .FirstOrDefault(route => string.Equals(route.Id, id, StringComparison.Ordinal));
        var now = DateTimeOffset.UtcNow;
        var route = new DeliveryRoute
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            ProviderId = providerId,
            Enabled = enabled,
            MinimumPriority = minimumPriority,
            TypeId = normalizedType,
            Project = normalizedProject,
            Agent = normalizedAgent,
            IncludeMessage = includeMessage,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
        await _repository.UpsertRouteAsync(route, ct);
        return route;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        _repository.DeleteRouteAsync(id, ct);

    private static string? NormalizeOptional(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > 100)
            throw new ArgumentException("Route filter must be at most 100 characters.", parameterName);
        return normalized;
    }
}
