using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Wsl;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Core.Usage;

/// <summary>Read-only, in-process index of local coding-agent token ledgers.</summary>
public sealed class LocalUsageService
{
    private readonly IReadOnlyList<string> _claudeRoots;
    private readonly IReadOnlyList<string> _codexRoots;
    private readonly string _openCodeDatabase;
    private readonly IReadOnlyList<string> _museRoots;
    private readonly IReadOnlyList<string> _kiloDatabases;
    private readonly IReadOnlyList<string> _geminiRoots;
    private readonly IWslEnvironment? _wsl;
    private readonly Func<IReadOnlyList<QuotaAccountDefinition>>? _accounts;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private Dictionary<string, CachedFile> _files = new(StringComparer.Ordinal);
    private Dictionary<string, CachedDatabase> _databases = new(StringComparer.Ordinal);

    /// <param name="wsl">
    /// Running WSL distributions whose agent logs are read too. Defaults to <see cref="WslDiscovery.Default"/>
    /// only when no explicit roots are given, so a caller that names its roots gets exactly those.
    /// </param>
    /// <param name="accounts">Profiles added by hand on Live quota, whose ledgers are read as well.</param>
    /// <param name="museRoots">Muse Code <c>sessions</c> directories.</param>
    /// <param name="kiloDatabases">Kilo CLI SQLite databases, which use OpenCode's schema.</param>
    /// <param name="geminiRoots">Gemini CLI <c>tmp</c> directories holding per-project <c>chats</c>.</param>
    public LocalUsageService(IEnumerable<string>? claudeRoots = null, IEnumerable<string>? codexRoots = null,
        string? openCodeDatabase = null, IWslEnvironment? wsl = null,
        Func<IReadOnlyList<QuotaAccountDefinition>>? accounts = null, IEnumerable<string>? museRoots = null,
        IEnumerable<string>? kiloDatabases = null, IEnumerable<string>? geminiRoots = null)
    {
        _accounts = accounts;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaults = claudeRoots is null && codexRoots is null && openCodeDatabase is null;
        _wsl = wsl ?? (defaults ? WslDiscovery.Default : null);
        _museRoots = (museRoots ?? (defaults ? [MuseSessions(home)] : [])).ToArray();
        _kiloDatabases = (kiloDatabases ?? (defaults ? [KiloDatabase(home)] : [])).ToArray();
        _geminiRoots = (geminiRoots ?? (defaults ? [Path.Combine(home, ".gemini", "tmp")] : [])).ToArray();
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
        var accounts = (_accounts?.Invoke() ?? []).Where(account => Path.IsPathFullyQualified(account.Directory)).ToArray();
        return new Sources(
            [.. new[]
            {
                _claudeRoots,
                homes.SelectMany(home => new[]
                {
                    Path.Combine(home.WindowsHome, ".claude", "projects"),
                    Path.Combine(home.WindowsHome, ".config", "claude", "projects")
                }),
                accounts.Where(account => account.Provider == "claude_code")
                    .Select(account => Path.Combine(account.Directory, "projects"))
            }.SelectMany(roots => roots).Distinct(PathComparer)],
            [.. new[]
            {
                _codexRoots,
                homes.SelectMany(home => new[]
                {
                    Path.Combine(home.WindowsHome, ".codex", "sessions"),
                    Path.Combine(home.WindowsHome, ".codex", "archived_sessions")
                }),
                accounts.Where(account => account.Provider == "codex").SelectMany(account => new[]
                {
                    Path.Combine(account.Directory, "sessions"),
                    Path.Combine(account.Directory, "archived_sessions")
                })
            }.SelectMany(roots => roots).Distinct(PathComparer)],
            [_openCodeDatabase, .. homes.Select(home => Path.Combine(home.WindowsHome, ".local", "share", "opencode", "opencode.db"))],
            homes.Select(home => home.Distribution).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            [.. _museRoots.Concat(homes.Select(home => Path.Combine(home.WindowsHome, ".local", "share", "muse", "sessions"))).Distinct(PathComparer)],
            [.. _kiloDatabases.Concat(homes.Select(home => Path.Combine(home.WindowsHome, ".local", "share", "kilo", "kilo.db"))).Distinct(PathComparer)],
            [.. _geminiRoots.Concat(homes.Select(home => Path.Combine(home.WindowsHome, ".gemini", "tmp"))).Distinct(PathComparer)]);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Sources(string[] ClaudeRoots, string[] CodexRoots, string[] OpenCodeDatabases,
        string[] WslDistributions, string[] MuseRoots, string[] KiloDatabases, string[] GeminiRoots);

    private static string XdgData(string home)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg;
    }

