using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Wsl;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Insights.Usage;

public sealed partial class LocalUsageService
{
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

    /// <param name="renewalDay">
    /// The plan's renewal day of the month. When set, the monthly cap is compared with the current
    /// billing cycle instead of the last 30 days. The five-hour and weekly windows stay rolling.
    /// </param>
    /// <param name="zone">The time zone renewals happen in; the machine's local zone by default.</param>
    public async Task<OpenCodeGoEstimate> GetOpenCodeGoEstimateAsync(DateTimeOffset now,
        CancellationToken cancellationToken = default, int? renewalDay = null, TimeZoneInfo? zone = null)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => EstimateOpenCodeGo(now, renewalDay, zone ?? TimeZoneInfo.Local, cancellationToken),
                cancellationToken);
        }
        finally { _scanGate.Release(); }
    }

    private OpenCodeGoEstimate EstimateOpenCodeGo(DateTimeOffset now, int? renewalDay, TimeZoneInfo zone, CancellationToken ct)
    {
        if (!OpenCodeGoBillingCycle.IsValidRenewalDay(renewalDay)) renewalDay = null;
        var cycle = renewalDay is { } day ? OpenCodeGoBillingCycle.Current(now, day, zone) : ((DateTimeOffset, DateTimeOffset)?)null;
        var periods = new (string Key, string Label, DateTimeOffset Start, DateTimeOffset? Resets, decimal Fraction)[]
        {
            ("five_hour", "Last 5 hours", now.AddHours(-5), null, .20m),
            ("seven_day", "Last 7 days", now.AddDays(-7), null, .50m),
            cycle is { } current
                ? ("billing_cycle", "This billing cycle", current.Item1, current.Item2, 1m)
                : ("thirty_day", "Last 30 days", now.AddDays(-30), null, 1m)
        };
        var earliest = periods.Min(period => period.Start);
        var skipped = 0;
        var rows = ReadOpenCode("opencode", CurrentSources().OpenCodeDatabases, ref skipped, ct)
            .Where(row => row.Provider == "opencode-go" && row.Timestamp <= now &&
                          row.Timestamp >= earliest).ToArray();
        if (skipped > 0 && rows.Length == 0)
            return new OpenCodeGoEstimate("unavailable", [], "The local OpenCode usage database could not be read.", renewalDay);
        if (rows.Length == 0)
            return new OpenCodeGoEstimate("no_data", [], cycle is null
                ? "No OpenCode Go requests were found in the last 30 days on this machine."
                : "No OpenCode Go requests were found in this billing cycle on this machine.", renewalDay);

        var models = rows.GroupBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(group =>
            {
                var monthlyLimit = ApiPriceCatalog.OpenCodeGoMonthlyLimit(group.Key);
                var windows = periods.Select(period =>
                {
                    var selected = group.Where(row => row.Timestamp >= period.Start)
                        .Select(row => (Row: row, Rate: ApiPriceCatalog.OpenCodeGoRate(group.Key, row.Timestamp, row.Counts)))
                        .ToArray();
                    var unpriced = selected.Count(item => item.Rate is null);
                    var observed = selected.Where(item => item.Rate is not null)
                        .Sum(item => item.Rate!.EstimateUsd(item.Row.Counts, item.Row.CacheWrite1h));
                    var limit = monthlyLimit * period.Fraction;
                    double? percent = unpriced == 0 && limit is > 0
                        ? (double)(observed / limit.Value * 100m) : null;
                    return new OpenCodeGoWindowEstimate(period.Key, period.Label, observed, limit,
                        percent, selected.Length, unpriced, period.Start, period.Resets);
                }).ToArray();
                return new OpenCodeGoModelEstimate(group.Key, windows);
            }).ToArray();
        return new OpenCodeGoEstimate("estimated", models,
            (cycle is null
                ? "Local OpenCode requests only. Other clients, billing-cycle boundaries, and provider-side adjustments are unknown; these are not live remaining quotas."
                : "Local OpenCode requests only. Other clients and provider-side adjustments are unknown; these are not live remaining quotas.") +
            (skipped > 0 ? " Some local OpenCode databases could not be read." : ""), renewalDay);
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

}
