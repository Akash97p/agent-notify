using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Wsl;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Core.Usage;

/// <summary>Read-only, in-process index of local coding-agent token ledgers.</summary>
public sealed class LocalUsageService
{
    private readonly IReadOnlyList<string> _claudeRoots;
    private readonly IReadOnlyList<string> _codexRoots;
    private readonly string _openCodeDatabase;
    private readonly IWslEnvironment? _wsl;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private Dictionary<string, CachedFile> _files = new(StringComparer.Ordinal);

    /// <param name="wsl">
    /// Running WSL distributions whose agent logs are read too. Defaults to <see cref="WslDiscovery.Default"/>
    /// only when no explicit roots are given, so a caller that names its roots gets exactly those.
    /// </param>
    public LocalUsageService(IEnumerable<string>? claudeRoots = null, IEnumerable<string>? codexRoots = null,
        string? openCodeDatabase = null, IWslEnvironment? wsl = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _wsl = wsl ?? (claudeRoots is null && codexRoots is null && openCodeDatabase is null ? WslDiscovery.Default : null);
        _claudeRoots = (claudeRoots ?? ClaudeRoots(home)).Distinct(StringComparer.Ordinal).ToArray();
        _codexRoots = (codexRoots ?? CodexRoots(home)).Distinct(StringComparer.Ordinal).ToArray();
        _openCodeDatabase = openCodeDatabase ?? OpenCodeDatabase(home);
    }

    /// <summary>
    /// The ledgers to read now. WSL homes are resolved on every scan because distributions start
    /// and stop; inside WSL the agents' own environment variables are not visible, so their
    /// default locations are used.
    /// </summary>
    private Sources CurrentSources()
    {
        var homes = _wsl?.RunningHomes() ?? [];
        return new Sources(
            [.. _claudeRoots, .. homes.SelectMany(home => new[]
            {
                Path.Combine(home.WindowsHome, ".claude", "projects"),
                Path.Combine(home.WindowsHome, ".config", "claude", "projects")
            })],
            [.. _codexRoots, .. homes.SelectMany(home => new[]
            {
                Path.Combine(home.WindowsHome, ".codex", "sessions"),
                Path.Combine(home.WindowsHome, ".codex", "archived_sessions")
            })],
            [_openCodeDatabase, .. homes.Select(home => Path.Combine(home.WindowsHome, ".local", "share", "opencode", "opencode.db"))],
            homes.Select(home => home.Distribution).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed record Sources(string[] ClaudeRoots, string[] CodexRoots, string[] OpenCodeDatabases,
        string[] WslDistributions);

    private static string OpenCodeDatabase(string home)
    {
        var configured = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.Combine(configured, "opencode.db");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg,
            "opencode", "opencode.db");
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

    public async Task<OpenCodeGoEstimate> GetOpenCodeGoEstimateAsync(DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => EstimateOpenCodeGo(now, cancellationToken), cancellationToken);
        }
        finally { _scanGate.Release(); }
    }

