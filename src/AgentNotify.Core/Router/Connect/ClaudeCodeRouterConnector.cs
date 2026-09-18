using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentNotify.Core.Router.Connect;

/// <summary>
/// Points Claude Code at the router through the <c>env</c> block of its <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Two mechanisms are used together, because Claude Code has both. Its <c>modelPicker</c> setting adds
/// rows to the <c>/model</c> list, which is how a routed selector becomes something the owner can pick
/// by name; each row also declares which model Claude Code already knows it <c>behavesAs</c>, because
/// otherwise Claude Code warns that it cannot tell the model's context window or capabilities. Its
/// built-in entries resolve their model name from the environment instead, so those variables are set
/// as well and the owner decides what Opus, Sonnet, Haiku, and the background model point at.
///
/// The file is merged as JSON, so every other setting, hook, and permission the owner has is
/// preserved; only the keys listed in <see cref="ManagedVariables"/> are touched, and their previous
/// values are recorded for a restore.
/// </remarks>
public sealed class ClaudeCodeRouterConnector
{
    public const string Id = "claude_code";

    /// <summary>The picker slots Claude Code resolves through the environment, in menu order.</summary>
    public static readonly IReadOnlyDictionary<string, string> Slots = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["default"] = "ANTHROPIC_MODEL",
        ["opus"] = "ANTHROPIC_DEFAULT_OPUS_MODEL",
        ["sonnet"] = "ANTHROPIC_DEFAULT_SONNET_MODEL",
        ["haiku"] = "ANTHROPIC_DEFAULT_HAIKU_MODEL",
        // What Claude Code uses for its own background work, which is the closest thing it has to a
        // subagent model: pointing it at something cheap is usually the reason to change it.
        ["small_fast"] = "ANTHROPIC_SMALL_FAST_MODEL"
    };

    /// <summary>
    /// What Claude Code needs to know about a model it has never heard of. <c>behaves_as</c> names the
    /// known model whose client-side handling a routed row borrows; without it Claude Code assumes a
    /// 200k context window and says so on every start.
    /// </summary>
    public static readonly IReadOnlyList<RouterAgentOption> Options =
    [
        new("behaves_as", "Behaves as",
            "The model Claude Code already knows whose handling a routed model borrows.", false,
            ["claude-sonnet-4-5", "claude-sonnet-4-6", "claude-opus-4-5", "claude-opus-4-6", "claude-haiku-4-5"]),
        new("replace_built_in_options", "Show only routed models",
            "When on, the /model list shows Default plus the routed models instead of adding them after Anthropic's own lineup.",
            false, ["on", "off"])
    ];

    public const string DefaultBehavesAs = "claude-sonnet-4-5";

    /// <summary>The settings key holding the extra rows this connector adds to the picker.</summary>
    private const string PickerKey = "modelPicker";

    /// <summary>Every variable this connector owns while connected.</summary>
    public static readonly string[] ManagedVariables =
        ["ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", .. Slots.Values];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ClaudeCodeRouterConnector(string claudeHome)
    {
        if (string.IsNullOrWhiteSpace(claudeHome)) throw new ArgumentException("claudeHome is required", nameof(claudeHome));
        Home = claudeHome;
        ConfigPath = Path.Combine(claudeHome, "settings.json");
    }

    public string Home { get; }
    public string ConfigPath { get; }
    public string DisplayName => "Claude Code";

    public bool Detected => Directory.Exists(Home) || File.Exists(ConfigPath);

    public bool IsConnected()
    {
        var env = ReadEnv(Load(out _));
        return env is not null &&
               env["ANTHROPIC_BASE_URL"] is JsonValue value &&
               (value.GetValue<string>() ?? "").Contains("/router", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the base URL, the embedded router key, and one selector per picker slot. Returns the
    /// values the file held before, for a later restore.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Apply(
        string anthropicBaseUrl,
        string routerKey,
        IReadOnlyDictionary<string, string> slotModels,
        IReadOnlyList<(string Selector, string Description)> rows,
        IReadOnlyDictionary<string, string>? options = null)
    {
        var root = Load(out var unreadable);
        if (unreadable)
            throw new InvalidOperationException(
                $"'{ConfigPath}' is not valid JSON, so AgentNotify will not rewrite it. Fix or move the file and try again.");

        var env = ReadEnv(root) ?? new JsonObject();
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variable in ManagedVariables)
            previous[variable] = env[variable] is JsonValue existing ? existing.GetValue<string>() : null;

        env["ANTHROPIC_BASE_URL"] = anthropicBaseUrl;
        env["ANTHROPIC_AUTH_TOKEN"] = routerKey;
        foreach (var (slot, variable) in Slots)
        {
            if (slotModels.TryGetValue(slot, out var model) && !string.IsNullOrWhiteSpace(model))
                env[variable] = model;
            else
                env.Remove(variable);
        }

        // The owner's own picker rows, if they have any, are recorded whole so a restore is exact.
        previous[PickerKey] = root[PickerKey]?.ToJsonString();

        var behavesAs = options is not null && options.TryGetValue("behaves_as", out var declared) && declared.Length > 0
            ? declared
            : DefaultBehavesAs;
        var replaceBuiltIn = options is not null &&
            options.TryGetValue("replace_built_in_options", out var replace) &&
            string.Equals(replace, "on", StringComparison.OrdinalIgnoreCase);

        var pickerRows = new JsonArray();
        foreach (var (selector, description) in rows)
        {
            pickerRows.Add(new JsonObject
            {
                ["model"] = selector,
                ["label"] = selector,
                ["description"] = description,
                ["behavesAs"] = behavesAs
            });
        }

        root[PickerKey] = new JsonObject
        {
            ["options"] = pickerRows,
            ["replaceBuiltInOptions"] = replaceBuiltIn
        };

        root["env"] = env;
        Save(root);
        return previous;
    }

    /// <summary>Removes the managed variables and puts the owner's own values back.</summary>
    public void Remove(IReadOnlyDictionary<string, string?> previous)
    {
        var root = Load(out var unreadable);
        if (unreadable) return;
        var env = ReadEnv(root);
        if (env is null) return;

        foreach (var variable in ManagedVariables)
        {
            if (previous.TryGetValue(variable, out var value) && value is not null)
                env[variable] = value;
            else
                env.Remove(variable);
        }

        if (previous.TryGetValue(PickerKey, out var picker) && picker is not null)
        {
            try { root[PickerKey] = JsonNode.Parse(picker); }
            catch (JsonException) { root.Remove(PickerKey); }
        }
        else
        {
            root.Remove(PickerKey);
        }

        // An env block that only ever held AgentNotify's variables is removed rather than left empty.
        if (env.Count == 0) root.Remove("env");
        else root["env"] = env;
        Save(root);
    }

    /// <summary>What the added picker rows are set to now, for display.</summary>
    public IReadOnlyDictionary<string, string> CurrentOptions()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = Load(out _);
        if (root[PickerKey] is not JsonObject picker) return values;
        if (picker["options"] is JsonArray rows && rows.Count > 0 &&
            rows[0] is JsonObject first && first["behavesAs"] is JsonValue behaves)
        {
            values["behaves_as"] = behaves.GetValue<string>();
        }
        values["replace_built_in_options"] =
            picker["replaceBuiltInOptions"] is JsonValue replace && replace.GetValue<bool>() ? "on" : "off";
        return values;
    }

    /// <summary>The selector each picker slot currently sends, for display.</summary>
    public IReadOnlyDictionary<string, string> CurrentSlots()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = ReadEnv(Load(out _));
        if (env is null) return result;
        foreach (var (slot, variable) in Slots)
        {
            if (env[variable] is JsonValue value && value.GetValue<string>() is { Length: > 0 } model)
                result[slot] = model;
        }
        return result;
    }

    private JsonObject Load(out bool unreadable)
    {
        unreadable = false;
        try
        {
            if (!File.Exists(ConfigPath)) return new JsonObject();
            var text = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            unreadable = true;
            return new JsonObject();
        }
    }

    private static JsonObject? ReadEnv(JsonObject root) => root["env"] as JsonObject;

    private void Save(JsonObject root)
    {
        UnixFilePermissions.CreateOwnerOnlyDirectory(Home);
        File.WriteAllText(ConfigPath, root.ToJsonString(WriteOptions) + Environment.NewLine);
        // The file now holds the router key.
        UnixFilePermissions.RestrictFile(ConfigPath);
    }
}
