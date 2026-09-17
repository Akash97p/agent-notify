namespace AgentNotify.Core.Router;

/// <summary>One concrete destination: an upstream and the model ID that upstream calls it.</summary>
public sealed record ResolvedTarget(StoredRouterUpstream Upstream, string NativeModel);

/// <summary>
/// The outcome of resolving a client's model selector. On success <see cref="Targets"/> is the
/// ordered attempt list; a combo contributes more than one.
/// </summary>
public sealed record RouteResolution(
    IReadOnlyList<ResolvedTarget> Targets,
    string? RouteKind,
    string? RouteName,
    string? ErrorCode,
    int? HttpStatus)
{
    public bool IsSuccess => ErrorCode is null;
}

/// <summary>
/// Turns the model string an agent asked for into the targets to try, from one configuration
/// snapshot. It is pure: the same snapshot and selector always resolve the same way, which is what
/// makes a ledger row explainable after the fact.
/// </summary>
public static class RouteResolver
{
    private const string ComboPrefix = "combo/";

    public static RouteResolution Resolve(RouterSnapshot snapshot, string? requestedModel)
    {
        var model = requestedModel?.Trim();

        if (string.IsNullOrEmpty(model))
            return ResolveDefault(snapshot) ?? Fail("missing_model", 400);

        if (ByName(snapshot, model) is { } named)
            return named;

        if (ByExplicitTarget(snapshot, model, RouterRouteKind.Explicit) is { } explicitTarget)
            return explicitTarget;

        var declaring = snapshot.Upstreams
            .Where(upstream => upstream.Enabled && upstream.Models.Contains(model, StringComparer.Ordinal))
            .ToList();
        if (declaring.Count == 1)
            return Success([new ResolvedTarget(declaring[0], model)], RouterRouteKind.ModelList, null);
        if (declaring.Count > 1)
            return Fail("ambiguous_model", 400);

        return ResolveDefault(snapshot) ?? Fail("unknown_model", 404);
    }

    /// <summary>Rule 1: <c>combo/&lt;name&gt;</c>, or a bare name equal to an enabled route's name.</summary>
    private static RouteResolution? ByName(RouterSnapshot snapshot, string model)
    {
        if (model.StartsWith(ComboPrefix, StringComparison.Ordinal))
        {
            var route = FindRoute(snapshot, model[ComboPrefix.Length..]);
            return route is null
                ? Fail("unknown_model", 404)
                : Expand(snapshot, route, RouteKindOf(route), route.Name);
        }

        var named = FindRoute(snapshot, model);
        return named is null ? null : Expand(snapshot, named, RouteKindOf(named), named.Name);
    }

    /// <summary>Rule 2: <c>slug/model</c>, where the native ID is everything after the first slash.</summary>
    private static RouteResolution? ByExplicitTarget(RouterSnapshot snapshot, string selector, string routeKind)
    {
        var slash = selector.IndexOf('/');
        if (slash <= 0 || slash == selector.Length - 1) return null;

        var upstream = FindUpstream(snapshot, selector[..slash]);
        return upstream is null
            ? null
            : Success([new ResolvedTarget(upstream, selector[(slash + 1)..])], routeKind, null);
    }

    /// <summary>Rule 4: the default route, resolved by rules 1–2 only.</summary>
    private static RouteResolution? ResolveDefault(RouterSnapshot snapshot)
    {
        var configured = snapshot.Settings.DefaultRoute?.Trim();
        if (string.IsNullOrEmpty(configured)) return null;

        if (configured.StartsWith(ComboPrefix, StringComparison.Ordinal))
        {
            var combo = FindRoute(snapshot, configured[ComboPrefix.Length..]);
            return combo is null ? null : Expand(snapshot, combo, RouterRouteKind.Default, combo.Name);
        }

        var route = FindRoute(snapshot, configured);
        if (route is not null)
            return Expand(snapshot, route, RouterRouteKind.Default, route.Name);

        return ByExplicitTarget(snapshot, configured, RouterRouteKind.Default);
    }

    private static RouteResolution Expand(RouterSnapshot snapshot, RouterRoute route, string routeKind, string routeName)
    {
        var targets = new List<ResolvedTarget>();
        foreach (var target in route.Targets)
        {
            var slash = target.IndexOf('/');
            if (slash <= 0 || slash == target.Length - 1) continue;
            if (FindUpstream(snapshot, target[..slash]) is { } upstream)
                targets.Add(new ResolvedTarget(upstream, target[(slash + 1)..]));
        }

        return targets.Count == 0
            ? Fail("no_enabled_target", 503)
            : Success(targets, routeKind, routeName);
    }

    private static string RouteKindOf(RouterRoute route) =>
        route.Kind == RouterKind.Combo ? RouterRouteKind.Combo : RouterRouteKind.Alias;

    private static RouterRoute? FindRoute(RouterSnapshot snapshot, string name) =>
        string.IsNullOrEmpty(name)
            ? null
            : snapshot.Routes.FirstOrDefault(route =>
                route.Enabled && string.Equals(route.Name, name, StringComparison.Ordinal));

    private static StoredRouterUpstream? FindUpstream(RouterSnapshot snapshot, string slug) =>
        snapshot.Upstreams.FirstOrDefault(upstream =>
            upstream.Enabled && string.Equals(upstream.Slug, slug, StringComparison.Ordinal));

    private static RouteResolution Success(IReadOnlyList<ResolvedTarget> targets, string routeKind, string? routeName) =>
        new(targets, routeKind, routeName, null, null);

    private static RouteResolution Fail(string code, int status) => new([], null, null, code, status);
}