    private OpenCodeGoEstimate EstimateOpenCodeGo(DateTimeOffset now, CancellationToken ct)
    {
        var skipped = 0;
        var rows = ReadOpenCode(CurrentSources().OpenCodeDatabases, ref skipped, ct)
            .Where(row => row.Provider == "opencode-go" && row.Timestamp <= now &&
                          row.Timestamp >= now.AddDays(-30)).ToArray();
        if (skipped > 0 && rows.Length == 0)
            return new OpenCodeGoEstimate("unavailable", [], "The local OpenCode usage database could not be read.");
        if (rows.Length == 0)
            return new OpenCodeGoEstimate("no_data", [], "No OpenCode Go requests were found in the last 30 days on this machine.");

        var periods = new (string Key, string Label, TimeSpan Length, decimal Fraction)[]
        {
            ("five_hour", "Last 5 hours", TimeSpan.FromHours(5), .20m),
            ("seven_day", "Last 7 days", TimeSpan.FromDays(7), .50m),
            ("thirty_day", "Last 30 days", TimeSpan.FromDays(30), 1m)
        };
        var models = rows.GroupBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(group =>
            {
                var rates = ApiPriceCatalog.Find("opencode", "opencode-go", group.Key);
                var monthlyLimit = ApiPriceCatalog.OpenCodeGoMonthlyLimit(group.Key);
                var windows = periods.Select(period =>
                {
                    var selected = group.Where(row => row.Timestamp >= now - period.Length).ToArray();
                    var unpriced = selected.Count(row => !ApiPriceCatalog.CanPriceOpenCodeGo(group.Key, row.Counts));
                    var observed = rates is null ? 0m : selected.Where(row => ApiPriceCatalog.CanPriceOpenCodeGo(group.Key, row.Counts))
                        .Sum(row => rates.EstimateUsd(row.Counts, row.CacheWrite1h));
                    var limit = monthlyLimit * period.Fraction;
                    double? percent = unpriced == 0 && limit is > 0
                        ? (double)(observed / limit.Value * 100m) : null;
                    return new OpenCodeGoWindowEstimate(period.Key, period.Label, observed, limit,
                        percent, selected.Length, unpriced);
                }).ToArray();
                return new OpenCodeGoModelEstimate(group.Key, windows);
            }).ToArray();
        return new OpenCodeGoEstimate("estimated", models,
            "Local OpenCode requests only. Other clients, billing-cycle boundaries, and provider-side adjustments are unknown; these are not live remaining quotas." +
            (skipped > 0 ? " Some local OpenCode databases could not be read." : ""));
    }

    private UsageReport Scan(int days, CancellationToken ct)
    {
        var next = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
        var skipped = 0;
        var sources = CurrentSources();
        AddFiles("claude_code", sources.ClaudeRoots, next, ref skipped, ct);
        AddFiles("codex", sources.CodexRoots, next, ref skipped, ct);
        _files = next;
        var openCode = ReadOpenCode(sources.OpenCodeDatabases, ref skipped, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = days == 0 ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddDays(-days);
        var rows = next.Values.SelectMany(file => file.Events).Concat(openCode)
            .OrderBy(row => row.Timestamp)
            .Where(row => row.Identity is null || seen.Add(row.Source + ":" + row.Identity))
            .Where(row => row.Timestamp >= cutoff)
            .ToArray();
        var groups = rows.GroupBy(row => row.Source).OrderBy(group => group.Key)
            .Select(group => new UsageGroup(group.Key, Sum(group), Estimate(group))).ToArray();
        var models = rows.GroupBy(row => (row.Source, row.Provider, row.Model)).OrderByDescending(group => Sum(group).Total)
            .Take(30).Select(group => Model(group)).ToArray();
        var daily = rows.GroupBy(row => DateOnly.FromDateTime(row.Timestamp.LocalDateTime))
            .OrderBy(group => group.Key).Select(group => new UsageDay(group.Key.ToString("yyyy-MM-dd"), Sum(group), Estimate(group))).ToArray();
        var projects = rows.GroupBy(row => (row.Project.Id, row.Project.Name))
            .Select(group => new UsageProject(group.Key.Id, group.Key.Name, Sum(group), Estimate(group),
                group.GroupBy(row => (row.Source, row.Provider, row.Model)).Select(Model)
                    .OrderByDescending(model => model.Cost.PricedUsd).ToArray()))
            .OrderByDescending(project => project.Cost.PricedUsd)
            .ThenByDescending(project => project.Counts.Total).ToArray();
        var sessionGroups = rows.Where(row => !string.IsNullOrWhiteSpace(row.Session))
            .GroupBy(row => (row.Source, row.Session, row.Project.Id, row.Project.Name))
            .Select(group => new UsageSession(SessionId(group.Key.Source, group.Key.Session, group.Key.Id),
                group.Key.Source, group.Key.Id, group.Key.Name, group.Min(row => row.Timestamp),
                group.Max(row => row.Timestamp), group.Count(), Sum(group), Estimate(group),
                group.GroupBy(row => (row.Source, row.Provider, row.Model)).Select(Model)
                    .OrderByDescending(model => model.Counts.Total).ToArray()))
            .OrderByDescending(session => session.EndedAt).ToArray();
        return new UsageReport(DateTimeOffset.UtcNow, days, next.Count + sources.OpenCodeDatabases.Count(File.Exists), skipped, rows.Length,
            Sum(rows), Estimate(rows), groups, models, projects, daily, ApiPriceCatalog.AsOf,
            sessionGroups.Length, sessionGroups.Take(50).ToArray(), sources.WslDistributions);
    }

    private static string SessionId(string source, string session, string projectId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + session + "\0" + projectId));
        return "s_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static UsageEvent[] ReadOpenCode(IEnumerable<string> databases, ref int skipped, CancellationToken ct)
    {
        var events = new List<UsageEvent>();
        foreach (var database in databases.Distinct(StringComparer.Ordinal))
            events.AddRange(ReadOpenCode(database, ref skipped, ct));
        return events.ToArray();
    }