    private static string MuseSessions(string home) => Path.Combine(XdgData(home), "muse", "sessions");

    private static string KiloDatabase(string home) => Path.Combine(XdgData(home), "kilo", "kilo.db");

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
        var rows = ReadOpenCode("opencode", CurrentSources().OpenCodeDatabases, ref skipped, ct)
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
                var monthlyLimit = ApiPriceCatalog.OpenCodeGoMonthlyLimit(group.Key);
                var windows = periods.Select(period =>
                {
                    var selected = group.Where(row => row.Timestamp >= now - period.Length)
                        .Select(row => (Row: row, Rate: ApiPriceCatalog.OpenCodeGoRate(group.Key, row.Timestamp, row.Counts)))
                        .ToArray();
                    var unpriced = selected.Count(item => item.Rate is null);
                    var observed = selected.Where(item => item.Rate is not null)
                        .Sum(item => item.Rate!.EstimateUsd(item.Row.Counts, item.Row.CacheWrite1h));
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
        AddFiles("muse", sources.MuseRoots, next, ref skipped, ct);
        AddFiles("gemini_cli", sources.GeminiRoots, next, ref skipped, ct);
        _files = next;
        var openCode = ReadOpenCode("opencode", sources.OpenCodeDatabases, ref skipped, ct)
            .Concat(ReadOpenCode("kilo", sources.KiloDatabases, ref skipped, ct)).ToArray();

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
        return new UsageReport(DateTimeOffset.UtcNow, days,
            next.Count + sources.OpenCodeDatabases.Concat(sources.KiloDatabases).Count(File.Exists), skipped, rows.Length,
            Sum(rows), Estimate(rows), groups, models, projects, daily, ApiPriceCatalog.AsOf,
            sessionGroups.Length, sessionGroups.Take(50).ToArray(), sources.WslDistributions);
    }

    private static string SessionId(string source, string session, string projectId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + session + "\0" + projectId));
        return "s_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// OpenCode rows from every database, reusing the previous result for a database whose file and
    /// write-ahead log are unchanged. Callers hold <see cref="_scanGate"/>.
    /// </summary>
    private UsageEvent[] ReadOpenCode(string source, IEnumerable<string> databases, ref int skipped, CancellationToken ct)
    {
        var events = new List<UsageEvent>();
        // Entries of other sources are kept: the OpenCode Go estimate reads OpenCode alone.
        var next = _databases.Where(item => !item.Key.StartsWith(source + "|", StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var database in databases.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(database)) continue;
            var key = source + "|" + database;
            var stamp = DatabaseStamp(database);
            if (!_databases.TryGetValue(key, out var cached) || cached.Stamp != stamp)
            {
                var read = ReadOpenCode(source, database, ref skipped, ct);
                if (read is null) continue;
                cached = new CachedDatabase(stamp, read);
            }
            next[key] = cached;
            events.AddRange(cached.Events);
        }
        _databases = next;
        return events.ToArray();
    }

    private sealed record CachedDatabase(string Stamp, UsageEvent[] Events);

