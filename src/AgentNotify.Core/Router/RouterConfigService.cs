using System.Text.RegularExpressions;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Core.Router;

/// <summary>
/// One consistent view of the router's configuration. Every routing decision is made against a
/// snapshot, so a change mid-request cannot move a request's target halfway through.
/// </summary>
public sealed record RouterSnapshot(
    IReadOnlyList<StoredRouterUpstream> Upstreams,
    IReadOnlyList<RouterRoute> Routes,
    RouterSettings Settings,
    long Generation,
    IReadOnlyList<RouterEffortMapping>? EffortMappings = null,
    IReadOnlyList<RouterEffortFamilyOverride>? EffortFamilyOverrides = null);

/// <summary>
/// Manages router upstreams, routes, and the default route. Upstream keys are sealed with the same
/// protector as channel credentials before they reach SQLite, and no public model carries one.
/// </summary>
public sealed class RouterConfigService
{
    public const int MaxUpstreams = 32;
    public const int MaxRoutes = 64;

    private static readonly Regex SlugRegex = new(@"^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex RouteNameRegex = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private readonly RouterRepository _repository;
    private readonly ISecretProtector _protector;
    private readonly ConfigStore _configStore;
    private readonly AgentNotifyConfig _config;
    private readonly TimeProvider _clock;

    private long _generation;
    private RouterSnapshot? _cached;
    private readonly object _cacheLock = new();

    /// <param name="config">
    /// The broker's live configuration object. The switch and the router key are read from it on
    /// every request, so turning the router on takes effect without a restart. When it is omitted
    /// the store is loaded once and that instance is used.
    /// </param>
    public RouterConfigService(
        RouterRepository repository,
        ISecretProtector protector,
        ConfigStore configStore,
        AgentNotifyConfig? config = null,
        TimeProvider? clock = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _config = config ?? configStore.Load();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The configuration the router reads its switch and key from.</summary>
    public AgentNotifyConfig Config => _config;

    /// <summary>
    /// Raised after upstreams, routes, the default route, or the key changed. A connected agent's
    /// generated model catalogue and embedded key are rewritten from here, so its own picker never
    /// lists a model this router can no longer resolve. Failures are the handler's problem: a
    /// configuration change must still succeed.
    /// </summary>
    public Action? Changed { get; set; }

    public static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The upstream's plaintext key, or null when it has none. Throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/> when the stored envelope
    /// cannot be opened on this machine; the caller reports that as a failed attempt rather than
    /// silently sending no credential.
    /// </summary>
    public string? DecryptKey(StoredRouterUpstream upstream) =>
        upstream.EncryptedKey is null ? null : _protector.Unprotect(upstream.EncryptedKey);

    public async Task<RouterSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        long generation;
        lock (_cacheLock)
        {
            if (_cached is not null) return _cached;
            generation = _generation;
        }

        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var upstreams = await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false);
        var routes = await _repository.ListRoutesAsync(ct).ConfigureAwait(false);
        var settings = await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
        var effortMappings = await _repository.ListEffortMappingsAsync(ct).ConfigureAwait(false);
        var effortFamilyOverrides = await _repository.ListEffortFamilyOverridesAsync(ct).ConfigureAwait(false);
        var snapshot = new RouterSnapshot(upstreams, routes, settings, generation, effortMappings, effortFamilyOverrides);

        lock (_cacheLock)
        {
            if (_generation == generation) _cached = snapshot;
        }
        return snapshot;
    }

    private void Invalidate()
    {
        lock (_cacheLock)
        {
            _generation++;
            _cached = null;
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { }
    }


    /// <summary>
    /// Turns the router on or off. Enabling it generates a key when there is none; the returned
    /// value is that newly generated key, and null when an existing key was kept.
    /// </summary>
    public Task<string?> SetRouterEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        string? generated = null;
        _config.RouterEnabled = enabled;
        if (enabled && string.IsNullOrWhiteSpace(_config.RouterKey))
            generated = _config.RouterKey = AgentNotifyConfig.GenerateRouterKey();
        Save();
        return Task.FromResult(generated);
    }

    /// <summary>Replaces the router key. Every agent configured with the old key stops working.</summary>
    public Task<string> RegenerateRouterKeyAsync(CancellationToken ct = default)
    {
        var key = AgentNotifyConfig.GenerateRouterKey();
        _config.RouterKey = key;
        Save();
        return Task.FromResult(key);
    }

    public Task<string?> GetRouterKeyAsync(CancellationToken ct = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(_config.RouterKey) ? null : _config.RouterKey);

