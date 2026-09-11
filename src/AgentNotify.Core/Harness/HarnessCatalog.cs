namespace AgentNotify.Core.Harness;

/// <summary>
/// A coding host AgentNotify can notify from automatically (hooks/plugin),
/// and where that host looks for its configuration.
/// </summary>
/// <remarks>
/// Like <see cref="Skills.AgentSkillCatalog"/>, every entry is a claim about
/// another product's on-disk layout. A wrong claim writes a file the host
/// never reads — which looks exactly like success. When a host moves,
/// correct the catalogue rather than adding a parallel list.
/// </remarks>
public sealed record HarnessTarget(
    string Id,
    string DisplayName,
    string[]? PersonalSegments,
    string[]? ProjectSegments,
    string Note)
{
    public bool HasDefaultLocation => PersonalSegments is not null;
}

/// <summary>The hosts with a bundled AgentNotify harness.</summary>
public static class HarnessCatalog
{
    public const string PluginFileName = "agentnotify.js";
    public const string HookScriptFileName = "agentnotify_hook.py";

    public static readonly HarnessTarget OpenCode = new(
        Id: "opencode",
        DisplayName: "OpenCode",
        PersonalSegments: [".config", "opencode", "plugins"],
        ProjectSegments: [".opencode", "plugins"],
        Note: "A dependency-free event plugin. Drop-in file, auto-loaded on restart.");

    public static readonly HarnessTarget Codex = new(
        Id: "codex",
        DisplayName: "Codex",
        PersonalSegments: [".codex"],
        ProjectSegments: [".codex"],
        Note: "Hook script plus hooks.json entries for approval and stop events.");

    public static readonly HarnessTarget ClaudeCode = new(
        Id: "claude",
        DisplayName: "Claude Code",
        PersonalSegments: [".claude"],
        ProjectSegments: [".claude"],
        Note: "Hook script plus settings.json entries for notification and stop events.");

    public static readonly HarnessTarget Gemini = new(
        Id: "gemini",
        DisplayName: "Gemini CLI",
        PersonalSegments: [".gemini"],
        ProjectSegments: [".gemini"],
        Note: "Hook script plus settings.json entries for notification, agent-end, and session-end events.");

    public static readonly HarnessTarget Copilot = new(
        Id: "copilot",
        DisplayName: "Copilot CLI",
        PersonalSegments: [".copilot"],
        ProjectSegments: [".github"],
        Note: "Hook script plus a hooks-directory JSON file for notification, stop, end, and error events.");

    public static readonly HarnessTarget Cursor = new(
        Id: "cursor",
        DisplayName: "Cursor",
        PersonalSegments: [".cursor"],
        ProjectSegments: [".cursor"],
        Note: "Hook script plus hooks.json entries for agent stop and session end.");

    public static readonly HarnessTarget Muse = new(
        Id: "muse",
        DisplayName: "Muse Code",
        PersonalSegments: [".config", "muse"],
        ProjectSegments: [".muse"],
        Note: "Hook script plus settings.json entries (beta host; verify the hook fires).");

    public static readonly IReadOnlyList<HarnessTarget> All =
        [OpenCode, Codex, ClaudeCode, Gemini, Copilot, Cursor, Muse];

    public static HarnessTarget? Find(string id) =>
        All.FirstOrDefault(target => string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where this host keeps the harness: the plugin directory for OpenCode,
    /// the <c>.codex</c>/<c>.claude</c> directory for Codex/Claude Code.
    /// </summary>
    public static string DefaultHarnessDir(HarnessTarget target, string? projectDirectory = null)
    {
        var segments = projectDirectory is null ? target.PersonalSegments : target.ProjectSegments;
        if (segments is null)
            throw new InvalidOperationException(
                $"{target.DisplayName} has no default harness folder. Pass one explicitly.");

        var root = projectDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(
                "Could not determine the current user's home directory. Pass a path explicitly.");

        return Path.Combine([root, .. segments]);
    }
}
