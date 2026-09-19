using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentNotify.Core.Harness;

public static partial class HarnessInstaller
{
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

}
