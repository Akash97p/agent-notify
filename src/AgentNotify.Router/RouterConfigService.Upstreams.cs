using System.Text.RegularExpressions;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Router;

public sealed partial class RouterConfigService
{
    public async Task<RouterUpstream> CreateUpstreamAsync(
        string? slug,
        string? label,
        string? wire,
        string? baseUrl,
        string? apiKey,
        IReadOnlyList<string>? models,
        bool enabled = true,
        CancellationToken ct = default,
        string? auth = null,
        IReadOnlyDictionary<string, string>? modelWires = null,
        string? credentialRef = null)
    {
        var normalizedSlug = NormalizeSlug(slug);
        var normalizedLabel = NormalizeLabel(label);
        var normalizedWire = RouterWire.Normalize(wire);
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedModels = NormalizeModels(models);
        var normalizedAuth = RouterAuth.Normalize(auth);
        var normalizedWires = NormalizeModelWires(modelWires, normalizedModels, normalizedWire);
        // A typed key replaces any reference; otherwise the reference, when given, is the credential.
        var normalizedRef = string.IsNullOrEmpty(apiKey) ? RouterCredentialRef.Normalize(credentialRef, normalizedAuth) : null;
        string? encryptedKey = null;
        // A subscription upstream reuses another tool's sign-in; there is no key to keep.
        if (!string.IsNullOrEmpty(apiKey) && !RouterAuth.IsSubscription(normalizedAuth))
        {
            ValidateKey(apiKey);
            encryptedKey = _protector.Protect(apiKey);
        }

        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var existing = await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false);
        if (existing.Count >= MaxUpstreams)
            throw new ArgumentException("Up to 32 upstreams can be stored.");
        if (existing.Any(u => string.Equals(u.Slug, normalizedSlug, StringComparison.Ordinal)))
            throw new ArgumentException($"Slug '{normalizedSlug}' is already in use.");

        var now = _clock.GetUtcNow();
        var stored = new StoredRouterUpstream(
            NewId("ru"), normalizedSlug, normalizedLabel, normalizedWire, normalizedBaseUrl,
            encryptedKey, normalizedModels, enabled, now, now)
        {
            Auth = normalizedAuth,
            ModelWires = normalizedWires,
            CredentialRef = normalizedRef
        };
        await _repository.InsertUpstreamAsync(stored, ct).ConfigureAwait(false);
        Invalidate();
        return ToPublic(stored);
    }

    public async Task<RouterUpstream> UpdateUpstreamAsync(
        string id,
        string? slug,
        string? label,
        string? wire,
        string? baseUrl,
        string? apiKey,
        IReadOnlyList<string>? models,
        bool? enabled = null,
        bool clearKey = false,
        CancellationToken ct = default,
        string? auth = null,
        IReadOnlyDictionary<string, string>? modelWires = null,
        string? credentialRef = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required.");
        var normalizedSlug = NormalizeSlug(slug);
        var normalizedLabel = NormalizeLabel(label);
        var normalizedWire = RouterWire.Normalize(wire);
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedModels = NormalizeModels(models);

        var hasNewKey = !string.IsNullOrEmpty(apiKey);
        if (hasNewKey) ValidateKey(apiKey);

        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var existing = await _repository.GetUpstreamAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("That upstream was not found.");
        var all = await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false);
        if (all.Any(u => u.Id != id && string.Equals(u.Slug, normalizedSlug, StringComparison.Ordinal)))
            throw new ArgumentException($"Slug '{normalizedSlug}' is already in use.");

        // Omitted means unchanged, so an older client or a partial edit keeps what is stored.
        var normalizedAuth = auth is null ? existing.Auth : RouterAuth.Normalize(auth);
        var normalizedWires = NormalizeModelWires(modelWires ?? existing.ModelWires, normalizedModels, normalizedWire);

        // Omitted keeps the reference, an empty string removes it, and a typed key or clearing the key
        // replaces it: the upstream has exactly one credential source at a time.
        var normalizedRef = hasNewKey || clearKey ? null
            : credentialRef is null ? existing.CredentialRef
            : RouterCredentialRef.Normalize(credentialRef, normalizedAuth);
        // A kept reference that no longer fits the upstream's kind (an API account on what is now a
        // subscription) is dropped rather than left pointing at the wrong thing.
        if (normalizedRef is not null)
        {
            try { RouterCredentialRef.Normalize(normalizedRef, normalizedAuth); }
            catch (ArgumentException) { normalizedRef = null; }
        }

        string? encryptedKey;
        if (RouterAuth.IsSubscription(normalizedAuth) || RouterCredentialRef.ApiAccountId(normalizedRef) is not null)
            encryptedKey = null;
        else if (clearKey)
            encryptedKey = null;
        else if (hasNewKey)
            encryptedKey = _protector.Protect(apiKey!);
        else
            encryptedKey = existing.EncryptedKey;

        var now = _clock.GetUtcNow();
        var updated = existing with
        {
            Slug = normalizedSlug,
            Label = normalizedLabel,
            Wire = normalizedWire,
            BaseUrl = normalizedBaseUrl,
            EncryptedKey = encryptedKey,
            Models = normalizedModels,
            Enabled = enabled ?? existing.Enabled,
            UpdatedAt = now,
            Auth = normalizedAuth,
            ModelWires = normalizedWires,
            CredentialRef = normalizedRef
        };
        await _repository.UpdateUpstreamAsync(updated, ct).ConfigureAwait(false);
        Invalidate();
        return ToPublic(updated);
    }

    public async Task DeleteUpstreamAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required.");
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var existing = await _repository.GetUpstreamAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("That upstream was not found.");
        var routes = await _repository.ListRoutesAsync(ct).ConfigureAwait(false);
        foreach (var route in routes)
        {
            foreach (var target in route.Targets)
            {
                var slash = target.IndexOf('/');
                var slug = slash >= 0 ? target[..slash] : target;
                if (string.Equals(slug, existing.Slug, StringComparison.Ordinal))
                    throw new ArgumentException($"Upstream '{existing.Slug}' is referenced by route '{route.Name}'.");
            }
        }
        var settings = await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings.DefaultRoute is not null)
        {
            var dr = settings.DefaultRoute.Trim();
            if (IsDefaultRouteReferencingSlug(dr, existing.Slug, routes))
                throw new ArgumentException($"Upstream '{existing.Slug}' is referenced by the default route.");
        }
        if (settings.ClaudeFallbackRoute is not null &&
            IsDefaultRouteReferencingSlug(settings.ClaudeFallbackRoute.Trim(), existing.Slug, routes))
            throw new ArgumentException($"Upstream '{existing.Slug}' is referenced by the Claude fallback route.");
        var deleted = await _repository.DeleteUpstreamAsync(id, ct).ConfigureAwait(false);
        if (!deleted) throw new KeyNotFoundException("That upstream was not found.");
        Invalidate();
    }

    public async Task<IReadOnlyList<RouterUpstream>> ListUpstreamsAsync(CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var stored = await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false);
        return stored.Select(ToPublic).ToList();
    }

    public async Task<StoredRouterUpstream?> GetStoredUpstreamAsync(string id, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        return await _repository.GetUpstreamAsync(id, ct).ConfigureAwait(false);
    }


}
