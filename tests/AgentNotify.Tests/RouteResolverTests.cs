using AgentNotify.Core.Router;

namespace AgentNotify.Tests;

public sealed class RouteResolverTests
{
    private static StoredRouterUpstream Upstream(string slug, bool enabled = true, string[]? models = null, string wire = RouterWire.OpenAiChat) =>
        new($"id_{slug}", slug, slug, wire, "https://api.example.com/v1", null, models ?? [], enabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static RouterRoute AliasRoute(string name, string target, bool enabled = true) =>
        new($"id_{name}", name, RouterKind.Alias, [target], enabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static RouterRoute ComboRoute(string name, string[] targets, bool enabled = true) =>
        new($"id_{name}", name, RouterKind.Combo, targets, enabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static RouterSnapshot Snapshot(
        StoredRouterUpstream[] upstreams,
        RouterRoute[] routes,
        string? defaultRoute = null,
        long gen = 0) =>
        new(upstreams, routes, new RouterSettings(defaultRoute), gen);

    [Fact]
    public void ComboByNameResolves()
    {
        var snap = Snapshot(
            [Upstream("openai"), Upstream("anthropic")],
            [ComboRoute("coding", ["openai/gpt-4o", "anthropic/claude"])]);
        var res = RouteResolver.Resolve(snap, "combo/coding");
        Assert.Null(res.ErrorCode);
        Assert.Equal(2, res.Targets.Count);
        Assert.Equal("openai", res.Targets[0].Upstream.Slug);
        Assert.Equal("gpt-4o", res.Targets[0].NativeModel);
        Assert.Equal("anthropic", res.Targets[1].Upstream.Slug);
        Assert.Equal(RouterRouteKind.Combo, res.RouteKind);
        Assert.Equal("coding", res.RouteName);
    }

    [Fact]
    public void BareRouteNameResolves()
    {
        var snap = Snapshot(
            [Upstream("openai")],
            [AliasRoute("my-alias", "openai/gpt-4o")]);
        var res = RouteResolver.Resolve(snap, "my-alias");
        Assert.Null(res.ErrorCode);
        Assert.Single(res.Targets);
        Assert.Equal("openai", res.Targets[0].Upstream.Slug);
        Assert.Equal(RouterRouteKind.Alias, res.RouteKind);
    }

    [Fact]
    public void BareRouteNamePrecedenceOverExplicitAndModelList()
    {
        // Bare model that equals a route name resolves the route, even if some upstream declares that model
        var snap = Snapshot(
            [Upstream("openai", models: ["my-alias"])],
            [AliasRoute("my-alias", "openai/gpt-4o")]);
        var res = RouteResolver.Resolve(snap, "my-alias");
        Assert.Null(res.ErrorCode);
        Assert.Equal(RouterRouteKind.Alias, res.RouteKind);
        Assert.Equal("gpt-4o", res.Targets[0].NativeModel);
    }

    [Fact]
    public void ExplicitSlugModelResolves()
    {
        var snap = Snapshot(
            [Upstream("openai"), Upstream("anthropic")],
            []);
        var res = RouteResolver.Resolve(snap, "openai/gpt-4o");
        Assert.Null(res.ErrorCode);
        Assert.Single(res.Targets);
        Assert.Equal("openai", res.Targets[0].Upstream.Slug);
        Assert.Equal("gpt-4o", res.Targets[0].NativeModel);
        Assert.Equal(RouterRouteKind.Explicit, res.RouteKind);
    }

    [Fact]
    public void ExplicitNativeModelAfterFirstSlash()
    {
        var snap = Snapshot([Upstream("openrouter")], []);
        var res = RouteResolver.Resolve(snap, "openrouter/anthropic/claude-sonnet-4.5");
        Assert.Null(res.ErrorCode);
        Assert.Equal("anthropic/claude-sonnet-4.5", res.Targets[0].NativeModel);
    }

    [Fact]
    public void DeclaredModelListUniqueMatch()
    {
        var snap = Snapshot(
            [Upstream("openai", models: ["gpt-4o"]), Upstream("anthropic", models: ["claude"])],
            []);
        var res = RouteResolver.Resolve(snap, "claude");
        Assert.Null(res.ErrorCode);
        Assert.Equal("anthropic", res.Targets[0].Upstream.Slug);
        Assert.Equal(RouterRouteKind.ModelList, res.RouteKind);
    }

    [Fact]
    public void DeclaredModelListAmbiguousFails()
    {
        var snap = Snapshot(
            [Upstream("openai", models: ["gpt-4o"]), Upstream("deepseek", models: ["gpt-4o"])],
            []);
        var res = RouteResolver.Resolve(snap, "gpt-4o");
        Assert.Equal("ambiguous_model", res.ErrorCode);
        Assert.Equal(400, res.HttpStatus);
    }

    [Fact]
    public void DeclaredModelListSkipsDisabled()
    {
        var snap = Snapshot(
            [Upstream("openai", enabled: false, models: ["gpt-4o"]), Upstream("deepseek", models: ["gpt-4o"])],
            []);
        var res = RouteResolver.Resolve(snap, "gpt-4o");
        Assert.Null(res.ErrorCode);
        Assert.Equal("deepseek", res.Targets[0].Upstream.Slug);
    }

    [Fact]
    public void DefaultRouteResolvedByRules1And2()
    {
        var snap = Snapshot(
            [Upstream("openai")],
            [AliasRoute("my-alias", "openai/gpt-4o")],
            defaultRoute: "my-alias");
        var res = RouteResolver.Resolve(snap, "unknown-model-xyz");
        Assert.Null(res.ErrorCode);
        Assert.Equal(RouterRouteKind.Default, res.RouteKind);
        Assert.Equal("my-alias", res.RouteName);

        var snap2 = Snapshot(
            [Upstream("openai")],
            [],
            defaultRoute: "openai/fallback");
        var res2 = RouteResolver.Resolve(snap2, "unknown-model-xyz");
        Assert.Null(res2.ErrorCode);
        Assert.Equal(RouterRouteKind.Default, res2.RouteKind);
        Assert.Equal("fallback", res2.Targets[0].NativeModel);
    }

    [Fact]
    public void DefaultRouteComboPrefix()
    {
        var snap = Snapshot(
            [Upstream("openai"), Upstream("anthropic")],
            [ComboRoute("coding", ["openai/gpt-4o", "anthropic/claude"])],
            defaultRoute: "combo/coding");
        var res = RouteResolver.Resolve(snap, "unknown-xyz");
        Assert.Null(res.ErrorCode);
        Assert.Equal(RouterRouteKind.Default, res.RouteKind);
        Assert.Equal(2, res.Targets.Count);
    }

    [Fact]
    public void UnknownModelReturns404()
    {
        var snap = Snapshot([Upstream("openai")], []);
        var res = RouteResolver.Resolve(snap, "unknown-model");
        Assert.Equal("unknown_model", res.ErrorCode);
        Assert.Equal(404, res.HttpStatus);
    }

    [Fact]
    public void MissingModelReturns400()
    {
        var snap = Snapshot([Upstream("openai")], []);
        Assert.Equal("missing_model", RouteResolver.Resolve(snap, null).ErrorCode);
        Assert.Equal("missing_model", RouteResolver.Resolve(snap, "").ErrorCode);
        Assert.Equal("missing_model", RouteResolver.Resolve(snap, "   ").ErrorCode);
        Assert.Equal(400, RouteResolver.Resolve(snap, null).HttpStatus);
    }

    [Fact]
    public void DisabledUpstreamsSkippedInCombo()
    {
        var snap = Snapshot(
            [Upstream("openai", enabled: false), Upstream("anthropic")],
            [ComboRoute("coding", ["openai/gpt-4o", "anthropic/claude"])]);
        var res = RouteResolver.Resolve(snap, "combo/coding");
        Assert.Null(res.ErrorCode);
        Assert.Single(res.Targets);
        Assert.Equal("anthropic", res.Targets[0].Upstream.Slug);
    }

    [Fact]
    public void ComboWithNoEnabledTargetFails503()
    {
        var snap = Snapshot(
            [Upstream("openai", enabled: false)],
            [ComboRoute("coding", ["openai/gpt-4o"])]);
        var res = RouteResolver.Resolve(snap, "combo/coding");
        Assert.Equal("no_enabled_target", res.ErrorCode);
        Assert.Equal(503, res.HttpStatus);
    }

    [Fact]
    public void DisabledRoutesIgnored()
    {
        var snap = Snapshot(
            [Upstream("openai")],
            [AliasRoute("my-alias", "openai/gpt-4o", enabled: false)]);
        var res = RouteResolver.Resolve(snap, "my-alias");
        Assert.Equal("unknown_model", res.ErrorCode);
    }

    [Fact]
    public void ComboUnknownNameFails()
    {
        var snap = Snapshot([Upstream("openai")], []);
        var res = RouteResolver.Resolve(snap, "combo/unknown");
        Assert.Equal("unknown_model", res.ErrorCode);
        Assert.Equal(404, res.HttpStatus);
    }

    [Fact]
    public void ExplicitDisabledUpstreamFallsThroughToDefault()
    {
        var snap = Snapshot(
            [Upstream("openai", enabled: false)],
            [AliasRoute("fallback", "openai/other")],
            defaultRoute: "openai/fallback-model");
        // explicit openai/gpt-4o has disabled upstream, so not resolved, then no model list, then default route tries explicit openai/fallback-model but also disabled -> unknown
        var res = RouteResolver.Resolve(snap, "openai/gpt-4o");
        Assert.Equal("unknown_model", res.ErrorCode);
    }

    [Fact]
    public void ExplicitTakesPrecedenceOverModelList()
    {
        var snap = Snapshot(
            [Upstream("openai", models: ["openai/gpt-4o"])],
            []);
        // "openai/gpt-4o" is explicit (slug openai) so should resolve as explicit, not model_list
        var res = RouteResolver.Resolve(snap, "openai/gpt-4o");
        Assert.Equal(RouterRouteKind.Explicit, res.RouteKind);
    }
}
