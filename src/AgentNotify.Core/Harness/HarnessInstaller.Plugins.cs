using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentNotify.Core.Harness;

public static partial class HarnessInstaller
{
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

}
