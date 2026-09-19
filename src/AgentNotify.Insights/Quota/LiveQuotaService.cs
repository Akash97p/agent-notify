using AgentNotify.Core.Config;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;

namespace AgentNotify.Insights.Quota;

/// <summary>Current provider-reported allowance, distinct from local token history.</summary>
public sealed class LiveQuotaService
{
    private readonly IReadOnlyList<ILiveQuotaProbe>? _fixedProbes;
    private readonly Func<IReadOnlyList<QuotaAccountDefinition>>? _accounts;
    /// <summary>Built-in and discovered account IDs the owner removed.</summary>
    private readonly Func<ICollection<string>> _removedAccounts;
    private readonly Func<int?> _openCodeGoRenewalDay;
    /// <summary>The owner's name for a built-in or discovered account, keyed by provider or WSL account ID.</summary>
    private readonly Func<string, string?> _defaultAccountLabel;
    private readonly Func<QuotaAccountDefinition, ILiveQuotaProbe> _probeFactory;
    private readonly LocalUsageService? _usage;
    private readonly IWslEnvironment? _wsl;
    /// <summary>Home scanned for secondary profiles: the real profile in production, none with fixed probes.</summary>
    private readonly string? _nativeHome;
    private readonly CodexQuotaProbe _defaultCodex = new();
    private readonly ClaudeQuotaProbe _defaultClaude = new();
    private readonly ILiveQuotaProbe _openCode = new UnavailableQuotaProbe("opencode",
        "OpenCode has no shared provider account quota. Local Go estimates appear separately below.");
    private readonly Dictionary<string, (QuotaAccountDefinition Definition, ILiveQuotaProbe Probe)> _extraProbes = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public LiveQuotaService(IEnumerable<ILiveQuotaProbe>? probes = null, TimeProvider? clock = null,
        Func<IReadOnlyList<QuotaAccountDefinition>>? accounts = null, LocalUsageService? usage = null,
        Func<QuotaAccountDefinition, ILiveQuotaProbe>? probeFactory = null,
        Func<string, string?>? defaultAccountLabel = null, IWslEnvironment? wsl = null,
        Func<ICollection<string>>? removedAccounts = null, string? nativeHome = null,
        Func<int?>? openCodeGoRenewalDay = null)
    {
        _openCodeGoRenewalDay = openCodeGoRenewalDay ?? (() => null);
        _fixedProbes = probes?.ToArray();
        _removedAccounts = removedAccounts ?? (() => Array.Empty<string>());
        _wsl = wsl;
        _nativeHome = nativeHome ?? (probes is null ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null);
        _accounts = accounts;
        _defaultAccountLabel = defaultAccountLabel ?? (_ => null);
        _usage = usage;
        _probeFactory = probeFactory ?? (account => account.Provider == "codex"
            ? new CodexQuotaProbe(codexHome: account.Directory)
            : new ClaudeQuotaProbe(credentialPath: Path.Combine(account.Directory, ".credentials.json")));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<LiveQuotaReport> GetReportAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var probes = GetProbes();
            var results = new List<LiveQuotaSnapshot>(probes.Count);
            foreach (var (probe, accountId, accountLabel) in probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _clock.GetUtcNow();
                var scope = probe.ScopeKey();
                _cache.TryGetValue(accountId, out var cached);
                if (cached is not null && cached.Scope != scope) cached = null;
                // The first visit probes immediately. Ordinary visits reuse a 5-minute snapshot;
                // a manual refresh can bypass that once every 30 seconds per provider.
                var due = cached is null || (refresh && now - cached.AttemptedAt >= TimeSpan.FromSeconds(30)) ||
                    now - cached.AttemptedAt >= TimeSpan.FromMinutes(5);
                if (!due)
                {
                    results.Add(cached!.Snapshot with { AccountLabel = accountLabel });
                    continue;
                }

                LiveQuotaSnapshot result;
                try { result = await probe.FetchAsync(now, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { result = LiveQuotaSnapshot.Unavailable(probe.Provider, "probe_failed", now); }

                // Keep a known result through a transient failure, but never carry it across a
                // changed credential file. The stale label and original fetch time stay visible.
                if (result.Status is "unavailable" or "rate_limited" &&
                    cached?.Snapshot is { Status: "ok" or "stale" } prior)
                    result = prior with { Status = "stale", Message = result.Message };
                result = result with { AccountId = accountId, AccountLabel = accountLabel };
                _cache[accountId] = new CacheEntry(scope, now, result);
                results.Add(result);
            }
            OpenCodeGoEstimate? go = null;
            if (_usage is not null)
            {
                try { go = await _usage.GetOpenCodeGoEstimateAsync(_clock.GetUtcNow(), cancellationToken, _openCodeGoRenewalDay()); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { go = new OpenCodeGoEstimate("unavailable", [], "Local OpenCode Go usage could not be read."); }
            }
            return new LiveQuotaReport(_clock.GetUtcNow(), results, go);
        }
        finally { _gate.Release(); }
    }

    private IReadOnlyList<(ILiveQuotaProbe Probe, string AccountId, string AccountLabel)> GetProbes()
    {
        var removed = _removedAccounts();
        var result = (_fixedProbes is null
            ? new List<(ILiveQuotaProbe, string, string)>
              {
                  (_defaultCodex, "codex:default", _defaultAccountLabel("codex") ?? "Current account"),
                  (_defaultClaude, "claude_code:default", _defaultAccountLabel("claude_code") ?? "Current account")
              }
            : _fixedProbes.Select(probe => (probe, probe.Provider + ":default", _defaultAccountLabel(probe.Provider) ?? "Current account")).ToList())
            .Where(item => !removed.Contains(item.Item2)).ToList();
        var configured = (_accounts?.Invoke() ?? []).Take(16).ToArray();
        var active = new HashSet<string>(StringComparer.Ordinal);
        // Discovered profiles come next — native secondary profiles, WSL defaults, WSL secondary
        // profiles — unless the owner removed them or already monitors the same directory by hand
        // or through a built-in default. They leave the list, and the cache, when the profile or
        // distribution goes away.
        var discovered = QuotaAccountDefinition.MonitoredDiscoveredAccounts(_wsl, _defaultAccountLabel,
            configured, removed, _nativeHome);
        foreach (var account in discovered)
        {
            if (active.Add(account.Id)) result.Add((ProbeFor(account), account.Id, account.Label));
        }
        foreach (var account in configured)
        {
            if (account is null || account.Provider is not ("codex" or "claude_code") ||
                account.Id is not { Length: 34 } || !account.Id.StartsWith("q_", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(account.Directory) ||
                !Path.IsPathFullyQualified(account.Directory) || !active.Add(account.Id)) continue;
            result.Add((ProbeFor(account), account.Id, account.Label));
        }
        foreach (var id in _extraProbes.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _extraProbes.Remove(id);
            _cache.Remove(id);
        }
        if (_fixedProbes is null) result.Add((_openCode, "opencode:default", "OpenCode"));
        return result;
    }

    private ILiveQuotaProbe ProbeFor(QuotaAccountDefinition account)
    {
        if (!_extraProbes.TryGetValue(account.Id, out var stored) || stored.Definition != account)
        {
            stored = (account, _probeFactory(account));
            _extraProbes[account.Id] = stored;
        }
        return stored.Probe;
    }

    private sealed record CacheEntry(string Scope, DateTimeOffset AttemptedAt, LiveQuotaSnapshot Snapshot);
}

public interface ILiveQuotaProbe
{
    string Provider { get; }
    string ScopeKey();
    Task<LiveQuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record LiveQuotaWindow(string Key, string Label, double UsedPercent, double RemainingPercent,
    int? DurationMinutes, DateTimeOffset? ResetsAt);

public sealed record LiveQuotaSnapshot(string Provider, string Status, string Source, DateTimeOffset? FetchedAt,
    string? Plan, decimal? CreditBalance, IReadOnlyList<LiveQuotaWindow> Windows, string? Message,
    string AccountId = "", string AccountLabel = "")
{
    public static LiveQuotaSnapshot Unavailable(string provider, string reason, DateTimeOffset now,
        string status = "unavailable") => new(provider, status, "none", null, null, null, [], reason);
}

public sealed record LiveQuotaReport(DateTimeOffset CheckedAt, IReadOnlyList<LiveQuotaSnapshot> Providers,
    OpenCodeGoEstimate? OpenCodeGo)
{
    public string ContractVersion => "3";
}

internal sealed class UnavailableQuotaProbe(string provider, string message) : ILiveQuotaProbe
{
    public string Provider => provider;
    public string ScopeKey() => "none";
    public Task<LiveQuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(LiveQuotaSnapshot.Unavailable(provider, message, now));
}

internal static class QuotaFileScope
{
    internal static string Of(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? path + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.CreationTimeUtc.Ticks : path + ":missing";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return path + ":unreadable"; }
    }
}
