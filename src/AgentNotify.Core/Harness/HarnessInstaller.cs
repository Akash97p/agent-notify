using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentNotify.Core.Harness;

/// <summary>What a harness install did, or would have done.</summary>
public sealed record HarnessInstallResult(
    bool Success,
    bool Changed,
    string HarnessDir,
    string Message);

/// <summary>
/// Writes the AgentNotify host harnesses: the OpenCode event plugin and the
/// Codex/Claude Code hook script plus their JSON hook registrations.
/// </summary>
/// <remarks>
/// Host hook files belong to the user and often contain other entries, so the
/// JSON merge preserves everything it does not own and only appends the
/// AgentNotify entries when they are missing. A changed script file is never
/// overwritten unless told to, for the same reason skills are protected.
/// <para>
/// Every hook is notify-only: the scripts exit 0 and return no host decision,
/// so a notification failure can never block or redirect the session.
/// </para>
/// </remarks>
public static partial class HarnessInstaller
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        // Hook files are read by humans as well as hosts: keep quotes and
        // paths literal instead of \u0022-escaping them.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- OpenCode plugin (single file) ----

    public static HarnessInstallResult InstallOpenCodePlugin(
        string pluginDir,
        string content,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDir);
        ArgumentNullException.ThrowIfNull(content);

        var destination = Path.Combine(Path.GetFullPath(pluginDir), HarnessCatalog.PluginFileName);
        return WriteOwnedFile("OpenCode", destination, content, force, dryRun,
            installed => $"Installed the AgentNotify harness for OpenCode at '{installed}'. Restart OpenCode to load it.");
    }

    // ---- Codex hooks (script + hooks.json merge) ----

    public static HarnessInstallResult InstallCodexHarness(
        string codexDir,
        string scriptContent,
        bool force,
        bool dryRun,
        bool askMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(codexDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);
        var hooksPath = Path.Combine(baseDir, "hooks.json");

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        // Ask mode replaces the notify-only PermissionRequest hook with a blocking
        // ask hook (verified Codex decision schema); anything else keeps notifying.
        // Each direction also removes the other mode's entries so switching modes
        // never leaves duplicate hooks on the same event.
        var permission = askMode
            ? HookCommand(scriptPath, "codex", "ask-permission") + " --timeout 590"
            : HookCommand(scriptPath, "codex", "permission");
        var wanted = new[]
        {
            ("PermissionRequest", permission, true, askMode ? 600 : 10),
            ("Stop", HookCommand(scriptPath, "codex", "stop"), false, 10),
            ("SessionEnd", HookCommand(scriptPath, "codex", "session-end"), false, 10),
        };
        var remove = askMode
            ? new[] { ("codex", "permission") }
            : new[] { ("codex", "ask-permission") };
        var hooks = MergeHookGroups(hooksPath, wanted, force, dryRun: true, remove);
        if (!hooks.Success)
            return Fail(baseDir, hooks.Message);

        var changed = script.Changed || hooks.Changed;
        if (dryRun)
            return new HarnessInstallResult(true, false, baseDir,
                $"Would install the AgentNotify harness for Codex at '{baseDir}'.");

        if (script.Changed)
            WriteOwnedFile("Codex", scriptPath, scriptContent, force, dryRun: false, _ => string.Empty);
        if (hooks.Changed)
            WriteJsonFile(hooksPath, hooks.Payload!);

        var mode = askMode
            ? " Ask mode is on: approval prompts wait up to ~10 minutes for a broker answer, then fall back to the local prompt."
            : "";
        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Codex at '{baseDir}'. Trust the project layer if Codex asks, then restart the session.{mode}"
                : $"AgentNotify harness for Codex is already up to date at '{baseDir}'.");
    }

    // ---- Claude Code hooks (script + settings.json merge) ----

}
