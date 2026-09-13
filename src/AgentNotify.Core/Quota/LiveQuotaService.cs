namespace AgentNotify.Core.Quota;

/// <summary>Current provider-reported allowance, distinct from local token history.</summary>
public sealed class LiveQuotaService
{
    private readonly IReadOnlyList<ILiveQuotaProbe> _probes;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public LiveQuotaService(IEnumerable<ILiveQuotaProbe>? probes = null, TimeProvider? clock = null)
    {
        _probes = (probes ?? [new CodexQuotaProbe(), new ClaudeQuotaProbe(),
            new UnavailableQuotaProbe("opencode", "OpenCode uses multiple providers; no shared account quota source is available.")]).ToArray();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<LiveQuotaReport> GetReportAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<LiveQuotaSnapshot>(_probes.Count);
            foreach (var probe in _probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _clock.GetUtcNow();
                var scope = probe.ScopeKey();
                _cache.TryGetValue(probe.Provider, out var cached);
                if (cached is not null && cached.Scope != scope) cached = null;
                // The first visit probes immediately. Ordinary visits reuse a 5-minute snapshot;
                // a manual refresh can bypass that once every 30 seconds per provider.
                var due = cached is null || (refresh && now - cached.AttemptedAt >= TimeSpan.FromSeconds(30)) ||
                    now - cached.AttemptedAt >= TimeSpan.FromMinutes(5);
                if (!due)
                {
                    results.Add(cached!.Snapshot);
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
                _cache[probe.Provider] = new CacheEntry(scope, now, result);
                results.Add(result);
            }
            return new LiveQuotaReport(_clock.GetUtcNow(), results);
        }
        finally { _gate.Release(); }
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
    string? Plan, decimal? CreditBalance, IReadOnlyList<LiveQuotaWindow> Windows, string? Message)
{
    public static LiveQuotaSnapshot Unavailable(string provider, string reason, DateTimeOffset now,
        string status = "unavailable") => new(provider, status, "none", null, null, null, [], reason);
}

public sealed record LiveQuotaReport(DateTimeOffset CheckedAt, IReadOnlyList<LiveQuotaSnapshot> Providers);

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
