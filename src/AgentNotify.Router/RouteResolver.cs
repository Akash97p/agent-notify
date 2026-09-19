namespace AgentNotify.Router;

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

    /// <param name="nativeAnthropic">
    /// The client brought its own Anthropic credential, so an Anthropic model it asks for by its bare ID
    /// goes to Anthropic itself (rule 3) unless the owner named a route that way.
    /// </param>
    public static RouteResolution Resolve(RouterSnapshot snapshot, string? requestedModel, bool nativeAnthropic = false)
    {
        var resolution = ResolveCore(snapshot, requestedModel, nativeAnthropic);
        if (!snapshot.Settings.SmartRouting || !resolution.IsSuccess) return resolution;
        return resolution.RouteKind == RouterRouteKind.Native
            ? WithNativeClaudeFallbacks(snapshot, resolution)
            : WithSameModelElsewhere(snapshot, resolution);
    }

    private static RouteResolution ResolveCore(RouterSnapshot snapshot, string? requestedModel, bool nativeAnthropic)
    {
        var model = requestedModel?.Trim();

        if (string.IsNullOrEmpty(model))
            return ResolveDefault(snapshot) ?? Fail("missing_model", 400);

        if (ByName(snapshot, model) is { } named)
            return named;

        if (ByExplicitTarget(snapshot, model, RouterRouteKind.Explicit) is { } explicitTarget)
            return explicitTarget;

        // Before the declared-model rule: a provider that happens to list claude-opus-5 must not take
        // over the agent's own Opus. Only a route the owner named that way (rule 1) does.
        if (nativeAnthropic && RouterNative.IsNativeModel(model))
            return Success([new ResolvedTarget(RouterNative.Upstream, model)], RouterRouteKind.Native, null);

        var declaring = snapshot.Upstreams
            .Where(upstream => upstream.Enabled && upstream.Models.Contains(model, StringComparer.Ordinal))
            .ToList();
        if (declaring.Count == 1)
            return Success([Target(declaring[0], model)], RouterRouteKind.ModelList, null);
        // Several providers serve it: smart routing uses them all (it orders them below), and without it
        // the router will not guess which one the owner meant.
        if (declaring.Count > 1 && snapshot.Settings.SmartRouting)
            return Success([Target(declaring[0], model)], RouterRouteKind.ModelList, null);
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
            : Success([Target(upstream, selector[(slash + 1)..])], routeKind, null);
    }

    /// <summary>Rule 5: the default route, resolved by rules 1–2 only.</summary>
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
                targets.Add(Target(upstream, target[(slash + 1)..]));
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

    /// <summary>
    /// One concrete target. A provider that serves different models over different wires (OpenCode
    /// Zen and Go do) records that per model, so the upstream is narrowed to the wire this model uses
    /// and everything downstream keeps reading <c>Upstream.Wire</c>.
    /// </summary>
    private static ResolvedTarget Target(StoredRouterUpstream upstream, string model)
    {
        var wire = upstream.WireFor(model);
        return new ResolvedTarget(wire == upstream.Wire ? upstream : upstream with { Wire = wire }, model);
    }

    /// <summary>
    /// Smart routing: after the targets the request resolved to, every other enabled provider that
    /// serves one of the same models, cheapest kind first (<see cref="CostTier"/>), so a limit or an
    /// outage at one provider hands the request to the same model somewhere else. Models are the same
    /// when their IDs match ignoring case and any vendor path (<c>deepseek/deepseek-v4-flash</c> at
    /// OpenRouter is <c>deepseek-v4-flash</c>). A bare model several providers list starts from the
    /// cheapest of them rather than from whichever was added first.
    /// </summary>
    private static RouteResolution WithSameModelElsewhere(RouterSnapshot snapshot, RouteResolution resolution)
    {
        var targets = resolution.Targets.ToList();
        if (resolution.RouteKind == RouterRouteKind.ModelList)
            targets.Clear();
        var wanted = resolution.Targets.Select(target => ModelKey(target.NativeModel)).ToHashSet(StringComparer.Ordinal);
        var taken = targets.Select(target => (target.Upstream.Slug, target.NativeModel)).ToHashSet();

        var elsewhere = snapshot.Upstreams
            .Select((upstream, order) => (upstream, order))
            .Where(item => item.upstream.Enabled)
            .SelectMany(item => item.upstream.Models
                .Where(model => wanted.Contains(ModelKey(model)))
                .Select(model => (item.upstream, item.order, model)))
            .Where(item => !taken.Contains((item.upstream.Slug, item.model)))
            .OrderBy(item => CostTier(item.upstream))
            .ThenBy(item => item.order)
            .Select(item => Target(item.upstream, item.model));
        targets.AddRange(elsewhere);
        return Success(targets, resolution.RouteKind!, resolution.RouteName);
    }

    private static RouteResolution WithNativeClaudeFallbacks(RouterSnapshot snapshot, RouteResolution resolution)
    {
        var targets = resolution.Targets.ToList();
        var nativeModel = resolution.Targets[0].NativeModel;
        var wanted = ModelKey(nativeModel);
        targets.AddRange(snapshot.Upstreams
            .Select((upstream, order) => (upstream, order))
            .Where(item => item.upstream.Enabled)
            .SelectMany(item => item.upstream.Models
                .Where(model => ModelKey(model) == wanted)
                .Select(model => (item.upstream, item.order, model)))
            .OrderBy(item => CostTier(item.upstream))
            .ThenBy(item => item.order)
            .Select(item => Target(item.upstream, item.model)));

        if (!string.IsNullOrWhiteSpace(snapshot.Settings.ClaudeFallbackRoute))
        {
            var fallback = ResolveCore(snapshot, snapshot.Settings.ClaudeFallbackRoute, nativeAnthropic: false);
            if (fallback.IsSuccess) targets.AddRange(fallback.Targets);
        }

        var seen = new HashSet<(string Slug, string Model)>();
        var unique = targets.Where(target => seen.Add((target.Upstream.Slug, target.NativeModel))).ToList();
        return Success(unique, resolution.RouteKind!, resolution.RouteName);
    }

    /// <summary>A model's identity across providers: its last path segment, ignoring case.</summary>
    public static string ModelKey(string model)
    {
        var slash = model.LastIndexOf('/');
        return (slash >= 0 ? model[(slash + 1)..] : model).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Which providers smart routing tries first: a plan already paid for through another tool's
    /// sign-in (0), OpenCode Go's flat monthly plan (1), a server on this computer (2), then anything
    /// billed per token (3), which is always last because it is the one that costs more with every call.
    /// </summary>
    public static int CostTier(StoredRouterUpstream upstream) => CostTier(upstream.Auth, upstream.BaseUrl);

    public static int CostTier(string auth, string baseUrl)
    {
        if (RouterAuth.IsSubscription(auth)) return 0;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return 3;
        if (uri.Host.EndsWith("opencode.ai", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Contains("/zen/go", StringComparison.OrdinalIgnoreCase)) return 1;
        if (uri.IsLoopback) return 2;
        return 3;
    }

    private static RouteResolution Success(IReadOnlyList<ResolvedTarget> targets, string routeKind, string? routeName) =>
        new(targets, routeKind, routeName, null, null);

    private static RouteResolution Fail(string code, int status) => new([], null, null, code, status);
}