    private static UsageEvent[] ReadOpenCode(string database, ref int skipped, CancellationToken ct)
    {
        if (!File.Exists(database)) return [];
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            // Select only scalar usage fields. Prompt, response, and tool payloads never leave SQLite.
            command.CommandText = """
                SELECT m.id, m.session_id, m.time_created, s.directory,
                       json_extract(m.data, '$.providerID'), json_extract(m.data, '$.modelID'),
                       json_extract(m.data, '$.tokens.input'), json_extract(m.data, '$.tokens.output'),
                       json_extract(m.data, '$.tokens.reasoning'),
                       json_extract(m.data, '$.tokens.cache.read'), json_extract(m.data, '$.tokens.cache.write')
                FROM message AS m
                LEFT JOIN session AS s ON s.id = m.session_id
                WHERE CASE WHEN json_valid(m.data) THEN json_extract(m.data, '$.role') END = 'assistant'
                """;
            using var reader = command.ExecuteReader();
            var events = new List<UsageEvent>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var model = reader.IsDBNull(5) ? null : reader.GetString(5);
                if (string.IsNullOrWhiteSpace(model) || reader.IsDBNull(2)) continue;
                var millis = reader.GetInt64(2);
                DateTimeOffset timestamp;
                try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(millis); }
                catch (ArgumentOutOfRangeException) { continue; }
                var input = SqliteCount(reader, 6);
                var output = SqliteCount(reader, 7);
                var reasoning = SqliteCount(reader, 8);
                var counts = new TokenCounts(input, output + reasoning, SqliteCount(reader, 9),
                    SqliteCount(reader, 10), reasoning);
                if (counts.Total == 0) continue;
                var provider = reader.IsDBNull(4) ? "unknown" : reader.GetString(4);
                var project = ProjectInfo.FromDirectory(reader.IsDBNull(3) ? null : reader.GetString(3));
                events.Add(new UsageEvent("opencode", timestamp, model, provider,
                    reader.IsDBNull(1) ? "" : reader.GetString(1), project,
                    reader.IsDBNull(0) ? null : reader.GetString(0), counts, 0));
            }
            return events.ToArray();
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            skipped++;
            return [];
        }
    }

    private static long SqliteCount(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index) || reader.GetFieldType(index) != typeof(long)) return 0;
        return Math.Max(0, reader.GetInt64(index));
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
        var project = ProjectInfo.Unknown;
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
                    project = ProjectInfo.FromDirectory(String(payload, "cwd"), project);
                }
                else if (type == "turn_context")
                {
                    model = String(payload, "model") ?? model;
                    project = ProjectInfo.FromDirectory(String(payload, "cwd"), project);
                }
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
                    events.Add(new UsageEvent("codex", timestamp, model, "openai", session, project, null, delta, 0));
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
        var oneHour = Number(Child(usage, "cache_creation"), "ephemeral_1h_input_tokens");
        return new UsageEvent("claude_code", timestamp, model, "anthropic", String(row, "sessionId") ?? "",
            ProjectInfo.FromDirectory(String(row, "cwd")), identity, counts, Math.Min(counts.CacheWrite, oneHour));
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

    private static UsageModel Model(IGrouping<(string Source, string Provider, string Model), UsageEvent> group) =>
        new(group.Key.Source, group.Key.Provider, group.Key.Model, Sum(group), Estimate(group),
            ApiPriceCatalog.Find(group.Key.Source, group.Key.Provider, group.Key.Model));

    private static ApiCostEstimate Estimate(IEnumerable<UsageEvent> rows)
    {
        decimal priced = 0;
        var unpricedEvents = 0;
        long unpricedTokens = 0;
        foreach (var row in rows)
        {
            var rate = ApiPriceCatalog.Find(row.Source, row.Provider, row.Model);
            if (rate is null || row.Source == "opencode" && row.Provider == "opencode-go" &&
                !ApiPriceCatalog.CanPriceOpenCodeGo(row.Model, row.Counts))
            {
                unpricedEvents++;
                unpricedTokens += row.Counts.Total;
            }
            else priced += rate.EstimateUsd(row.Counts, row.CacheWrite1h);
        }
        return new ApiCostEstimate(priced, unpricedEvents, unpricedTokens);
    }

    private sealed record ProjectInfo(string Id, string Name)
    {
        internal static readonly ProjectInfo Unknown = new("unknown", "Unknown project");

        internal static ProjectInfo FromDirectory(string? directory, ProjectInfo? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || directory.Length > 4096) return fallback ?? Unknown;
            // WSL agents record Linux paths, which are not fully qualified to a Windows broker.
            if (directory[0] == '/' && OperatingSystem.IsWindows()) return FromPosix(directory, fallback);
            if (!Path.IsPathFullyQualified(directory)) return fallback ?? Unknown;
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                var name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name)) return fallback ?? Unknown;
                var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
                return new ProjectInfo("p_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(), name[..Math.Min(name.Length, 120)]);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return fallback ?? Unknown;
            }
        }

        private static ProjectInfo FromPosix(string directory, ProjectInfo? fallback)
        {
            if (directory.Any(char.IsControl)) return fallback ?? Unknown;
            var path = directory.TrimEnd('/');
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(name)) return fallback ?? Unknown;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
            return new ProjectInfo("p_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(), name[..Math.Min(name.Length, 120)]);
        }
    }

    private sealed record CachedFile(long Length, DateTime Modified, UsageEvent[] Events);
    private sealed record UsageEvent(string Source, DateTimeOffset Timestamp, string Model, string Provider, string Session,
        ProjectInfo Project, string? Identity, TokenCounts Counts, long CacheWrite1h);
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

