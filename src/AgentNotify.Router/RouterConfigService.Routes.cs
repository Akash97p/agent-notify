using System.Text.RegularExpressions;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Router;

public sealed partial class RouterConfigService
{
    public async Task<RouterRoute> CreateRouteAsync(
        string? name,
        string? kind,
        IReadOnlyList<string>? targets,
        bool enabled = true,
        CancellationToken ct = default)
    {
        var normalizedName = NormalizeRouteName(name);
        var normalizedKind = NormalizeKind(kind);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var routes = await _repository.ListRoutesAsync(ct).ConfigureAwait(false);
        if (routes.Count >= MaxRoutes)
            throw new ArgumentException("Up to 64 routes can be stored.");
        if (routes.Any(r => string.Equals(r.Name, normalizedName, StringComparison.Ordinal)))
            throw new ArgumentException($"Route name '{normalizedName}' is already in use.");
        var upstreams = await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false);
        if (upstreams.Any(u => string.Equals(u.Slug, normalizedName, StringComparison.Ordinal)))
            throw new ArgumentException($"Route name '{normalizedName}' conflicts with an upstream slug.");
        var normalizedTargets = NormalizeTargets(targets, normalizedKind, upstreams);

        var now = _clock.GetUtcNow();
        var route = new RouterRoute(NewId("rr"), normalizedName, normalizedKind, normalizedTargets, enabled, now, now);
        await _repository.InsertRouteAsync(route, ct).ConfigureAwait(false);
        Invalidate();
        return route;
    }

    public async Task<RouterRoute> UpdateRouteAsync(
        string id,
        string? name,
        string? kind,
        IReadOnlyList<string>? targets,
        bool? enabled = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required.");
        var normalizedName = NormalizeRouteName(name);
        var normalizedKind = NormalizeKind(kind);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var existing = await _repository.GetRouteAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("That route was not found.");
        var routes = await _repository.ListRoutesAsync(ct).ConfigureAwait(false);
        if (routes.Any(r => r.Id != id && string.Equals(r.Name, normalizedName, StringComparison.Ordinal)))
            throw new ArgumentException($"Route name '{normalizedName}' is already in use.");
        var upstreams = await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false);
        if (upstreams.Any(u => string.Equals(u.Slug, normalizedName, StringComparison.Ordinal)))
            throw new ArgumentException($"Route name '{normalizedName}' conflicts with an upstream slug.");
        var normalizedTargets = NormalizeTargets(targets, normalizedKind, upstreams);

        var now = _clock.GetUtcNow();
        var updated = existing with
        {
            Name = normalizedName,
            Kind = normalizedKind,
            Targets = normalizedTargets,
            Enabled = enabled ?? existing.Enabled,
            UpdatedAt = now
        };
        await _repository.UpdateRouteAsync(updated, ct).ConfigureAwait(false);
        Invalidate();
        return updated;
    }

    public async Task DeleteRouteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required.");
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var existing = await _repository.GetRouteAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("That route was not found.");
        var settings = await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
        var configured = settings.DefaultRoute?.Trim();
        if (configured is not null &&
            (configured == existing.Name || configured == "combo/" + existing.Name))
            throw new ArgumentException($"Route '{existing.Name}' is the default route. Change the default first.");
        var claudeFallback = settings.ClaudeFallbackRoute?.Trim();
        if (claudeFallback is not null &&
            (claudeFallback == existing.Name || claudeFallback == "combo/" + existing.Name))
            throw new ArgumentException($"Route '{existing.Name}' is the Claude fallback route. Change it first.");
        var deleted = await _repository.DeleteRouteAsync(id, ct).ConfigureAwait(false);
        if (!deleted) throw new KeyNotFoundException("That route was not found.");
        Invalidate();
    }

    public async Task<IReadOnlyList<RouterRoute>> ListRoutesAsync(CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        return await _repository.ListRoutesAsync(ct).ConfigureAwait(false);
    }


}
