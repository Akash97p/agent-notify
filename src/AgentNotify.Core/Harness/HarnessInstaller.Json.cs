using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentNotify.Core.Harness;

public static partial class HarnessInstaller
{
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
