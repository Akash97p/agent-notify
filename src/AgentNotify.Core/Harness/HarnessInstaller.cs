using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(codexDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);
        var hooksPath = Path.Combine(baseDir, "hooks.json");

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        var wanted = new[]
        {
            ("PermissionRequest", HookCommand(scriptPath, "codex", "permission"), WithMatcher: true),
            ("Stop", HookCommand(scriptPath, "codex", "stop"), WithMatcher: false),
            ("SessionEnd", HookCommand(scriptPath, "codex", "session-end"), WithMatcher: false),
        };
        var hooks = MergeHookGroups(hooksPath, wanted, force, dryRun: true);
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

        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Codex at '{baseDir}'. Trust the project layer if Codex asks, then restart the session."
                : $"AgentNotify harness for Codex is already up to date at '{baseDir}'.");
    }

    // ---- Claude Code hooks (script + settings.json merge) ----

    public static HarnessInstallResult InstallClaudeHarness(
        string claudeDir,
        string scriptContent,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeDir);
        ArgumentNullException.ThrowIfNull(scriptContent);

        var baseDir = Path.GetFullPath(claudeDir);
        var scriptPath = Path.Combine(baseDir, "agentnotify", HarnessCatalog.HookScriptFileName);
        var settingsPath = Path.Combine(baseDir, "settings.json");

        var script = PlanOwnedFile(scriptPath, scriptContent, force);
        if (!script.Success)
            return Fail(baseDir, script.Message);

        var wanted = new[]
        {
            ("Notification", HookCommand(scriptPath, "claude", "notification")),
            ("Stop", HookCommand(scriptPath, "claude", "stop")),
        };
        var hooks = MergeSettingsHooks(settingsPath, wanted, force, dryRun: true);
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

        return new HarnessInstallResult(true, changed, baseDir,
            changed
                ? $"Installed the AgentNotify harness for Claude Code at '{baseDir}'. Restart the session to load it."
                : $"AgentNotify harness for Claude Code is already up to date at '{baseDir}'.");
    }

    /// <summary>The shell command recorded in hooks JSON for one event.</summary>
    public static string HookCommand(string scriptPath, string agentId, string eventName)
    {
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        return $"{python} \"{scriptPath}\" {agentId} {eventName}";
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
        (string Event, string Command, bool WithMatcher)[] wanted,
        bool force,
        bool dryRun)
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
        foreach (var (eventName, command, withMatcher) in wanted)
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
                array.Add(BuildGroup(command, withMatcher));
                changed = true;
            }
        }

        if (!changed)
            return new JsonPlan(true, false, "up to date", null);
        return new JsonPlan(true, true, "merge", root.ToJsonString(JsonWriteOptions) + Environment.NewLine);
    }

    private static JsonPlan MergeSettingsHooks(
        string settingsPath,
        (string Event, string Command)[] wanted,
        bool force,
        bool dryRun)
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
        foreach (var (eventName, command) in wanted)
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
                var group = new JsonObject
                {
                    ["matcher"] = "",
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = command,
                        },
                    },
                };
                array.Add(group);
                changed = true;
            }
        }

        if (!changed)
            return new JsonPlan(true, false, "up to date", null);
        return new JsonPlan(true, true, "merge", root.ToJsonString(JsonWriteOptions) + Environment.NewLine);
    }

    private static JsonObject BuildGroup(string command, bool withMatcher)
    {
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = 10,
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

    private static (string Agent, string Event) Signature(string command)
    {
        // Commands look like: python3 "<script>" <agent> <event>
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return (parts[^2], parts[^1].Trim('"'));
        return (string.Empty, command);
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