    public Task<bool> GetRouterEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult(_config.RouterEnabled);

    private void Save()
    {
        _config.ApplyDefaults();
        _configStore.Save(_config);
        NotifyChanged();
    }


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


    public async Task<RouterSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        return await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task SetDefaultRouteAsync(string? defaultRoute, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var current = await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(defaultRoute))
        {
            await _repository.SetSettingsAsync(current with { DefaultRoute = null }, ct).ConfigureAwait(false);
            Invalidate();
            return;
        }
        var trimmed = defaultRoute.Trim();
        ValidateDefaultRoute(trimmed, await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false),
            await _repository.ListRoutesAsync(ct).ConfigureAwait(false));
        await _repository.SetSettingsAsync(current with { DefaultRoute = trimmed }, ct).ConfigureAwait(false);
        Invalidate();
    }

    /// <summary>
    /// Keeps one ChatGPT-plan provider per signed-in Codex account, once the owner has added the plan
    /// at all. Codex accounts are one list (<c>QuotaAccountDefinition.Monitored</c>, what Insights
    /// shows), so a second login is a second provider without being added by hand; with smart routing
    /// on, the same model then moves from one account to the next when the first runs out. A provider
    /// that duplicates another's account is pointed at an account nothing uses yet, keeping its slug so
    /// routes and agent pickers that name it still work. Returns whether anything changed.
    /// </summary>
    public async Task<bool> SyncCodexAccountsAsync(IReadOnlyList<CodexPlanAccount> accounts, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var plans = (await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false))
            .Where(upstream => upstream.Auth == RouterAuth.CodexChatGpt)
            .OrderBy(upstream => upstream.CreatedAt)
            .ToList();
        var signedIn = accounts.Where(account => account.SignedIn).ToList();
        if (plans.Count == 0 || signedIn.Count == 0) return false;

        var defaultDirectory = accounts.FirstOrDefault(account => account.IsDefault)?.Directory;
        string? DirectoryOf(StoredRouterUpstream plan) =>
            RouterCredentialRef.ProfileDirectory(plan.CredentialRef) ?? defaultDirectory;
        bool Same(string? a, string? b) =>
            a is not null && b is not null && QuotaAccountDefinition.SameDirectory(a, b);

        var changed = false;
        var used = new List<string>();
        var duplicates = new List<StoredRouterUpstream>();
        foreach (var plan in plans)
        {
            var directory = DirectoryOf(plan);
            if (directory is not null && used.Any(item => Same(item, directory))) duplicates.Add(plan);
            else if (directory is not null) used.Add(directory);
        }

        var unused = new Queue<CodexPlanAccount>(signedIn.Where(account => !used.Any(item => Same(item, account.Directory))));
        foreach (var duplicate in duplicates)
        {
            if (!unused.TryDequeue(out var account)) break;
            await UpdateUpstreamAsync(duplicate.Id, duplicate.Slug, PlanLabel(account), duplicate.Wire, duplicate.BaseUrl,
                null, duplicate.Models, ct: ct, credentialRef: RouterCredentialRef.ProfilePrefix + account.Directory,
                modelWires: duplicate.ModelWires).ConfigureAwait(false);
            changed = true;
        }

        var template = plans[0];
        var slugs = (await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false)).Select(upstream => upstream.Slug).ToHashSet(StringComparer.Ordinal);
        while (unused.TryDequeue(out var account))
        {
            var slug = FreeSlug("chatgpt-" + SlugPart(account), slugs);
            slugs.Add(slug);
            await CreateUpstreamAsync(slug, PlanLabel(account), template.Wire, template.BaseUrl, null, template.Models,
                enabled: true, ct: ct, auth: RouterAuth.CodexChatGpt, modelWires: template.ModelWires,
                credentialRef: RouterCredentialRef.ProfilePrefix + account.Directory).ConfigureAwait(false);
            changed = true;
        }
        return changed;
    }

    private static string PlanLabel(CodexPlanAccount account) =>
        account.IsDefault ? "ChatGPT plan" : $"ChatGPT plan · {account.Label}";

    /// <summary><c>second</c> for <c>~/.codex-second</c>; otherwise the account's label, made slug-safe.</summary>
    private static string SlugPart(CodexPlanAccount account)
    {
        var name = Path.GetFileName(account.Directory.TrimEnd('/', '\\')).TrimStart('.');
        if (name.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) name = name[5..];
        if (string.IsNullOrWhiteSpace(name.Trim('-', '_'))) name = account.Label;
        var cleaned = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        return cleaned.Length == 0 ? "account" : cleaned[..Math.Min(cleaned.Length, 20)].Trim('-');
    }

    private static string FreeSlug(string wanted, ISet<string> taken)
    {
        if (!taken.Contains(wanted)) return wanted;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{wanted}-{n}")) return $"{wanted}-{n}";
    }

    public async Task<IReadOnlyList<RouterEffortCapability>> ListEffortCapabilitiesAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var overrides = snapshot.EffortMappings ?? [];
        var familyOverrides = snapshot.EffortFamilyOverrides ?? [];
        return snapshot.Upstreams.Where(upstream => upstream.Enabled)
            .SelectMany(upstream => upstream.Models.Select(model =>
                RouterEffortCatalog.Resolve(upstream, model, overrides, familyOverrides)))
            .ToList();
    }

    /// <summary>
    /// The routed models grouped by family, with the effective capability each family gets. A family
    /// override beats the automatic vocabulary; a per-model override beats both and is counted.
    /// </summary>
    public async Task<IReadOnlyList<RouterEffortFamily>> ListEffortFamiliesAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var overrides = snapshot.EffortMappings ?? [];
        var familyOverrides = snapshot.EffortFamilyOverrides ?? [];
        var families = new Dictionary<string, RouterEffortFamily>();
        foreach (var upstream in snapshot.Upstreams.Where(upstream => upstream.Enabled))
        {
            foreach (var model in upstream.Models)
            {
                var family = RouterEffortCatalog.Family(model);
                var (supported, map, _) = RouterEffortCatalog.FamilyAutomatic(family);
                var familyOverride = familyOverrides.FirstOrDefault(item => item.Family == family);
                if (!families.TryGetValue(family, out var entry))
                {
                    families[family] = entry = new RouterEffortFamily(family,
                        familyOverride is null ? "automatic" : "family",
                        familyOverride?.SupportedValues ?? supported,
                        familyOverride?.LevelMap ?? map,
                        familyOverride?.DefaultValue, [], 0);
                }
                var concrete = new RouterEffortFamilyModel(upstream.Id, upstream.Slug, model, upstream.WireFor(model));
                families[family] = entry with { Models = [.. entry.Models, concrete] };
            }
        }
        foreach (var family in families.Keys.ToList())
        {
            var modelOverrides = overrides.Count(mapping =>
                families[family].Models.Any(concrete =>
                    concrete.UpstreamId == mapping.UpstreamId && concrete.Model == mapping.Model));
            families[family] = families[family] with { ModelOverrideCount = modelOverrides };
        }
        return families.Values.OrderBy(item => item.Family, StringComparer.Ordinal).ToList();
    }

    public async Task SetEffortMappingAsync(string upstreamId, string model,
        IReadOnlyList<string>? supportedValues, IReadOnlyList<string>? levelMap, string? defaultValue,
        CancellationToken ct = default)
    {
        defaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim();
        RouterEffortCatalog.Validate(supportedValues, levelMap, defaultValue);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var upstream = await _repository.GetUpstreamAsync(upstreamId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("That upstream was not found.");
        if (!upstream.Models.Contains(model, StringComparer.Ordinal))
            throw new ArgumentException("That model is not declared by the upstream.");
        await _repository.SetEffortMappingAsync(
            new RouterEffortMapping(upstreamId, model, supportedValues!, levelMap!, defaultValue), ct).ConfigureAwait(false);
        Invalidate();
    }

    public async Task ResetEffortMappingAsync(string upstreamId, string model, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        await _repository.DeleteEffortMappingAsync(upstreamId, model, ct).ConfigureAwait(false);
        Invalidate();
    }

    public async Task SetEffortFamilyMappingAsync(string family,
        IReadOnlyList<string>? supportedValues, IReadOnlyList<string>? levelMap, string? defaultValue,
        CancellationToken ct = default)
    {
        family = family.Trim();
        RouterEffortCatalog.ValidateFamily(family);
        defaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim();
        RouterEffortCatalog.Validate(supportedValues, levelMap, defaultValue);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        await _repository.SetEffortFamilyOverrideAsync(
            new RouterEffortFamilyOverride(family, supportedValues!, levelMap!, defaultValue), ct).ConfigureAwait(false);
        Invalidate();
    }

    public async Task ResetEffortFamilyMappingAsync(string family, CancellationToken ct = default)
    {
        family = family.Trim();
        RouterEffortCatalog.ValidateFamily(family);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        await _repository.DeleteEffortFamilyOverrideAsync(family, ct).ConfigureAwait(false);
        Invalidate();
    }

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
