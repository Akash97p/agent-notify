using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentNotify.Core.Router.Connect;

/// <summary>
/// Generates the model catalogue Codex reads through its <c>model_catalog_json</c> setting, so every
/// selector the router can serve appears in Codex's own model picker.
/// </summary>
/// <remarks>
/// The shape is Codex's, not ours: a <c>models</c> array whose entries must carry
/// <c>slug</c>, <c>display_name</c>, <c>supported_reasoning_levels</c>, <c>shell_type</c>,
/// <c>visibility</c>, <c>supported_in_api</c>, <c>priority</c>, <c>support_verbosity</c>,
/// <c>truncation_policy</c>, <c>experimental_supported_tools</c>, <c>model_messages</c>, and either
/// <c>base_instructions</c> or <c>model_messages.instructions_template</c>. Codex refuses to start
/// when a field is missing, so the required set was established against the installed Codex rather
/// than assumed, and a catalogue must contain at least one model.
///
/// Codex normally receives the prompt for its own models from its backend. A routed third-party model
/// has no such entry, so AgentNotify supplies its own short instructions. They are deliberately
/// plain: the owner can replace them with their own file, and nothing here copies another product's
/// prompt.
/// </remarks>
public static class CodexModelCatalog
{
    public const string FileName = "codex-model-catalog.json";

    /// <summary>Codex's own limit on how many models a picker can usefully show.</summary>
    public const int MaxEntries = 200;

    /// <summary>
    /// Which shell tool Codex offers a routed model. <c>shell_command</c> is the default because it is
    /// one ordinary function call, which third-party models handle far more reliably than the stateful
    /// <c>unified_exec</c> session tool. Codex's other value, <c>local</c>, is deliberately not offered:
    /// it is a built-in tool type rather than a function, and translation to another wire drops it, so
    /// the model would be left with no way to run anything.
    /// </summary>
    public const string DefaultShellType = "shell_command";

    public static readonly IReadOnlyList<string> ShellTypes = ["shell_command", "unified_exec", "default", "disabled"];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The instructions a routed model receives when the owner supplies none.</summary>
    public const string DefaultInstructions =
        """
        You are a coding agent running in the user's terminal, reached through a local model router.
        You are working in the user's repository on their machine.

        - Prefer reading files and running commands to guessing. Explain what you are about to do when
          it changes files, installs anything, or touches the network.
        - Make the smallest change that solves the problem, and match the conventions already in the
          code you are editing.
        - Use the tools you are given for shell commands and file edits rather than describing the
          command for the user to run.
        - When you finish, say plainly what you changed and what you did not verify.
        """;

    /// <summary>
    /// Builds the catalogue for one router snapshot. Every enabled upstream model, every enabled
    /// route, and <c>combo/&lt;name&gt;</c> for each combo becomes a selectable entry.
    /// </summary>
    public static JsonObject Build(RouterSnapshot snapshot, string? instructions = null, string? shellType = null,
        IReadOnlyDictionary<string, JsonObject>? nativeEntries = null)
    {
        var shell = ShellTypes.Contains(shellType, StringComparer.Ordinal) ? shellType! : DefaultShellType;
        var text = string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions!.Trim();
        var models = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var priority = 1;

        void Add(string slug, string label, string description)
        {
            if (models.Count >= MaxEntries || !seen.Add(slug)) return;
            models.Add(Entry(slug, label, description, priority++, text, shell));
        }

        // Routes first: an owner who made an alias or a combo means to pick it, and Codex orders the
        // picker by priority.
        foreach (var route in snapshot.Routes.Where(r => r.Enabled))
        {
            var kind = route.Kind == RouterKind.Combo ? "failover combo" : "alias";
            var describes = string.Join(", ", route.Targets);
            Add(route.Name, route.Name, $"Router {kind}: {describes}");
            if (route.Kind == RouterKind.Combo)
                Add("combo/" + route.Name, "combo/" + route.Name, $"Router failover combo: {describes}");
        }

        foreach (var upstream in snapshot.Upstreams.Where(u => u.Enabled))
        {
            foreach (var model in upstream.Models)
            {
                var slug = upstream.Slug + "/" + model;
                // A ChatGPT-plan model is one of Codex's own, reached through the same backend: its
                // real entry (prompt, context window, reasoning levels, tools) is reused so it behaves
                // exactly as it does without the router.
                if (upstream.Auth == RouterAuth.CodexChatGpt && nativeEntries is not null &&
                    nativeEntries.TryGetValue(model, out var native) && models.Count < MaxEntries && seen.Add(slug))
                {
                    var entry = (JsonObject)native.DeepClone();
                    entry["slug"] = slug;
                    entry["display_name"] = $"{(string?)native["display_name"] ?? model} · {upstream.Label}";
                    entry["priority"] = priority++;
                    entry["visibility"] = "list";
                    models.Add(entry);
                    continue;
                }
                Add(slug, $"{upstream.Label} · {model}", $"Routed to {upstream.Label} as {model}");
            }
        }

        return new JsonObject { ["models"] = models };
    }

    /// <summary>Writes the catalogue for this snapshot and returns how many models it holds.</summary>
    public static int Write(string path, RouterSnapshot snapshot, string? instructions = null, string? shellType = null,
        IReadOnlyDictionary<string, JsonObject>? nativeEntries = null)
    {
        var catalog = Build(snapshot, instructions, shellType, nativeEntries);
        var count = (catalog["models"] as JsonArray)?.Count ?? 0;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) UnixFilePermissions.CreateOwnerOnlyDirectory(directory);
        File.WriteAllText(path, catalog.ToJsonString(WriteOptions) + Environment.NewLine);
        UnixFilePermissions.RestrictFile(path);
        return count;
    }

    /// <summary>
    /// Codex's own model entries, from the catalogue its backend last sent (<c>models_cache.json</c>),
    /// keyed by model ID. Empty when Codex has not cached one.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonObject> ReadNative(string codexHome)
    {
        var entries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(codexHome, "models_cache.json");
            if (!File.Exists(path)) return entries;
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["models"] is JsonArray models)
            {
                foreach (var model in models.OfType<JsonObject>())
                    if ((string?)model["slug"] is { Length: > 0 } slug) entries[slug] = model;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return entries;
    }

    /// <summary>How many models the catalogue at this path currently holds, or 0 when unreadable.</summary>
    public static int CountAt(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("models", out var models) &&
                   models.ValueKind == JsonValueKind.Array
                ? models.GetArrayLength()
                : 0;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static JsonObject Entry(string slug, string displayName, string description, int priority, string instructions, string shellType) => new()
    {
        ["slug"] = slug,
        ["display_name"] = displayName,
        ["description"] = description,
        ["base_instructions"] = instructions,
        ["supported_reasoning_levels"] = new JsonArray(
            Effort("low", "Fast responses with lighter reasoning"),
            Effort("medium", "Balances speed and reasoning depth"),
            Effort("high", "Greater reasoning depth for complex problems")),
        ["default_reasoning_level"] = "medium",
        ["shell_type"] = shellType,
        ["visibility"] = "list",
        ["supported_in_api"] = true,
        ["priority"] = priority,
        ["support_verbosity"] = false,
        ["truncation_policy"] = new JsonObject { ["mode"] = "tokens", ["limit"] = 10000 },
        ["experimental_supported_tools"] = new JsonArray(),
        ["model_messages"] = new JsonObject(),
        ["input_modalities"] = new JsonArray("text"),
        ["context_window"] = 200000,
        ["max_context_window"] = 200000
    };

    private static JsonObject Effort(string effort, string description) =>
        new() { ["effort"] = effort, ["description"] = description };
}