public sealed record UsageGroup(string Source, TokenCounts Counts, ApiCostEstimate Cost);
public sealed record UsageModel(string Source, string Provider, string Model, TokenCounts Counts, ApiCostEstimate Cost, ApiTokenRates? Rate);
public sealed record UsageProject(string Id, string Name, TokenCounts Counts, ApiCostEstimate Cost,
    IReadOnlyList<UsageModel> Models);
public sealed record UsageDay(string Date, TokenCounts Counts, ApiCostEstimate Cost);
public sealed record UsageSession(string Id, string Source, string ProjectId, string ProjectName,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, int Events, TokenCounts Counts,
    ApiCostEstimate Cost, IReadOnlyList<UsageModel> Models);
public sealed record UsageReport(DateTimeOffset ScannedAt, int Days, int FilesScanned, int FilesSkipped,
    int Events, TokenCounts Totals, ApiCostEstimate Cost, IReadOnlyList<UsageGroup> Sources,
    IReadOnlyList<UsageModel> Models, IReadOnlyList<UsageProject> Projects, IReadOnlyList<UsageDay> Daily,
    string PricingAsOf, int SessionCount, IReadOnlyList<UsageSession> Sessions,
    IReadOnlyList<string> WslDistributions)
{
    public string ContractVersion => "3";
}
