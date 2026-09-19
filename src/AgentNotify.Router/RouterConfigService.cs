using System.Text.RegularExpressions;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Router;

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
public sealed partial class RouterConfigService
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


}
