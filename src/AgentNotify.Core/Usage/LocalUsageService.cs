using System.Text.Json;

namespace AgentNotify.Core.Usage;

/// <summary>Read-only, in-process index of the local Claude Code and Codex token ledgers.</summary>
public sealed class LocalUsageService
{
    private readonly IReadOnlyList<string> _claudeRoots;
    private readonly IReadOnlyList<string> _codexRoots;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private Dictionary<string, CachedFile> _files = new(StringComparer.Ordinal);

    public LocalUsageService(IEnumerable<string>? claudeRoots = null, IEnumerable<string>? codexRoots = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _claudeRoots = (claudeRoots ?? ClaudeRoots(home)).Distinct(StringComparer.Ordinal).ToArray();
        _codexRoots = (codexRoots ?? CodexRoots(home)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> ClaudeRoots(string home)
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var path in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Path.Combine(path, "projects");
        }
        else
        {
            yield return Path.Combine(home, ".claude", "projects");
            yield return Path.Combine(home, ".config", "claude", "projects");
        }
    }

    private static IEnumerable<string> CodexRoots(string home)
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        var root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(home, ".codex") : configured;
        yield return Path.Combine(root, "sessions");
        yield return Path.Combine(root, "archived_sessions");
    }

    public async Task<UsageReport> GetReportAsync(int days, CancellationToken cancellationToken = default)
    {
        if (days is not (7 or 30 or 0)) throw new ArgumentOutOfRangeException(nameof(days));
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            // JSONL parsing is disk work; keep it off the ASP.NET request thread. Unchanged files
            // reuse their parsed events, so visiting this page does not reread the whole ledger.
            return await Task.Run(() => Scan(days, cancellationToken), cancellationToken);
        }
        finally { _scanGate.Release(); }
    }

    private UsageReport Scan(int days, CancellationToken ct)
    {
        var next = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
        var skipped = 0;
        AddFiles("claude_code", _claudeRoots, next, ref skipped, ct);
        AddFiles("codex", _codexRoots, next, ref skipped, ct);
        _files = next;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = days == 0 ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddDays(-days);
        var rows = next.Values.SelectMany(file => file.Events)
            .OrderBy(row => row.Timestamp)
            .Where(row => row.Identity is null || seen.Add(row.Source + ":" + row.Identity))
            .Where(row => row.Timestamp >= cutoff)
            .ToArray();
        var sources = rows.GroupBy(row => row.Source).OrderBy(group => group.Key)
            .Select(group => new UsageGroup(group.Key, Sum(group))).ToArray();
        var models = rows.GroupBy(row => (row.Source, row.Model)).OrderByDescending(group => Sum(group).Total)
            .Take(30).Select(group => new UsageModel(group.Key.Source, group.Key.Model, Sum(group))).ToArray();
        var daily = rows.GroupBy(row => DateOnly.FromDateTime(row.Timestamp.LocalDateTime))
            .OrderBy(group => group.Key).Select(group => new UsageDay(group.Key.ToString("yyyy-MM-dd"), Sum(group))).ToArray();
        return new UsageReport(DateTimeOffset.UtcNow, days, next.Count, skipped, rows.Length, Sum(rows), sources, models, daily);
    }

    private void AddFiles(string source, IReadOnlyList<string> roots, Dictionary<string, CachedFile> next, ref int skipped, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).ToArray();
            }
            catch (IOException) { skipped++; continue; }
            catch (UnauthorizedAccessException) { skipped++; continue; }

            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                // Codex can retain the same rollout in both active and archived directories.
                if (source == "codex" && !names.Add(Path.GetFileName(path))) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (_files.TryGetValue(path, out var previous) && previous.Length == info.Length && previous.Modified == info.LastWriteTimeUtc)
                    {
                        next[path] = previous;
                        continue;
                    }
                    next[path] = new CachedFile(info.Length, info.LastWriteTimeUtc, ParseFile(source, path, ct));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    skipped++;
                }
            }
        }
    }

    private static UsageEvent[] ParseFile(string source, string path, CancellationToken ct)
    {
        var events = new List<UsageEvent>();
        var model = "Unknown model";
        var session = Path.GetFileNameWithoutExtension(path);
        TokenCounts? previousTotal = null;
        var mirrored = false;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length > 1024 * 1024 || line.Length == 0) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var row = json.RootElement;
                if (source == "claude_code")
                {
                    var item = ParseClaude(row);
                    if (item is not null) events.Add(item);
                    continue;
                }
                var type = String(row, "type");
                var payload = Child(row, "payload");
                if (type == "session_meta")
                {
                    var threadSource = String(payload, "thread_source");
                    mirrored = threadSource == "subagent" && !string.IsNullOrEmpty(String(payload, "forked_from_id"));
                    session = String(payload, "id") ?? session;
                }
                else if (type == "turn_context") model = String(payload, "model") ?? model;
                else if (type == "event_msg" && String(payload, "type") == "token_count" && !mirrored)
                {
                    var info = Child(payload, "info");
                    if (info.ValueKind != JsonValueKind.Object) continue;
                    var total = Child(info, "total_token_usage");
                    TokenCounts delta;
                    if (total.ValueKind == JsonValueKind.Object)
                    {
                        var current = CodexCounters(total);
                        delta = previousTotal is null ? current : current.Subtract(previousTotal.Value);
                        previousTotal = current;
                    }
                    else
                    {
                        var last = Child(info, "last_token_usage");
                        if (last.ValueKind != JsonValueKind.Object) continue;
                        delta = CodexCounters(last);
                    }
                    if (delta.Total == 0) continue;
                    if (!DateTimeOffset.TryParse(String(row, "timestamp"), out var timestamp)) continue;
                    events.Add(new UsageEvent("codex", timestamp, model, session, null, delta));
                }
            }
            catch (JsonException) { /* One malformed line must not discard the rest of a session. */ }
        }
        return events.ToArray();
    }

    private static UsageEvent? ParseClaude(JsonElement row)
    {
        if (String(row, "type") != "assistant") return null;
        var message = Child(row, "message");
        var usage = Child(message, "usage");
        var model = String(message, "model");
        if (usage.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(model) || model.StartsWith('<') ||
            !DateTimeOffset.TryParse(String(row, "timestamp"), out var timestamp)) return null;

        var messageId = String(message, "id");
        var requestId = String(row, "requestId");
        var identity = messageId is not null && requestId is not null ? messageId + ":" + requestId
            : messageId ?? (requestId is null ? String(row, "uuid") : (String(row, "sessionId") ?? "") + ":" + requestId);
        var counts = new TokenCounts(Number(usage, "input_tokens"), Number(usage, "output_tokens"),
            Number(usage, "cache_read_input_tokens"), Number(usage, "cache_creation_input_tokens"), 0);
        if (counts.Total == 0) return null;
        return new UsageEvent("claude_code", timestamp, model, String(row, "sessionId") ?? "", identity, counts);
    }

    private static TokenCounts CodexCounters(JsonElement value)
    {
        var input = Number(value, "input_tokens");
        var cached = Math.Min(input, Number(value, "cached_input_tokens"));
        var output = Number(value, "output_tokens");
        return new TokenCounts(input - cached, output, cached, 0, Math.Min(output, Number(value, "reasoning_output_tokens")));
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : default;

    private static string? String(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long Number(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? Math.Max(0, count) : 0;
    }

    private static TokenCounts Sum(IEnumerable<UsageEvent> rows)
    {
        var result = new TokenCounts(0, 0, 0, 0, 0);
        foreach (var row in rows) result = result.Add(row.Counts);
        return result;
    }

    private sealed record CachedFile(long Length, DateTime Modified, UsageEvent[] Events);
    private sealed record UsageEvent(string Source, DateTimeOffset Timestamp, string Model, string Session, string? Identity, TokenCounts Counts);
}

public readonly record struct TokenCounts(long Input, long Output, long CacheRead, long CacheWrite, long Reasoning)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
    public TokenCounts Add(TokenCounts other) => new(Input + other.Input, Output + other.Output,
        CacheRead + other.CacheRead, CacheWrite + other.CacheWrite, Reasoning + other.Reasoning);
    public TokenCounts Subtract(TokenCounts previous) => new(Math.Max(0, Input - previous.Input),
        Math.Max(0, Output - previous.Output), Math.Max(0, CacheRead - previous.CacheRead),
        Math.Max(0, CacheWrite - previous.CacheWrite), Math.Max(0, Reasoning - previous.Reasoning));
}

public sealed record UsageGroup(string Source, TokenCounts Counts);
public sealed record UsageModel(string Source, string Model, TokenCounts Counts);
public sealed record UsageDay(string Date, TokenCounts Counts);
public sealed record UsageReport(DateTimeOffset ScannedAt, int Days, int FilesScanned, int FilesSkipped,
    int Events, TokenCounts Totals, IReadOnlyList<UsageGroup> Sources, IReadOnlyList<UsageModel> Models,
    IReadOnlyList<UsageDay> Daily);
