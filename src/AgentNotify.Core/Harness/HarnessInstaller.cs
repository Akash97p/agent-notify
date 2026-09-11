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
public static class HarnessInstaller
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

    public static HarnessInstallResult InstallClaudeHarness(
        string claudeDir,
        string scriptContent,
        bool force,
        bool dryRun,
        bool askMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(claudeDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);
        var settingsPath = Path.Combine(baseDir, "settings.json");

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        var wanted = askMode
            ? new[]
            {
                ("PermissionRequest", HookCommand(scriptPath, "claude", "ask-permission") + " --timeout 290", (int?)300),
                ("Stop", HookCommand(scriptPath, "claude", "stop"), (int?)null),
            }
            : new[]
            {
                ("Notification", HookCommand(scriptPath, "claude", "notification"), (int?)null),
                ("Stop", HookCommand(scriptPath, "claude", "stop"), (int?)null),
            };
        var remove = askMode
            ? new[] { ("claude", "notification") }
            : new[] { ("claude", "ask-permission") };
        var hooks = MergeSettingsHooks(settingsPath, wanted, force, dryRun: true, remove);
        if (!hooks.Success)
            return Fail(baseDir, hooks.Message);

        var changed = script.Changed || hooks.Changed;
        if (dryRun)
            return new HarnessInstallResult(true, false, baseDir,
                $"Would install the AgentNotify harness for Claude Code at '{baseDir}'.");

        if (script.Changed)
            WriteOwnedFile("Claude Code", scriptPath, scriptContent, force, dryRun: false, _ => string.Empty);
        if (hooks.Changed)
            WriteJsonFile(settingsPath, hooks.Payload!);

        var mode = askMode
            ? " Ask mode is on: permission prompts wait up to ~5 minutes for a broker answer, then fall back to the local prompt."
            : "";
        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Claude Code at '{baseDir}'. Restart the session to load it.{mode}"
                : $"AgentNotify harness for Claude Code is already up to date at '{baseDir}'.");
    }

    /// <summary>The shell command recorded in hooks JSON for one event.</summary>
    public static string HookCommand(string scriptPath, string agentId, string eventName)
    {
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        return $"{python} \"{scriptPath}\" {agentId} {eventName}";
    }

    // ---- Gemini CLI hooks (script + settings.json merge) ----

    public static HarnessInstallResult InstallGeminiHarness(
        string geminiDir,
        string scriptContent,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(geminiDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(geminiDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);
        var settingsPath = Path.Combine(baseDir, "settings.json");

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        // Gemini hook events are advisory: Notification observes permission prompts
        // without deciding them; AfterAgent/SessionEnd observe completion.
        var wanted = new[]
        {
            ("Notification", HookCommand(scriptPath, "gemini", "notification"), (int?)null),
            ("AfterAgent", HookCommand(scriptPath, "gemini", "after-agent"), (int?)null),
            ("SessionEnd", HookCommand(scriptPath, "gemini", "session-end"), (int?)null),
        };
        var hooks = MergeSettingsHooks(settingsPath, wanted, force, dryRun: true);
        if (!hooks.Success)
            return Fail(baseDir, hooks.Message);

        var changed = script.Changed || hooks.Changed;
        if (dryRun)
            return new HarnessInstallResult(true, false, baseDir,
                $"Would install the AgentNotify harness for Gemini CLI at '{baseDir}'.");

        if (script.Changed)
            WriteOwnedFile("Gemini CLI", scriptPath, scriptContent, force, dryRun: false, _ => string.Empty);
        if (hooks.Changed)
            WriteJsonFile(settingsPath, hooks.Payload!);

        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Gemini CLI at '{baseDir}'. Restart the session to load it."
                : $"AgentNotify harness for Gemini CLI is already up to date at '{baseDir}'.");
    }

    // ---- Copilot CLI hooks (script + owned hooks-directory file) ----

    public static HarnessInstallResult InstallCopilotHarness(
        string copilotDir,
        string scriptContent,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copilotDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(copilotDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);
        var hooksPath = Path.Combine(baseDir, "hooks", "agentnotify.json");

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        var payload = BuildCopilotHooksFile(scriptPath);
        var hooks = PlanOwnedFile(hooksPath, payload, force);
        if (!hooks.Success)
            return Fail(baseDir, hooks.Message);

        var changed = script.Changed || hooks.Changed;
        if (dryRun)
            return new HarnessInstallResult(true, false, baseDir,
                $"Would install the AgentNotify harness for Copilot CLI at '{baseDir}'.");

        if (script.Changed)
            WriteOwnedFile("Copilot CLI", scriptPath, scriptContent, force, dryRun: false, _ => string.Empty);
        if (hooks.Changed)
            WriteOwnedFile("Copilot CLI", hooksPath, payload, force, dryRun: false, _ => string.Empty);

        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Copilot CLI at '{baseDir}'. Restart the session to load it."
                : $"AgentNotify harness for Copilot CLI is already up to date at '{baseDir}'.");
    }

    private static string BuildCopilotHooksFile(string scriptPath)
    {
        // Copilot loads every *.json file in its hooks directory, so this file is
        // AgentNotify-owned by name: keep custom hooks in a separate file.
        // The notification event is fire-and-forget and never blocks the session.
        var root = new JsonObject { ["version"] = 1 };
        var hooks = new JsonObject();
        foreach (var (evt, agentEvent) in new[]
                 {
                     ("notification", "notification"),
                     ("agentStop", "agent-stop"),
                     ("sessionEnd", "session-end"),
                     ("errorOccurred", "error-occurred"),
                 })
        {
            hooks[evt] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["bash"] = $"python3 \"{scriptPath}\" copilot {agentEvent}",
                    ["powershell"] = $"python \"{scriptPath}\" copilot {agentEvent}",
                    ["timeoutSec"] = 10
                }
            };
        }
        root["hooks"] = hooks;
        return root.ToJsonString(JsonWriteOptions) + Environment.NewLine;
    }

    // ---- Cursor hooks (script + hooks.json merge) ----

    public static HarnessInstallResult InstallCursorHarness(
        string cursorDir,
        string scriptContent,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(cursorDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);
        var hooksPath = Path.Combine(baseDir, "hooks.json");

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        var wanted = new[]
        {
            ("stop", HookCommand(scriptPath, "cursor", "stop")),
            ("sessionEnd", HookCommand(scriptPath, "cursor", "session-end")),
        };
        var hooks = MergeCursorHooks(hooksPath, wanted, force);
        if (!hooks.Success)
            return Fail(baseDir, hooks.Message);

        var changed = script.Changed || hooks.Changed;
        if (dryRun)
            return new HarnessInstallResult(true, false, baseDir,
                $"Would install the AgentNotify harness for Cursor at '{baseDir}'.");

        if (script.Changed)
            WriteOwnedFile("Cursor", scriptPath, scriptContent, force, dryRun: false, _ => string.Empty);
        if (hooks.Changed)
            WriteJsonFile(hooksPath, hooks.Payload!);

        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Cursor at '{baseDir}'. Restart Cursor to load it."
                : $"AgentNotify harness for Cursor is already up to date at '{baseDir}'.");
    }

    private sealed record CursorPlan(bool Success, bool Changed, string Message, string? Payload);

    private static CursorPlan MergeCursorHooks(
        string hooksPath,
        (string Event, string Command)[] wanted,
        bool force)
    {
        JsonObject root;
        if (!File.Exists(hooksPath))
        {
            root = new JsonObject();
        }
        else
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(File.ReadAllText(hooksPath));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                if (!force)
                    return new CursorPlan(false, false,
                        $"Refusing to rewrite '{hooksPath}' because it is not valid JSON ({ex.GetType().Name}). Re-run with --force to replace it.", null);
                root = new JsonObject();
                goto Build;
            }
            if (node is not JsonObject)
            {
                if (!force)
                    return new CursorPlan(false, false,
                        $"Refusing to rewrite '{hooksPath}' because its top level is not a JSON object. Re-run with --force to replace it.", null);
                root = new JsonObject();
                goto Build;
            }
            root = (JsonNode.Parse(node.ToJsonString()) as JsonObject)!;
        }

    Build:
        var changed = !File.Exists(hooksPath);
        if (root["version"] is null)
        {
            root["version"] = 1;
            changed = true;
        }
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
            changed = true;
        }
        foreach (var (eventName, command) in wanted)
        {
            var array = hooks[eventName] as JsonArray;
            if (array is null)
            {
                array = new JsonArray();
                hooks[eventName] = array;
                changed = true;
            }
            if (!CursorGroupContainsCommand(array, command))
            {
                array.Add(new JsonObject { ["command"] = command });
                changed = true;
            }
        }

        if (!changed)
            return new CursorPlan(true, false, "up to date", null);
        return new CursorPlan(true, true, "merge", root.ToJsonString(JsonWriteOptions) + Environment.NewLine);
    }

    private static bool CursorGroupContainsCommand(JsonArray entries, string command)
    {
        var (agent, evt) = Signature(command);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject obj) continue;
            var existing = obj["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(existing)) continue;
            if (existing.Contains("agentnotify_hook.py", StringComparison.Ordinal) &&
                existing.Contains($"{agent} {evt}", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ---- Muse Code hooks (script + settings merge, or project hooks file) ----

    public static HarnessInstallResult InstallMuseHarness(
        string museDir,
        string scriptContent,
        bool force,
        bool dryRun,
        bool projectScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(museDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(museDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        string hooksPath;
        JsonPlan hooks;
        if (projectScope)
        {
            // Muse project hooks live in .muse/hooks.json. The beta host follows the
            // Claude Code hook schema; the installer message tells the owner how to confirm.
            hooksPath = Path.Combine(baseDir, "hooks.json");
            var wanted = new[]
            {
                ("PermissionRequest", HookCommand(scriptPath, "muse", "permission-request"), true, 10),
                ("Stop", HookCommand(scriptPath, "muse", "stop"), false, 10),
            };
            hooks = MergeHookGroups(hooksPath, wanted, force, dryRun: true);
        }
        else
        {
            hooksPath = Path.Combine(baseDir, "settings.json");
            var wanted = new[]
            {
                ("PermissionRequest", HookCommand(scriptPath, "muse", "permission-request"), (int?)null),
                ("Stop", HookCommand(scriptPath, "muse", "stop"), (int?)null),
            };
            hooks = MergeMuseSettingsHooks(hooksPath, wanted, force);
        }
        if (!hooks.Success)
            return Fail(baseDir, hooks.Message);

        var changed = script.Changed || hooks.Changed;
        if (dryRun)
            return new HarnessInstallResult(true, false, baseDir,
                $"Would install the AgentNotify harness for Muse Code at '{baseDir}'.");

        if (script.Changed)
            WriteOwnedFile("Muse Code", scriptPath, scriptContent, force, dryRun: false, _ => string.Empty);
        if (hooks.Changed)
            WriteJsonFile(hooksPath, hooks.Payload!);

        var verify = projectScope
            ? " Start one session and confirm Muse reports no hooks warning; the project format is beta and unconfirmed."
            : " Restart the session to load it.";
        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Muse Code at '{baseDir}'.{verify}"
                : $"AgentNotify harness for Muse Code is already up to date at '{baseDir}'.");
    }

    // ---- Kilo Code plugin (retargeted OpenCode plugin, single file) ----

    public static HarnessInstallResult InstallKiloPlugin(
        string pluginDir,
        string content,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDir);
        ArgumentNullException.ThrowIfNull(content);

        var destination = Path.Combine(Path.GetFullPath(pluginDir), HarnessCatalog.PluginFileName);
        return WriteOwnedFile("Kilo Code", destination, content, force, dryRun,
            installed => $"Installed the AgentNotify harness for Kilo Code at '{installed}'. Restart Kilo to load it.");
    }

    // ---- OpenClaw bridge (watch script, no host config to merge) ----

    public static HarnessInstallResult InstallOpenClawBridge(
        string openClawDir,
        string scriptContent,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openClawDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(openClawDir);
        var destination = Path.Combine(baseDir, "agentnotify", "agentnotify_openclaw.py");
        var result = WriteOwnedFile("OpenClaw", destination, scriptContent, force, dryRun,
            installed => $"Installed the AgentNotify bridge for OpenClaw at '{installed}'. Run it with: python3 \"{installed}\" watch");
        if (result.Success && result.Changed && !dryRun)
            TryMakeExecutable(destination);
        return result;
    }

    // ---- Hermes plugin (plugin directory with manifest + module) ----

    public static HarnessInstallResult InstallHermesPlugin(
        string pluginsDir,
        string pluginYaml,
        string pluginInit,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDir);
        ArgumentNullException.ThrowIfNull(pluginYaml);
        ArgumentNullException.ThrowIfNull(pluginInit);

        var pluginDir = Path.Combine(Path.GetFullPath(pluginsDir), "agentnotify");
        var yamlPath = Path.Combine(pluginDir, "plugin.yaml");
        var initPath = Path.Combine(pluginDir, "__init__.py");

        var yaml = PlanOwnedFile(yamlPath, pluginYaml, force);
        if (!yaml.Success)
            return Fail(pluginDir, yaml.Message);
        var init = PlanOwnedFile(initPath, pluginInit, force);
        if (!init.Success)
            return Fail(pluginDir, init.Message);

        var changed = yaml.Changed || init.Changed;
        if (dryRun)
            return new HarnessInstallResult(true, false, pluginDir,
                $"Would install the AgentNotify harness for Hermes Agent at '{pluginDir}'.");

        if (yaml.Changed)
            WriteOwnedFile("Hermes Agent", yamlPath, pluginYaml, force, dryRun: false, _ => string.Empty);
        if (init.Changed)
            WriteOwnedFile("Hermes Agent", initPath, pluginInit, force, dryRun: false, _ => string.Empty);

        const string configSnippet =
            "Then enable it in ~/.hermes/config.yaml (two separate consent steps):\n" +
            "  plugins:\n    enabled: [agentnotify]\n" +
            "  security:\n    approval:\n      transport: agentnotify\n" +
            "      transport_fallback: deny   # or: builtin (ordinary prompt on failure)";
        return new HarnessInstallResult(true, changed, pluginDir,
            changed
                ? $"Installed the AgentNotify harness for Hermes Agent at '{pluginDir}'. {configSnippet}"
                : $"AgentNotify harness for Hermes Agent is already up to date at '{pluginDir}'.");
    }

    // ---- Pi extension (single .ts file in the extensions directory) ----

    public static HarnessInstallResult InstallPiExtension(
        string extensionsDir,
        string content,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsDir);
        ArgumentNullException.ThrowIfNull(content);

        var destination = Path.Combine(Path.GetFullPath(extensionsDir), "agentnotify.ts");
        return WriteOwnedFile("Pi", destination, content, force, dryRun,
            installed => $"Installed the AgentNotify harness for Pi at '{installed}'. Run /reload in Pi to load it.");
    }

    private static void TryMakeExecutable(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: the script runs fine via an explicit interpreter either way.
        }
    }

    private static JsonPlan MergeMuseSettingsHooks(
        string settingsPath,
        (string Event, string Command, int? TimeoutSec)[] wanted,
        bool force)
    {
        // Muse requires schema_version: 1 — a settings file without it fails every
        // command at startup, so a fresh file is seeded with it and an existing file
        // keeps whatever value it already has.
        if (!File.Exists(settingsPath))
        {
            var fresh = new JsonObject { ["schema_version"] = 1, ["hooks"] = new JsonObject() };
            var plan = MergeSettingsHooksInto(fresh, wanted);
            return new JsonPlan(true, true, "merge", plan.ToJsonString(JsonWriteOptions) + Environment.NewLine);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(settingsPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            if (!force)
                return new JsonPlan(false, false,
                    $"Refusing to rewrite '{settingsPath}' because it is not valid JSON ({ex.GetType().Name}). Re-run with --force to replace it.", null);
            var fresh = new JsonObject { ["schema_version"] = 1, ["hooks"] = new JsonObject() };
            var rebuilt = MergeSettingsHooksInto(fresh, wanted);
            return new JsonPlan(true, true, "merge", rebuilt.ToJsonString(JsonWriteOptions) + Environment.NewLine);
        }
        if (node is not JsonObject)
        {
            if (!force)
                return new JsonPlan(false, false,
                    $"Refusing to rewrite '{settingsPath}' because its top level is not a JSON object. Re-run with --force to replace it.", null);
            var fresh = new JsonObject { ["schema_version"] = 1, ["hooks"] = new JsonObject() };
            var rebuilt = MergeSettingsHooksInto(fresh, wanted);
            return new JsonPlan(true, true, "merge", rebuilt.ToJsonString(JsonWriteOptions) + Environment.NewLine);
        }

        var root = (JsonNode.Parse(node.ToJsonString()) as JsonObject)!;
        var before = root.ToJsonString();
        var merged = MergeSettingsHooksInto(root, wanted);
        if (merged.ToJsonString() == before && File.Exists(settingsPath))
            return new JsonPlan(true, false, "up to date", null);
        return new JsonPlan(true, true, "merge", merged.ToJsonString(JsonWriteOptions) + Environment.NewLine);
    }

    private static JsonObject MergeSettingsHooksInto(JsonObject root, (string Event, string Command, int? TimeoutSec)[] wanted)
    {
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var (eventName, command, timeoutSec) in wanted)
        {
            var array = hooks[eventName] as JsonArray;
            if (array is null)
            {
                array = new JsonArray();
                hooks[eventName] = array;
            }
            if (!GroupContainsCommand(array, command))
                array.Add(BuildSettingsGroup(command, timeoutSec));
        }
        return root;
    }

    // ---- internals ----

    private sealed record FilePlan(bool Success, bool Changed, string Message);
    private sealed record JsonPlan(bool Success, bool Changed, string Message, string? Payload);

    private static HarnessInstallResult Fail(string dir, string message) =>
        new(false, false, dir, message);

    private static HarnessInstallResult WriteOwnedFile(
        string displayName,
        string destination,
        string content,
        bool force,
        bool dryRun,
        Func<string, string> installedMessage)
    {
        if (File.Exists(destination))
        {
            if (SameContent(destination, content))
                return new HarnessInstallResult(true, false, Path.GetDirectoryName(destination) ?? destination,
                    $"AgentNotify harness for {displayName} is already up to date at '{destination}'.");
            if (!force)
                return new HarnessInstallResult(false, false, Path.GetDirectoryName(destination) ?? destination,
                    $"Refusing to overwrite the existing file '{destination}'. Re-run with --force after reviewing it.");
        }

        if (dryRun)
            return new HarnessInstallResult(true, false, Path.GetDirectoryName(destination) ?? destination,
                $"Would install the AgentNotify harness for {displayName} at '{destination}'.");

        WriteAtomically(destination, content);
        return new HarnessInstallResult(true, true, Path.GetDirectoryName(destination) ?? destination,
            installedMessage(destination));
    }

    private static FilePlan PlanOwnedFile(string destination, string content, bool force)
    {
        if (File.Exists(destination))
        {
            if (SameContent(destination, content))
                return new FilePlan(true, false, "up to date");
            if (!force)
                return new FilePlan(false, false,
                    $"Refusing to overwrite the existing file '{destination}'. Re-run with --force after reviewing it.");
            return new FilePlan(true, true, "would replace");
        }
        return new FilePlan(true, true, "would create");
    }

    private static JsonPlan MergeHookGroups(
        string hooksPath,
        (string Event, string Command, bool WithMatcher, int TimeoutSec)[] wanted,
        bool force,
        bool dryRun,
        (string Agent, string Event)[]? remove = null)
    {
        JsonObject root;
        if (!File.Exists(hooksPath))
        {
            root = new JsonObject();
        }
        else
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(File.ReadAllText(hooksPath));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                if (!force)
                    return new JsonPlan(false, false,
                        $"Refusing to rewrite '{hooksPath}' because it is not valid JSON ({ex.GetType().Name}). Re-run with --force to replace it.", null);
                root = new JsonObject();
                goto Build;
            }
            if (node is not JsonObject obj)
            {
                if (!force)
                    return new JsonPlan(false, false,
                        $"Refusing to rewrite '{hooksPath}' because its top level is not a JSON object. Re-run with --force to replace it.", null);
                root = new JsonObject();
                goto Build;
            }
            // Clone via re-parse so later writes never mutate a live document.
            root = (JsonNode.Parse(node.ToJsonString()) as JsonObject)!;
        }

    Build:
        var changed = !File.Exists(hooksPath);
        if (remove is not null)
        {
            foreach (var (eventName, array) in root
                         .Where(kvp => kvp.Value is JsonArray)
                         .Select(kvp => (kvp.Key, (JsonArray)kvp.Value!))
                         .ToArray())
            {
                var before = array.Count;
                foreach (var signature in remove)
                    RemoveSignatureMatches(array, signature.Agent, signature.Event);
                if (array.Count != before)
                    changed = true;
            }
        }
        foreach (var (eventName, command, withMatcher, timeoutSec) in wanted)
        {
            var array = root[eventName] as JsonArray;
            if (array is null)
            {
                array = new JsonArray();
                root[eventName] = array;
                changed = true;
            }
            if (!GroupContainsCommand(array, command))
            {
                array.Add(BuildGroup(command, withMatcher, timeoutSec));
                changed = true;
            }
        }

        if (!changed)
            return new JsonPlan(true, false, "up to date", null);
        return new JsonPlan(true, true, "merge", root.ToJsonString(JsonWriteOptions) + Environment.NewLine);
    }

    private static JsonPlan MergeSettingsHooks(
        string settingsPath,
        (string Event, string Command, int? TimeoutSec)[] wanted,
        bool force,
        bool dryRun,
        (string Agent, string Event)[]? remove = null)
    {
        JsonObject root;
        if (!File.Exists(settingsPath))
        {
            root = new JsonObject();
        }
        else
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(File.ReadAllText(settingsPath));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                if (!force)
                    return new JsonPlan(false, false,
                        $"Refusing to rewrite '{settingsPath}' because it is not valid JSON ({ex.GetType().Name}). Re-run with --force to replace it.", null);
                root = new JsonObject();
                goto Build;
            }
            if (node is not JsonObject obj)
            {
                if (!force)
                    return new JsonPlan(false, false,
                        $"Refusing to rewrite '{settingsPath}' because its top level is not a JSON object. Re-run with --force to replace it.", null);
                root = new JsonObject();
                goto Build;
            }
            root = (JsonNode.Parse(node.ToJsonString()) as JsonObject)!;
        }

    Build:
        var changed = !File.Exists(settingsPath);
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
            changed = true;
        }
        if (remove is not null)
        {
            foreach (var array in hooks
                         .Where(kvp => kvp.Value is JsonArray)
                         .Select(kvp => (JsonArray)kvp.Value!)
                         .ToArray())
            {
                var before = array.Count;
                foreach (var signature in remove)
                    RemoveSignatureMatches(array, signature.Agent, signature.Event);
                if (array.Count != before)
                    changed = true;
            }
        }
        foreach (var (eventName, command, timeoutSec) in wanted)
        {
            var array = hooks[eventName] as JsonArray;
            if (array is null)
            {
                array = new JsonArray();
                hooks[eventName] = array;
                changed = true;
            }
            if (!GroupContainsCommand(array, command))
            {
                array.Add(BuildSettingsGroup(command, timeoutSec));
                changed = true;
            }
        }

        if (!changed)
            return new JsonPlan(true, false, "up to date", null);
        return new JsonPlan(true, true, "merge", root.ToJsonString(JsonWriteOptions) + Environment.NewLine);
    }

    private static JsonObject BuildSettingsGroup(string command, int? timeoutSec)
    {
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
        };
        if (timeoutSec is { } seconds)
            hook["timeout"] = seconds;
        return new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = new JsonArray { hook },
        };
    }

    private static JsonObject BuildGroup(string command, bool withMatcher, int timeoutSec = 10)
    {
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = timeoutSec,
        };
        var group = new JsonObject();
        if (withMatcher)
            group["matcher"] = "";
        group["hooks"] = new JsonArray { hook };
        return group;
    }

    private static bool GroupContainsCommand(JsonArray groups, string command)
    {
        var (agent, evt) = Signature(command);
        foreach (var group in groups)
        {
            if (group is not JsonObject obj) continue;
            if (obj["hooks"] is not JsonArray hooks) continue;
            foreach (var hook in hooks)
            {
                if (hook is not JsonObject hookObj) continue;
                var existing = hookObj["command"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(existing)) continue;
                if (existing.Contains("agentnotify_hook.py", StringComparison.Ordinal) &&
                    existing.Contains($"{agent} {evt}", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static readonly Regex HookSignaturePattern =
        new(@"agentnotify_hook\.py""?\s+(?<agent>\S+)\s+(?<evt>\S+)", RegexOptions.Compiled);

    private static (string Agent, string Event) Signature(string command)
    {
        // Commands look like: python3 "<script>" <agent> <event> [--flags ...].
        // Anchor on the script name so trailing flags and quoted paths cannot
        // shift the match; unknown shapes yield an empty signature that matches nothing.
        var match = HookSignaturePattern.Match(command);
        if (!match.Success)
            return (string.Empty, command);
        return (match.Groups["agent"].Value, match.Groups["evt"].Value.Trim('"'));
    }

    private static void RemoveSignatureMatches(JsonArray groups, string agent, string evt)
    {
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i] is not JsonObject obj) continue;
            if (obj["hooks"] is not JsonArray hooks) continue;
            var owned = hooks.OfType<JsonObject>()
                .Select(hook => hook["command"]?.GetValue<string>())
                .Any(existing => !string.IsNullOrWhiteSpace(existing) &&
                    existing.Contains("agentnotify_hook.py", StringComparison.Ordinal) &&
                    Signature(existing) == (agent, evt));
            if (owned)
                groups.RemoveAt(i);
        }
    }

    private static bool SameContent(string path, string content)
    {
        try
        {
            return string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WriteJsonFile(string destination, string payload)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not determine the destination directory for '{destination}'.");
        Directory.CreateDirectory(directory);
        WriteAtomically(destination, payload);
    }

    private static void WriteAtomically(string destination, string content)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not determine the destination directory for '{destination}'.");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
