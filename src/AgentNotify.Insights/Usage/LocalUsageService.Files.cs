using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Wsl;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Insights.Usage;

public sealed partial class LocalUsageService
{
    private void AddFiles(string source, IReadOnlyList<string> roots, Dictionary<string, CachedFile> next, ref int skipped, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // A Muse subagent file inherits its parent session's project; each parent is read once per scan.
        var museProjects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            // The Gemini CLI keeps each session as one JSON document under <project>/chats.
            var gemini = source == "gemini_cli";
            var pattern = gemini ? "session-*.json" : "*.jsonl";
            var projects = gemini ? GeminiProjects(root) : null;
            // Inside WSL the listing comes from the distribution itself: walking the share costs a
            // round trip per directory. Only files that changed are then read through the share.
            var files = WslFileListing.TryList(root, pattern, TimeSpan.FromSeconds(30))?
                .Select(file => (Path: file.WindowsPath, file.Length, Modified: file.ModifiedUtc)).ToArray();
            if (files is null)
            {
                try
                {
                    files = new DirectoryInfo(root).EnumerateFiles(pattern, new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }).Select(file => (Path: file.FullName, file.Length, Modified: file.LastWriteTimeUtc)).ToArray();
                }
                catch (IOException) { skipped++; continue; }
                catch (UnauthorizedAccessException) { skipped++; continue; }
            }

            foreach (var (path, length, modified) in files.Where(file => !gemini ||
                         Path.GetFileName(Path.GetDirectoryName(file.Path)) == "chats").OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                // Codex can retain the same rollout in both active and archived directories.
                if (source == "codex" && !names.Add(Path.GetFileName(path))) continue;
                try
                {
                    if (_files.TryGetValue(path, out var previous) && previous.Length == length && previous.Modified == modified)
                    {
                        next[path] = previous;
                        continue;
                    }
                    next[path] = new CachedFile(length, modified,
                        gemini ? ParseGemini(path, length, projects!, ct) : ParseFile(source, path, museProjects, ct));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    skipped++;
                }
            }
        }
    }

    private static UsageEvent[] ParseFile(string source, string path, Dictionary<string, ProjectInfo> museProjects,
        CancellationToken ct)
    {
        var events = new List<UsageEvent>();
        var model = "Unknown model";
        var session = Path.GetFileNameWithoutExtension(path);
        var project = ProjectInfo.Unknown;
        TokenCounts? previousTotal = null;
        string? serviceTier = null;
        var mirrored = false;
        if (source == "muse") return ParseMuse(path, museProjects, ct);
        using var reader = OpenSequential(path);
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
    private static UsageEvent[] ParseMuse(string path, Dictionary<string, ProjectInfo> parentProjects, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var parentDirectory = Path.GetDirectoryName(directory) ?? "";
        var isSubagent = Path.GetFileName(parentDirectory) == "subagent";
        var sessionDirectory = isSubagent ? Path.GetDirectoryName(parentDirectory) ?? directory : directory;
        var session = Path.GetFileName(sessionDirectory);
        var project = MuseProject(path, ct, out var calls);
        if (project == ProjectInfo.Unknown && isSubagent)
        {
            var parent = Path.Combine(sessionDirectory, "session.jsonl");
            if (!parentProjects.TryGetValue(parent, out project!))
                parentProjects[parent] = project = MuseProject(parent, ct, out _, projectOnly: true);
        }
        return calls.Select(call => call with { Session = session, Project = project }).ToArray();
    }

    private static ProjectInfo MuseProject(string path, CancellationToken ct, out List<UsageEvent> calls,
        bool projectOnly = false)
    {
        calls = [];
        var project = ProjectInfo.Unknown;
        if (!File.Exists(path)) return project;
        using var reader = OpenSequential(path);
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
    /// Opens a ledger for one forward read with a large buffer. Small reads each cost a round trip
    /// through the WSL share, which made a cold scan several times slower than the bytes justify.
    /// </summary>
    private static StreamReader OpenSequential(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.SequentialScan), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024 * 1024);

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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.SequentialScan);
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

}
