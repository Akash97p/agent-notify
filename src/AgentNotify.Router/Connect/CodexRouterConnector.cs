using System.Text;

namespace AgentNotify.Router.Connect;

/// <summary>
/// Points Codex at the router by editing its <c>config.toml</c> and generating the model catalogue
/// its picker reads.
/// </summary>
/// <remarks>
/// The file belongs to Codex, so the edit is surgical rather than generative: AgentNotify's lines
/// live between two marker comments and nothing else in the file is reordered, reformatted, or
/// dropped. There are two regions because TOML is positional — bare keys must appear before the first
/// table header, so <c>model</c>, <c>model_provider</c>, and <c>model_catalog_json</c> go at the top
/// of the file while the <c>[model_providers.agentnotify]</c> table goes at the end.
///
/// Keys the owner already had are commented out inside the top region rather than deleted, and their
/// original values are recorded in AgentNotify's own state so disconnecting restores them.
/// </remarks>
public sealed class CodexRouterConnector
{
    public const string Id = "codex";
    public const string ProviderName = "agentnotify";

    internal const string TopBegin = "# >>> agentnotify router (managed) >>>";
    internal const string TopEnd = "# <<< agentnotify router (managed) <<<";
    internal const string TableBegin = "# >>> agentnotify router provider (managed) >>>";
    internal const string TableEnd = "# <<< agentnotify router provider (managed) <<<";

    /// <summary>Bare keys the managed region owns while connected.</summary>
    internal static readonly string[] ManagedKeys =
    [
        "model", "model_provider", "model_catalog_json", "model_reasoning_effort",
        "default_subagent_model", "default_subagent_reasoning_effort", "review_model"
    ];

    /// <summary>
    /// The settings beyond the main model that Codex resolves per model, each mapped to its own
    /// configuration key. A subagent or a review runs its own turns, so pointing them at a cheaper
    /// routed model is usually the point of having a router at all.
    /// </summary>
    public static readonly IReadOnlyList<RouterAgentOption> Options =
    [
        new("reasoning_effort", "Reasoning effort", "How hard the main model thinks.", false,
            ["minimal", "low", "medium", "high", "xhigh"]),
        new("subagent_model", "Subagent model", "The model Codex subagents run on.", true, []),
        new("subagent_reasoning_effort", "Subagent reasoning effort", "How hard subagents think.", false,
            ["minimal", "low", "medium", "high", "xhigh"]),
        new("review_model", "Review model", "The model code review runs on.", true, []),
        new("shell_tool", "Shell tool",
            "Which shell tool Codex offers a routed model. shell_command is one plain function call and works with the most models.",
            false, CodexModelCatalog.ShellTypes)
    ];