    /// <summary>
    /// Identifies a database's current contents without querying it. Size and modification time can
    /// repeat for two quick same-sized writes, so the SQLite header's change counter (rollback journal)
    /// and the WAL header's checkpoint sequence and salts (reset when the log restarts) are included;
    /// frames appended to the log grow its size.
    /// </summary>
    private static string DatabaseStamp(string database)
    {
        static string Of(string path, int headerBytes)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return "-";
            var header = new byte[headerBytes];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                header = header[..stream.ReadAtLeast(header, headerBytes, throwOnEndOfStream: false)];
            return file.Length + "@" + file.LastWriteTimeUtc.Ticks + ":" + Convert.ToHexString(header);
        }
        try { return Of(database, 100) + "|" + Of(database + "-wal", 32); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "unreadable"; }
    }

    /// <summary>
    /// Reads a database on a network or WSL share from a private local copy. SQLite fetches pages one
    /// small read at a time, which over <c>\\wsl.localhost</c> turned a 260 MB database into a 30-second
    /// query; copying it sequentially takes a couple of seconds. The copy is deleted straight away.
    /// </summary>
    private static UsageEvent[]? ReadOpenCode(string source, string database, ref int skipped, CancellationToken ct)
    {
        if (!database.StartsWith(@"\\", StringComparison.Ordinal)) return QueryOpenCode(source, database, readOnly: true, ref skipped, ct);
        var temp = Path.GetTempPath();
        RemoveStaleSnapshots(temp);
        var directory = Path.Combine(temp, SnapshotPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, "opencode.db");
            // A copy taken while OpenCode checkpoints can mix old and new pages; take it again then.
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var before = DatabaseStamp(database);
                File.Copy(database, copy, overwrite: true);
                if (File.Exists(database + "-wal")) File.Copy(database + "-wal", copy + "-wal", overwrite: true);
                else File.Delete(copy + "-wal");
                if (DatabaseStamp(database) == before || attempt == 2) break;
            }
            // The copy is ours, so it opens read-write: SQLite needs to create the -shm for its WAL.
            return QueryOpenCode(source, copy, readOnly: false, ref skipped, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            skipped++;
            return null;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private const string SnapshotPrefix = "agentnotify-opencode-";

    /// <summary>Deletes snapshots a crashed scan left behind; a running scan's copy is minutes younger.</summary>
    private static void RemoveStaleSnapshots(string temp)
    {
        try
        {
            foreach (var stale in new DirectoryInfo(temp).EnumerateDirectories(SnapshotPrefix + "*")
                         .Where(item => item.CreationTimeUtc < DateTime.UtcNow.AddHours(-1)))
                stale.Delete(recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static UsageEvent[]? QueryOpenCode(string source, string database, bool readOnly, ref int skipped, CancellationToken ct)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
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
                events.Add(new UsageEvent(source, timestamp, model, provider,
                    reader.IsDBNull(1) ? "" : reader.GetString(1), project,
                    reader.IsDBNull(0) ? null : reader.GetString(0), counts, 0));
            }
            return events.ToArray();
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            skipped++;
            return null;
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
            // The Gemini CLI keeps each session as one JSON document under <project>/chats.
            var gemini = source == "gemini_cli";
            var projects = gemini ? GeminiProjects(root) : null;
            try
            {
                paths = Directory.EnumerateFiles(root, gemini ? "session-*.json" : "*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).Where(path => !gemini || Path.GetFileName(Path.GetDirectoryName(path)) == "chats").ToArray();
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
                    next[path] = new CachedFile(info.Length, info.LastWriteTimeUtc,
                        gemini ? ParseGemini(path, info.Length, projects!, ct) : ParseFile(source, path, ct));
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
        string? serviceTier = null;
        var mirrored = false;
        if (source == "muse") return ParseMuse(path, ct);
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length > 1024 * 1024 || line.Length == 0) continue;
            // Most lines are prompts, tool output, and reasoning. Skipping those before JSON parsing
            // is what keeps a first scan of large ledgers over the WSL share bearable.
            if (source == "claude_code" ? !line.Contains("\"assistant\"", StringComparison.Ordinal)
                : !(line.Contains("\"token_count\"", StringComparison.Ordinal) ||
                    line.Contains("\"turn_context\"", StringComparison.Ordinal) ||
                    line.Contains("\"session_meta\"", StringComparison.Ordinal) ||
                    line.Contains("\"thread_settings_applied\"", StringComparison.Ordinal))) continue;
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
                    serviceTier = String(payload, "service_tier") ?? serviceTier;
                    project = ProjectInfo.FromDirectory(String(payload, "cwd"), project);
                }
                else if (type == "event_msg" && String(payload, "type") == "thread_settings_applied")
                {
                    // Fast mode is recorded as the "priority" service tier and is billed at its own rates.
                    serviceTier = String(Child(payload, "thread_settings"), "service_tier") ?? serviceTier;
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
                    events.Add(new UsageEvent("codex", timestamp, model, "openai", session, project, null, delta, 0, serviceTier));
                }
            }
            catch (JsonException) { /* One malformed line must not discard the rest of a session. */ }
        }
        return events.ToArray();
    }

    /// <summary>
    /// Muse Code writes one <c>session.jsonl</c> per session and per subagent
    /// (<c>&lt;session&gt;/subagent/&lt;child&gt;/session.jsonl</c>). Every model call is a
    /// <c>runtime.session</c> event of kind <c>model_completed</c> in the file of the session that
    /// made it, so a call is never repeated in its parent. Usage follows OpenAI's convention:
    /// <c>input_tokens</c> includes <c>cached_tokens</c>, and <c>output_tokens</c> includes
    /// <c>reasoning_tokens</c>. Subagent calls are grouped under the parent session and its project.
    /// </summary>
    private static UsageEvent[] ParseMuse(string path, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var parentDirectory = Path.GetDirectoryName(directory) ?? "";
        var isSubagent = Path.GetFileName(parentDirectory) == "subagent";
        var sessionDirectory = isSubagent ? Path.GetDirectoryName(parentDirectory) ?? directory : directory;
        var session = Path.GetFileName(sessionDirectory);
        var project = MuseProject(path, ct, out var calls);
        if (project == ProjectInfo.Unknown && isSubagent)
            project = MuseProject(Path.Combine(sessionDirectory, "session.jsonl"), ct, out _, projectOnly: true);
        return calls.Select(call => call with { Session = session, Project = project }).ToArray();
    }

    private static ProjectInfo MuseProject(string path, CancellationToken ct, out List<UsageEvent> calls,
        bool projectOnly = false)
    {
        calls = [];
        var project = ProjectInfo.Unknown;
        if (!File.Exists(path)) return project;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var lines = 0;
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            // The route facts come first; a parent file is read only until they appear.
            if (projectOnly && ++lines > 5000) break;
            if (line.Length > 1024 * 1024 || line.Length == 0) continue;
            var route = project == ProjectInfo.Unknown && line.Contains("\"route_facts\"", StringComparison.Ordinal);
            if (!route && (projectOnly || !line.Contains("\"model_completed\"", StringComparison.Ordinal))) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var row = json.RootElement;
                var payload = Child(row, "payload");
                if (route && String(payload, "kind") == "route_facts")
                {
                    project = ProjectInfo.FromDirectory(String(Child(payload, "record"), "cwd"));
                    if (projectOnly && project != ProjectInfo.Unknown) break;
                    continue;
                }
                var call = Child(payload, "event");
                var usage = Child(call, "usage");
                var model = String(call, "model");
                if (String(call, "kind") != "model_completed" || usage.ValueKind != JsonValueKind.Object ||
                    string.IsNullOrWhiteSpace(model)) continue;
                var micros = Child(row, "recorded_at");
                if (micros.ValueKind != JsonValueKind.Number || !micros.TryGetInt64(out var recorded)) continue;
                DateTimeOffset timestamp;
                try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(recorded / 1000); }
                catch (ArgumentOutOfRangeException) { continue; }
                var input = Number(usage, "input_tokens");
                var cached = Math.Min(input, Math.Max(Number(usage, "cached_tokens"), Number(usage, "cache_read_tokens")));
                var output = Number(usage, "output_tokens");
                var counts = new TokenCounts(input - cached, output, cached, Number(usage, "cache_write_tokens"),
                    Math.Min(output, Number(usage, "reasoning_tokens")));
                if (counts.Total == 0) continue;
                calls.Add(new UsageEvent("muse", timestamp, model, "meta", "", ProjectInfo.Unknown,
                    String(row, "id"), counts, 0));
            }
            catch (JsonException) { /* One malformed line must not discard the rest of a session. */ }
        }
        return project;
    }

    /// <summary>
    /// Gemini CLI chats: <c>tmp/&lt;project&gt;/chats/session-*.json</c>, one JSON document per
    /// session whose <c>gemini</c> messages carry <c>tokens</c>. <c>input</c> includes <c>cached</c>;
    /// <c>thoughts</c> and <c>tool</c> are counted separately from <c>output</c> and <c>input</c>.
    /// </summary>
    private static UsageEvent[] ParseGemini(string path, long length, IReadOnlyDictionary<string, string> projects,
        CancellationToken ct)
    {
        if (length > 64 * 1024 * 1024) return [];
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) ?? "";
        var project = projects.TryGetValue(folder, out var directory) ? ProjectInfo.FromDirectory(directory) : ProjectInfo.Unknown;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        var session = String(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path);
        var messages = Child(root, "messages");
        if (messages.ValueKind != JsonValueKind.Array) return [];
        var events = new List<UsageEvent>();
        foreach (var message in messages.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var tokens = Child(message, "tokens");
            var model = String(message, "model");
            if (String(message, "type") != "gemini" || tokens.ValueKind != JsonValueKind.Object ||
                string.IsNullOrWhiteSpace(model) || !DateTimeOffset.TryParse(String(message, "timestamp"),
                    System.Globalization.CultureInfo.InvariantCulture, out var timestamp)) continue;
            var input = Number(tokens, "input");
            var cached = Math.Min(input, Number(tokens, "cached"));
            var thoughts = Number(tokens, "thoughts");
            var counts = new TokenCounts(input - cached + Number(tokens, "tool"), Number(tokens, "output") + thoughts,
                cached, 0, thoughts);
            if (counts.Total == 0) continue;
            events.Add(new UsageEvent("gemini_cli", timestamp, model, "google", session, project,
                String(message, "id"), counts, 0));
        }
        return events.ToArray();
    }

    /// <summary>
    /// Maps a Gemini CLI project folder name to its directory. Current versions name the folder after
    /// the project (listed in <c>~/.gemini/projects.json</c>); older ones used the SHA-256 of the path.
    /// </summary>
    private static IReadOnlyDictionary<string, string> GeminiProjects(string tmpRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = new FileInfo(Path.Combine(Path.GetDirectoryName(tmpRoot.TrimEnd('\\', '/')) ?? "", "projects.json"));
            if (!file.Exists || file.Length > 4 * 1024 * 1024) return result;
            using var stream = file.OpenRead();
            using var json = JsonDocument.Parse(stream);
            var projects = Child(json.RootElement, "projects");
            if (projects.ValueKind != JsonValueKind.Object) return result;
            foreach (var project in projects.EnumerateObject())
            {
                if (project.Value.ValueKind == JsonValueKind.String && project.Value.GetString() is { Length: > 0 } slug)
                    result.TryAdd(slug, project.Name);
                result.TryAdd(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project.Name))), project.Name);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
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
            var rate = ApiPriceCatalog.RateFor(row.Source, row.Provider, row.Model, row.Timestamp, row.Counts, row.ServiceTier);
            if (rate is null)
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
        ProjectInfo Project, string? Identity, TokenCounts Counts, long CacheWrite1h, string? ServiceTier = null);
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
    public string ContractVersion => "4";
}
