using System.Text.RegularExpressions;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Router;

public sealed partial class RouterConfigService
{
    public async Task SetSwitchSettingsAsync(string? strategy, string? claudeFallbackRoute, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var normalizedStrategy = RouterSwitchStrategy.Normalize(strategy);
        var normalizedFallback = string.IsNullOrWhiteSpace(claudeFallbackRoute) ? null : claudeFallbackRoute.Trim();
        if (normalizedFallback is not null)
        {
            ValidateDefaultRoute(normalizedFallback,
                await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false),
                await _repository.ListRoutesAsync(ct).ConfigureAwait(false));
        }
        var current = await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
        await _repository.SetSettingsAsync(current with
        {
            SwitchStrategy = normalizedStrategy,
            ClaudeFallbackRoute = normalizedFallback
        }, ct).ConfigureAwait(false);
        Invalidate();
    }


    private static string NormalizeSlug(string? slug)
    {
        var s = slug?.Trim() ?? "";
        if (!SlugRegex.IsMatch(s))
            throw new ArgumentException("Slug must match ^[a-z0-9][a-z0-9-]{0,31}$.");
        return s;
    }

    private static string NormalizeLabel(string? label)
    {
        var t = label?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 60 || t.Any(char.IsControl))
            throw new ArgumentException("Label must be 1–60 characters without control characters.");
        return t;
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        if (!RouterDestination.TryValidateBaseUrl(baseUrl, out _, out var normalized, out var error))
            throw new ArgumentException(error ?? "Enter a valid base URL.");
        return normalized!;
    }

    private static void ValidateKey(string? key)
    {
        if (key is not { Length: >= 8 and <= 512 } || key.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("Key must be 8–512 printable characters without whitespace.");
    }

    private static IReadOnlyList<string> NormalizeModels(IReadOnlyList<string>? models)
    {
        if (models is null) return [];
        if (models.Count > 500)
            throw new ArgumentException("Up to 500 models can be declared.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var m in models)
        {
            if (string.IsNullOrEmpty(m) || m.Length > 200 || m.Any(c => c < '!' || c > '~'))
                throw new ArgumentException("Each model must be 1–200 printable characters without whitespace.");
            if (seen.Add(m))
                result.Add(m);
        }
        return result;
    }

    /// <summary>
    /// Keeps only overrides for declared models that actually differ from the upstream's own wire, so
    /// removing a model also drops its override.
    /// </summary>
    private static IReadOnlyDictionary<string, string> NormalizeModelWires(
        IReadOnlyDictionary<string, string>? modelWires, IReadOnlyList<string> models, string wire)
    {
        if (modelWires is null || modelWires.Count == 0) return RouterUpstreamWires.None;
        var declared = new HashSet<string>(models, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (model, modelWire) in modelWires)
        {
            if (!RouterWire.IsValid(modelWire))
                throw new ArgumentException($"The wire for '{model}' must be one of openai_responses, openai_chat, anthropic_messages.");
            if (declared.Contains(model) && modelWire != wire) result[model] = modelWire;
        }
        return result;
    }

    private static string NormalizeRouteName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (!RouteNameRegex.IsMatch(trimmed))
            throw new ArgumentException("A route name uses 1–64 characters: lower-case letters, digits, dot, underscore, or hyphen.");
        return trimmed;
    }

    private static string NormalizeKind(string? kind)
    {
        if (!RouterKind.IsValid(kind))
            throw new ArgumentException("Kind must be alias or combo.");
        return kind!;
    }

    private static IReadOnlyList<string> NormalizeTargets(IReadOnlyList<string>? targets, string kind, IReadOnlyList<StoredRouterUpstream> upstreams)
    {
        if (targets is null || targets.Count == 0)
            throw new ArgumentException("At least one target is required.");
        if (kind == RouterKind.Alias && targets.Count != 1)
            throw new ArgumentException("Alias must have exactly one target.");
        if (kind == RouterKind.Combo && (targets.Count < 1 || targets.Count > 8))
            throw new ArgumentException("Combo must have 1–8 targets.");
        var slugs = new HashSet<string>(upstreams.Select(u => u.Slug), StringComparer.Ordinal);
        var normalized = new List<string>();
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t))
                throw new ArgumentException("Target must be slug/model.");
            var trimmed = t.Trim();
            var slash = trimmed.IndexOf('/');
            if (slash <= 0 || slash == trimmed.Length - 1)
                throw new ArgumentException($"Target '{trimmed}' must be slug/model.");
            var slug = trimmed[..slash];
            var model = trimmed[(slash + 1)..];
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException($"Target '{trimmed}' must be slug/model.");
            if (!slugs.Contains(slug))
                throw new ArgumentException($"Unknown upstream slug '{slug}'.");
            normalized.Add(trimmed);
        }
        return normalized;
    }

    private static void ValidateDefaultRoute(string route, IReadOnlyList<StoredRouterUpstream> upstreams, IReadOnlyList<RouterRoute> routes)
    {
        var routeNames = new HashSet<string>(routes.Select(r => r.Name), StringComparer.Ordinal);
        var slugs = new HashSet<string>(upstreams.Select(u => u.Slug), StringComparer.Ordinal);
        if (route.StartsWith("combo/", StringComparison.Ordinal))
        {
            var name = route["combo/".Length..];
            if (string.IsNullOrEmpty(name) || !routeNames.Contains(name))
                throw new ArgumentException($"Unknown route '{name}'.");
            return;
        }
        if (routeNames.Contains(route)) return;
        var slash = route.IndexOf('/');
        if (slash > 0 && slash < route.Length - 1)
        {
            var slug = route[..slash];
            if (!slugs.Contains(slug))
                throw new ArgumentException($"Unknown upstream slug '{slug}'.");
            return;
        }
        throw new ArgumentException($"Default route '{route}' must be an existing route name, combo/<name>, or slug/model.");
    }

    /// <summary>
    /// Whether the default route would still send traffic to this upstream, either directly as
    /// <c>slug/model</c> or through the route it names.
    /// </summary>
    private static bool IsDefaultRouteReferencingSlug(string defaultRoute, string slug, IReadOnlyList<RouterRoute> routes)
    {
        var name = defaultRoute.StartsWith("combo/", StringComparison.Ordinal)
            ? defaultRoute["combo/".Length..]
            : defaultRoute;

        if (routes.FirstOrDefault(route => route.Name == name) is { } named)
            return named.Targets.Any(target => target.StartsWith(slug + "/", StringComparison.Ordinal));

        return defaultRoute.StartsWith(slug + "/", StringComparison.Ordinal);
    }

    private static RouterUpstream ToPublic(StoredRouterUpstream s) =>
        new(s.Id, s.Slug, s.Label, s.Wire, s.BaseUrl, s.EncryptedKey is not null, s.Models, s.Enabled, s.CreatedAt, s.UpdatedAt)
        {
            Auth = s.Auth,
            ModelWires = s.ModelWires,
            CredentialRef = s.CredentialRef
        };
}