    /// <summary>Which Codex key each option writes.</summary>
    internal static readonly IReadOnlyDictionary<string, string> OptionKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reasoning_effort"] = "model_reasoning_effort",
            ["subagent_model"] = "default_subagent_model",
            ["subagent_reasoning_effort"] = "default_subagent_reasoning_effort",
            ["review_model"] = "review_model"
        };

    private readonly string _configPath;

    public CodexRouterConnector(string codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) throw new ArgumentException("codexHome is required", nameof(codexHome));
        Home = codexHome;
        _configPath = Path.Combine(codexHome, "config.toml");
    }

    public string Home { get; }
    public string ConfigPath => _configPath;
    public string DisplayName => "Codex";

    /// <summary>The option values the managed region currently carries, for display.</summary>
    public IReadOnlyDictionary<string, string> CurrentOptions()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_configPath)) return values;
        var current = ReadRegionAssignments(File.ReadAllText(_configPath));
        foreach (var (option, key) in OptionKeys)
            if (current.TryGetValue(key, out var value)) values[option] = value;
        return values;
    }

    /// <summary>The bare keys the managed top region assigns right now, with quotes removed.</summary>
    internal static Dictionary<string, string> ReadRegionAssignments(string toml)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inside = false;
        foreach (var raw in SplitLines(toml))
        {
            var line = raw.Trim();
            if (line == TopBegin) { inside = true; continue; }
            if (line == TopEnd) break;
            if (!inside || line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim().Trim('"');
            if (value.Length > 0) values[key] = value;
        }
        return values;
    }

    public bool Detected => Directory.Exists(Home) || File.Exists(_configPath);

    public bool IsConnected() =>
        File.Exists(_configPath) && File.ReadAllText(_configPath).Contains(TopBegin, StringComparison.Ordinal);

    /// <summary>
    /// Writes the managed regions. <paramref name="catalogPath"/> is the generated catalogue, and
    /// <paramref name="routerKey"/> is embedded so Codex needs no environment variable; returns the
    /// values the file held before, for a later restore.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Apply(
        string baseUrl,
        string routerKey,
        string catalogPath,
        string model,
        IReadOnlyDictionary<string, string>? options = null)
    {
        var original = File.Exists(_configPath) ? File.ReadAllText(_configPath) : "";
        var previous = ReadManagedKeys(original);

        // Only the keys this region actually writes are taken over; anything else the owner set is
        // left exactly as it is.
        var written = new HashSet<string>(["model", "model_provider", "model_catalog_json"], StringComparer.Ordinal);
        foreach (var (option, key) in OptionKeys)
            if (options is not null && options.TryGetValue(option, out var value) && !string.IsNullOrWhiteSpace(value))
                written.Add(key);

        var stripped = RemoveRegions(original, written);
        var body = string.Join("\n", BuildTopRegion(model, catalogPath, previous, options));
        var table = string.Join("\n", BuildProviderRegion(baseUrl, routerKey));

        var result = new StringBuilder();
        result.Append(body).Append('\n');
        var rest = stripped.TrimStart('\n');
        if (rest.Length > 0)
        {
            result.Append('\n').Append(rest);
            if (!rest.EndsWith('\n')) result.Append('\n');
        }
        result.Append('\n').Append(table).Append('\n');

        UnixFilePermissions.CreateOwnerOnlyDirectory(Home);
        File.WriteAllText(_configPath, result.ToString());
        // The file now holds the router key, so it must not be readable by other users.
        UnixFilePermissions.RestrictFile(_configPath);
        return previous;
    }

    /// <summary>Removes the managed regions and puts the owner's own values back.</summary>
    public void Remove(IReadOnlyDictionary<string, string?> previous)
    {
        if (!File.Exists(_configPath)) return;
        // Removing the regions also un-comments the owner's own lines, so only a key that has no line
        // left in the file is written back from what was recorded at connect time.
        var stripped = RemoveRegions(File.ReadAllText(_configPath));
        var alreadyPresent = BareAssignments(stripped);
        var restored = new List<string>();
        foreach (var key in ManagedKeys)
        {
            if (alreadyPresent.Contains(key)) continue;
            if (previous.TryGetValue(key, out var value) && value is not null)
                restored.Add($"{key} = {value}");
        }

        var result = new StringBuilder();
        if (restored.Count > 0) result.Append(string.Join("\n", restored)).Append('\n');
        var rest = stripped.TrimStart('\n');
        if (rest.Length > 0)
        {
            if (restored.Count > 0) result.Append('\n');
            result.Append(rest);
            if (!rest.EndsWith('\n')) result.Append('\n');
        }

        File.WriteAllText(_configPath, result.ToString());
        UnixFilePermissions.RestrictFile(_configPath);
    }

    private static IEnumerable<string> BuildTopRegion(
        string model,
        string catalogPath,
        IReadOnlyDictionary<string, string?> previous,
        IReadOnlyDictionary<string, string>? options)
    {
        yield return TopBegin;
        yield return "# Written by AgentNotify. Change it on the Model router pages, or run";
        yield return "# 'agentnotify router disconnect codex' to put your own settings back.";
        foreach (var key in ManagedKeys)
        {
            if (previous.TryGetValue(key, out var value) && value is not null)
                yield return $"# was: {key} = {value}";
        }
        yield return $"model = {Quote(model)}";
        yield return $"model_provider = {Quote(ProviderName)}";
        yield return $"model_catalog_json = {Quote(catalogPath)}";
        foreach (var (option, key) in OptionKeys)
        {
            if (options is not null && options.TryGetValue(option, out var value) && !string.IsNullOrWhiteSpace(value))
                yield return $"{key} = {Quote(value.Trim())}";
        }
        yield return TopEnd;
    }

    private static IEnumerable<string> BuildProviderRegion(string baseUrl, string routerKey)
    {
        yield return TableBegin;
        yield return $"[model_providers.{ProviderName}]";
        yield return "name = \"AgentNotify router\"";
        yield return $"base_url = {Quote(baseUrl)}";
        yield return "wire_api = \"responses\"";
        // A literal token keeps this one-click: Codex launched from any shell, editor, or launchd job
        // authenticates without an environment variable being set for it.
        yield return $"experimental_bearer_token = {Quote(routerKey)}";
        yield return TableEnd;
    }

    /// <summary>The current values of the managed bare keys, before AgentNotify changes them.</summary>
    internal static Dictionary<string, string?> ReadManagedKeys(string toml)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in ManagedKeys) values[key] = null;

        var managed = false;
        foreach (var raw in SplitLines(toml))
        {
            var line = raw.Trim();
            if (line == TopBegin || line == TableBegin) { managed = true; continue; }
            if (line == TopEnd || line == TableEnd) { managed = false; continue; }
            if (managed)
            {
                // A previous connect recorded the owner's value as a "# was:" line; keep it, because
                // reconnecting twice must not lose what the file looked like originally.
                const string was = "# was: ";
                if (line.StartsWith(was, StringComparison.Ordinal))
                    Record(values, line[was.Length..]);
                continue;
            }
            if (line.StartsWith('[')) break;
            Record(values, line);
        }

        return values;
    }

    private static void Record(Dictionary<string, string?> values, string line)
    {
        var equals = line.IndexOf('=');
        if (equals <= 0) return;
        var key = line[..equals].Trim();
        if (!ManagedKeys.Contains(key)) return;
        var value = line[(equals + 1)..].Trim();
        if (value.Length > 0 && values.TryGetValue(key, out var existing) && existing is null)
            values[key] = value;
    }

    internal const string DisabledPrefix = "# agentnotify disabled: ";

    /// <summary>
    /// Drops both managed regions, leaving every other line exactly as it was.
    /// <paramref name="keysToDisable"/> names the keys the managed region is about to set: the owner's
    /// own assignment of one of those is commented out, because TOML rejects a key assigned twice and
    /// Codex would refuse to start. A key the region does not set is left alone, and disconnecting
    /// reverses every comment.
    /// </summary>
    internal static string RemoveRegions(string toml, IReadOnlyCollection<string>? keysToDisable = null)
    {
        if (toml.Length == 0) return "";
        var kept = new List<string>();
        var skipping = false;
        var beforeFirstTable = true;
        foreach (var raw in SplitLines(toml))
        {
            var line = raw.Trim();
            if (line == TopBegin || line == TableBegin) { skipping = true; continue; }
            if (line == TopEnd || line == TableEnd) { skipping = false; continue; }
            if (skipping) continue;
            if (line.StartsWith('[')) beforeFirstTable = false;

            if (line.StartsWith(DisabledPrefix, StringComparison.Ordinal))
            {
                // A line disabled by an earlier connect comes back unless it is about to be set again.
                var original = line[DisabledPrefix.Length..];
                kept.Add(Assigns(original, keysToDisable) ? raw : original);
                continue;
            }

            if (beforeFirstTable && Assigns(line, keysToDisable))
            {
                kept.Add(DisabledPrefix + line);
                continue;
            }

            kept.Add(raw);
        }

        // Collapse the blank lines the removed regions leave behind.
        while (kept.Count > 0 && kept[0].Trim().Length == 0) kept.RemoveAt(0);
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        return kept.Count == 0 ? "" : string.Join("\n", kept) + "\n";
    }

    /// <summary>The managed keys the bare (pre-table) part of this file already assigns.</summary>
    private static HashSet<string> BareAssignments(string toml)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in SplitLines(toml))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) break;
            if (line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            if (ManagedKeys.Contains(key)) found.Add(key);
        }
        return found;
    }

    private static bool Assigns(string line, IReadOnlyCollection<string>? keys)
    {
        if (keys is null || keys.Count == 0 || line.StartsWith('#')) return false;
        var equals = line.IndexOf('=');
        return equals > 0 && keys.Contains(line[..equals].Trim());
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>A TOML basic string. Backslashes and quotes are the only characters that need it.</summary>
    internal static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
