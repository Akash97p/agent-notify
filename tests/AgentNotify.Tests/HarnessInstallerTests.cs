using System.Text.Json;
using AgentNotify.Core.Harness;

namespace AgentNotify.Tests;

/// <summary>
/// Installing the auto-notify host harnesses.
/// </summary>
/// <remarks>
/// Harness files live inside folders owned by other products, so the behavior
/// worth pinning down is what the installer refuses to do: overwrite an edited
/// plugin/script, duplicate hook entries on reinstall, or clobber unrelated
/// hook configuration.
/// </remarks>
public sealed class HarnessInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"agentnotify-harness-{Guid.NewGuid():N}");

    private const string PluginContent = "// AgentNotify test plugin\n";
    private const string ScriptContent = "# AgentNotify test hook\n";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- OpenCode plugin ----

    [Fact]
    public void OpenCodePlugin_WritesThePluginFile()
    {
        var pluginDir = Path.Combine(_root, "plugins");
        var result = HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(PluginContent, File.ReadAllText(Path.Combine(pluginDir, "agentnotify.js")));
    }

    [Fact]
    public void OpenCodePlugin_IsIdempotent()
    {
        var pluginDir = Path.Combine(_root, "plugins");
        HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: false, dryRun: false);
        var again = HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: false, dryRun: false);

        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    [Fact]
    public void OpenCodePlugin_RefusesToOverwriteAnEditedPlugin()
    {
        var pluginDir = Path.Combine(_root, "plugins");
        HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: false, dryRun: false);
        File.WriteAllText(Path.Combine(pluginDir, "agentnotify.js"), "// our team's own tweak\n");

        var result = HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: false, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("our team's own tweak", File.ReadAllText(Path.Combine(pluginDir, "agentnotify.js")));
    }

    [Fact]
    public void OpenCodePlugin_ReplacesAnEditedPluginWhenForced()
    {
        var pluginDir = Path.Combine(_root, "plugins");
        HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: false, dryRun: false);
        File.WriteAllText(Path.Combine(pluginDir, "agentnotify.js"), "// edited\n");

        var result = HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: true, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(PluginContent, File.ReadAllText(Path.Combine(pluginDir, "agentnotify.js")));
    }

    [Fact]
    public void OpenCodePlugin_DryRunWritesNothing()
    {
        var pluginDir = Path.Combine(_root, "plugins");
        var result = HarnessInstaller.InstallOpenCodePlugin(pluginDir, PluginContent, force: false, dryRun: true);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(pluginDir));
    }

    // ---- Codex harness ----

    [Fact]
    public void CodexHarness_WritesScriptAndAllThreeHookEvents()
    {
        var codexDir = Path.Combine(_root, ".codex");
        var result = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(ScriptContent, File.ReadAllText(Path.Combine(codexDir, "agentnotify", "agentnotify_hook.py")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(codexDir, "hooks.json")));
        foreach (var evt in new[] { "PermissionRequest", "Stop", "SessionEnd" })
        {
            Assert.True(doc.RootElement.TryGetProperty(evt, out var array));
            Assert.Contains(array.EnumerateArray(), group =>
                group.ToString().Contains("agentnotify_hook.py", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CodexHarness_IsIdempotent()
    {
        var codexDir = Path.Combine(_root, ".codex");
        HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false);
        var again = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false);

        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    [Fact]
    public void CodexHarness_PreservesUnrelatedHooks()
    {
        var codexDir = Path.Combine(_root, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Combine(codexDir, "hooks.json"),
            """{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"echo hi"}]}]}""");

        var result = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(codexDir, "hooks.json")));
        Assert.True(doc.RootElement.TryGetProperty("PreToolUse", out _));
        Assert.True(doc.RootElement.TryGetProperty("PermissionRequest", out _));
    }

    [Fact]
    public void CodexHarness_RefusesInvalidJsonUnlessForced()
    {
        var codexDir = Path.Combine(_root, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Combine(codexDir, "hooks.json"), "{ not json");

        var refused = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false);
        Assert.False(refused.Success);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(codexDir, "hooks.json")));

        var forced = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: true, dryRun: false);
        Assert.True(forced.Success);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(codexDir, "hooks.json")));
        Assert.True(doc.RootElement.TryGetProperty("Stop", out _));
    }

    [Fact]
    public void CodexHarness_DryRunWritesNothing()
    {
        var codexDir = Path.Combine(_root, ".codex");
        var result = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: true);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(codexDir));
    }

    // ---- Claude harness ----

    [Fact]
    public void ClaudeHarness_WritesScriptAndBothHookEvents()
    {
        var claudeDir = Path.Combine(_root, ".claude");
        var result = HarnessInstaller.InstallClaudeHarness(claudeDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(ScriptContent, File.ReadAllText(Path.Combine(claudeDir, "agentnotify", "agentnotify_hook.py")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(claudeDir, "settings.json")));
        var hooks = doc.RootElement.GetProperty("hooks");
        foreach (var evt in new[] { "Notification", "Stop" })
        {
            Assert.True(hooks.TryGetProperty(evt, out var array));
            Assert.Contains(array.EnumerateArray(), group =>
                group.ToString().Contains("agentnotify_hook.py", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ClaudeHarness_PreservesOtherSettingsKeys()
    {
        var claudeDir = Path.Combine(_root, ".claude");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, "settings.json"),
            """{"model":"sonnet","hooks":{"Stop":[]}}""");

        var result = HarnessInstaller.InstallClaudeHarness(claudeDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(claudeDir, "settings.json")));
        Assert.Equal("sonnet", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("hooks").TryGetProperty("Notification", out _));
    }

    [Fact]
    public void ClaudeHarness_IsIdempotent()
    {
        var claudeDir = Path.Combine(_root, ".claude");
        HarnessInstaller.InstallClaudeHarness(claudeDir, ScriptContent, force: false, dryRun: false);
        var again = HarnessInstaller.InstallClaudeHarness(claudeDir, ScriptContent, force: false, dryRun: false);

        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    // ---- Gemini harness ----

    [Fact]
    public void GeminiHarness_WritesScriptAndAllThreeHookEvents()
    {
        var geminiDir = Path.Combine(_root, ".gemini");
        var result = HarnessInstaller.InstallGeminiHarness(geminiDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(ScriptContent, File.ReadAllText(Path.Combine(geminiDir, "agentnotify", "agentnotify_hook.py")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(geminiDir, "settings.json")));
        var hooks = doc.RootElement.GetProperty("hooks");
        foreach (var evt in new[] { "Notification", "AfterAgent", "SessionEnd" })
        {
            Assert.True(hooks.TryGetProperty(evt, out var array));
            Assert.Contains(array.EnumerateArray(), group =>
                group.ToString().Contains("gemini notification", StringComparison.Ordinal)
                || group.ToString().Contains("gemini after-agent", StringComparison.Ordinal)
                || group.ToString().Contains("gemini session-end", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void GeminiHarness_IsIdempotentAndPreservesOtherKeys()
    {
        var geminiDir = Path.Combine(_root, ".gemini");
        Directory.CreateDirectory(geminiDir);
        File.WriteAllText(Path.Combine(geminiDir, "settings.json"), """{"theme":"dark"}""");

        var result = HarnessInstaller.InstallGeminiHarness(geminiDir, ScriptContent, force: false, dryRun: false);
        Assert.True(result.Success);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(geminiDir, "settings.json")));
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());

        var again = HarnessInstaller.InstallGeminiHarness(geminiDir, ScriptContent, force: false, dryRun: false);
        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    // ---- Copilot harness ----

    [Fact]
    public void CopilotHarness_WritesScriptAndOwnedHooksFile()
    {
        var copilotDir = Path.Combine(_root, ".copilot");
        var result = HarnessInstaller.InstallCopilotHarness(copilotDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(ScriptContent, File.ReadAllText(Path.Combine(copilotDir, "agentnotify", "agentnotify_hook.py")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(copilotDir, "hooks", "agentnotify.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        var hooks = doc.RootElement.GetProperty("hooks");
        foreach (var evt in new[] { "notification", "agentStop", "sessionEnd", "errorOccurred" })
            Assert.True(hooks.TryGetProperty(evt, out _));
    }

    [Fact]
    public void CopilotHarness_IsIdempotentAndProtectsEdits()
    {
        var copilotDir = Path.Combine(_root, ".copilot");
        HarnessInstaller.InstallCopilotHarness(copilotDir, ScriptContent, force: false, dryRun: false);
        var again = HarnessInstaller.InstallCopilotHarness(copilotDir, ScriptContent, force: false, dryRun: false);
        Assert.True(again.Success);
        Assert.False(again.Changed);

        File.WriteAllText(Path.Combine(copilotDir, "hooks", "agentnotify.json"), """{"version":1,"hooks":{}}""");
        var refused = HarnessInstaller.InstallCopilotHarness(copilotDir, ScriptContent, force: false, dryRun: false);
        Assert.False(refused.Success);
        var forced = HarnessInstaller.InstallCopilotHarness(copilotDir, ScriptContent, force: true, dryRun: false);
        Assert.True(forced.Success);
        Assert.True(forced.Changed);
    }

    // ---- Cursor harness ----

    [Fact]
    public void CursorHarness_WritesScriptAndMergesVersionedHooks()
    {
        var cursorDir = Path.Combine(_root, ".cursor");
        var result = HarnessInstaller.InstallCursorHarness(cursorDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(ScriptContent, File.ReadAllText(Path.Combine(cursorDir, "agentnotify", "agentnotify_hook.py")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(cursorDir, "hooks.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        var hooks = doc.RootElement.GetProperty("hooks");
        foreach (var evt in new[] { "stop", "sessionEnd" })
        {
            Assert.True(hooks.TryGetProperty(evt, out var array));
            Assert.Contains(array.EnumerateArray(), entry =>
                entry.ToString().Contains("agentnotify_hook.py", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CursorHarness_PreservesUnrelatedHooksAndRefusesInvalidJson()
    {
        var cursorDir = Path.Combine(_root, ".cursor");
        Directory.CreateDirectory(cursorDir);
        File.WriteAllText(Path.Combine(cursorDir, "hooks.json"),
            """{"version":1,"hooks":{"afterFileEdit":[{"command":"./format.sh"}]}}""");

        var result = HarnessInstaller.InstallCursorHarness(cursorDir, ScriptContent, force: false, dryRun: false);
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(cursorDir, "hooks.json")));
        Assert.True(doc.RootElement.GetProperty("hooks").TryGetProperty("afterFileEdit", out _));
        Assert.True(doc.RootElement.GetProperty("hooks").TryGetProperty("stop", out _));

        File.WriteAllText(Path.Combine(cursorDir, "hooks.json"), "nope");
        Assert.False(HarnessInstaller.InstallCursorHarness(cursorDir, ScriptContent, force: false, dryRun: false).Success);
        Assert.True(HarnessInstaller.InstallCursorHarness(cursorDir, ScriptContent, force: true, dryRun: false).Success);
    }

    // ---- Muse harness ----

    [Fact]
    public void MuseHarness_UserScopeSeedsSchemaVersion()
    {
        var museDir = Path.Combine(_root, ".config", "muse");
        var result = HarnessInstaller.InstallMuseHarness(museDir, ScriptContent, force: false, dryRun: false, projectScope: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(ScriptContent, File.ReadAllText(Path.Combine(museDir, "agentnotify", "agentnotify_hook.py")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(museDir, "settings.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        var hooks = doc.RootElement.GetProperty("hooks");
        Assert.True(hooks.TryGetProperty("PermissionRequest", out _));
        Assert.True(hooks.TryGetProperty("Stop", out _));

        var again = HarnessInstaller.InstallMuseHarness(museDir, ScriptContent, force: false, dryRun: false, projectScope: false);
        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    [Fact]
    public void MuseHarness_ProjectScopeWritesHooksFile()
    {
        var museDir = Path.Combine(_root, ".muse");
        var result = HarnessInstaller.InstallMuseHarness(museDir, ScriptContent, force: false, dryRun: false, projectScope: true);

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(museDir, "hooks.json")));
        Assert.True(doc.RootElement.TryGetProperty("PermissionRequest", out _));
        Assert.True(doc.RootElement.TryGetProperty("Stop", out _));
    }

    [Fact]
    public void MuseHarness_PreservesExistingSchemaVersion()
    {
        var museDir = Path.Combine(_root, ".config", "muse");
        Directory.CreateDirectory(museDir);
        File.WriteAllText(Path.Combine(museDir, "settings.json"), """{"schema_version":1,"model":"x"}""");

        var result = HarnessInstaller.InstallMuseHarness(museDir, ScriptContent, force: false, dryRun: false, projectScope: false);
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(museDir, "settings.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("x", doc.RootElement.GetProperty("model").GetString());
    }

    // ---- Ask mode (Codex + Claude) ----

    [Fact]
    public void CodexAskMode_WritesBlockingHookAndRemovesNotifyHook()
    {
        var codexDir = Path.Combine(_root, ".codex");
        HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false);

        var ask = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false, askMode: true);
        Assert.True(ask.Success);
        Assert.True(ask.Changed);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(codexDir, "hooks.json")));
        var permission = doc.RootElement.GetProperty("PermissionRequest");
        Assert.DoesNotContain(permission.EnumerateArray(), group =>
            group.ToString().Contains("codex permission", StringComparison.Ordinal));
        var askGroup = Assert.Single(permission.EnumerateArray(), group =>
            group.ToString().Contains("codex ask-permission", StringComparison.Ordinal));
        Assert.Contains("--timeout 590", askGroup.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"timeout\": 600", askGroup.ToString(), StringComparison.Ordinal);

        var again = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false, askMode: true);
        Assert.True(again.Success);
        Assert.False(again.Changed);

        // Downgrading restores the notify hook and drops the ask hook.
        var notify = HarnessInstaller.InstallCodexHarness(codexDir, ScriptContent, force: false, dryRun: false);
        Assert.True(notify.Success);
        using var back = JsonDocument.Parse(File.ReadAllText(Path.Combine(codexDir, "hooks.json")));
        var restored = back.RootElement.GetProperty("PermissionRequest");
        Assert.Contains(restored.EnumerateArray(), group =>
            group.ToString().Contains("codex permission", StringComparison.Ordinal));
        Assert.DoesNotContain(restored.EnumerateArray(), group =>
            group.ToString().Contains("ask-permission", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeAskMode_WritesPermissionRequestAndRemovesNotification()
    {
        var claudeDir = Path.Combine(_root, ".claude");
        HarnessInstaller.InstallClaudeHarness(claudeDir, ScriptContent, force: false, dryRun: false);

        var ask = HarnessInstaller.InstallClaudeHarness(claudeDir, ScriptContent, force: false, dryRun: false, askMode: true);
        Assert.True(ask.Success);
        Assert.True(ask.Changed);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(claudeDir, "settings.json")));
        var hooks = doc.RootElement.GetProperty("hooks");
        Assert.True(hooks.TryGetProperty("PermissionRequest", out var permission));
        var askGroup = Assert.Single(permission.EnumerateArray(), group =>
            group.ToString().Contains("claude ask-permission", StringComparison.Ordinal));
        Assert.Contains("--timeout 290", askGroup.ToString(), StringComparison.Ordinal);
        // The notify-only Notification hook is gone; Stop survives the migration.
        Assert.True(hooks.TryGetProperty("Notification", out var notification)
            && !notification.EnumerateArray().Any(group =>
                group.ToString().Contains("agentnotify_hook.py", StringComparison.Ordinal)));
        Assert.True(hooks.TryGetProperty("Stop", out _));

        var again = HarnessInstaller.InstallClaudeHarness(claudeDir, ScriptContent, force: false, dryRun: false, askMode: true);
        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    [Fact]
    public async Task InstallHarness_AskRejectedForUnsupportedHosts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-harness-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(1, await AgentNotify.Cli.Program.Main(["install-harness", "gemini", "--path", root, "--ask"]));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallHarness_AskInstallsForCodex()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-harness-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, await AgentNotify.Cli.Program.Main(["install-harness", "codex", "--path", root, "--ask"]));
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "hooks.json")));
            Assert.Contains(doc.RootElement.GetProperty("PermissionRequest").EnumerateArray(), group =>
                group.ToString().Contains("ask-permission", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- Kilo harness ----

    [Fact]
    public void KiloHarness_WritesRetargetedPlugin()
    {
        var kiloDir = Path.Combine(_root, "plugin");
        var result = HarnessInstaller.InstallKiloPlugin(kiloDir, PluginContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(PluginContent, File.ReadAllText(Path.Combine(kiloDir, "agentnotify.js")));

        var again = HarnessInstaller.InstallKiloPlugin(kiloDir, PluginContent, force: false, dryRun: false);
        Assert.True(again.Success);
        Assert.False(again.Changed);

        File.WriteAllText(Path.Combine(kiloDir, "agentnotify.js"), "// edited");
        Assert.False(HarnessInstaller.InstallKiloPlugin(kiloDir, PluginContent, force: false, dryRun: false).Success);
        var forced = HarnessInstaller.InstallKiloPlugin(kiloDir, PluginContent, force: true, dryRun: false);
        Assert.True(forced.Success);
        Assert.True(forced.Changed);
    }

    // ---- OpenClaw bridge ----

    [Fact]
    public void OpenClawBridge_WritesWatcherScript()
    {
        var openClawDir = Path.Combine(_root, ".openclaw");
        var result = HarnessInstaller.InstallOpenClawBridge(openClawDir, ScriptContent, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(ScriptContent, File.ReadAllText(Path.Combine(openClawDir, "agentnotify", "agentnotify_openclaw.py")));

        var again = HarnessInstaller.InstallOpenClawBridge(openClawDir, ScriptContent, force: false, dryRun: false);
        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    // ---- Hermes plugin ----

    private const string HermesYaml = "name: agentnotify\n";
    private const string HermesInit = "def register(ctx):\n    pass\n";

    [Fact]
    public void HermesPlugin_WritesBothFiles()
    {
        var pluginsDir = Path.Combine(_root, "plugins");
        var result = HarnessInstaller.InstallHermesPlugin(pluginsDir, HermesYaml, HermesInit, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(HermesYaml, File.ReadAllText(Path.Combine(pluginsDir, "agentnotify", "plugin.yaml")));
        Assert.Equal(HermesInit, File.ReadAllText(Path.Combine(pluginsDir, "agentnotify", "__init__.py")));
        Assert.Contains("transport: agentnotify", result.Message);

        var again = HarnessInstaller.InstallHermesPlugin(pluginsDir, HermesYaml, HermesInit, force: false, dryRun: false);
        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    [Fact]
    public void HermesPlugin_ProtectsEditedFilesIndependently()
    {
        var pluginsDir = Path.Combine(_root, "plugins");
        HarnessInstaller.InstallHermesPlugin(pluginsDir, HermesYaml, HermesInit, force: false, dryRun: false);
        File.WriteAllText(Path.Combine(pluginsDir, "agentnotify", "__init__.py"), "# edited");

        var refused = HarnessInstaller.InstallHermesPlugin(pluginsDir, HermesYaml, HermesInit, force: false, dryRun: false);
        Assert.False(refused.Success);
        // The untouched manifest must not have been rewritten by the refusal path.
        Assert.Equal(HermesYaml, File.ReadAllText(Path.Combine(pluginsDir, "agentnotify", "plugin.yaml")));
    }

    // ---- Pi extension ----

    [Fact]
    public void PiExtension_WritesSingleFile()
    {
        var extensionsDir = Path.Combine(_root, "extensions");
        var result = HarnessInstaller.InstallPiExtension(extensionsDir, "// pi", force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal("// pi", File.ReadAllText(Path.Combine(extensionsDir, "agentnotify.ts")));

        var again = HarnessInstaller.InstallPiExtension(extensionsDir, "// pi", force: false, dryRun: false);
        Assert.True(again.Success);
        Assert.False(again.Changed);

        File.WriteAllText(Path.Combine(extensionsDir, "agentnotify.ts"), "// edited");
        Assert.False(HarnessInstaller.InstallPiExtension(extensionsDir, "// pi", force: false, dryRun: false).Success);
    }
}

public sealed class HarnessCatalogTests
{
    [Theory]
    [InlineData("opencode", ".config")]
    [InlineData("codex", ".codex")]
    [InlineData("claude", ".claude")]
    [InlineData("gemini", ".gemini")]
    [InlineData("copilot", ".copilot")]
    [InlineData("cursor", ".cursor")]
    [InlineData("muse", ".config")]
    [InlineData("kilo", ".config")]
    [InlineData("openclaw", ".openclaw")]
    [InlineData("hermes", ".hermes")]
    [InlineData("pi", ".pi")]
    public void PersonalDir_SitsUnderTheHomeDirectory(string id, string firstSegment)
    {
        var target = HarnessCatalog.Find(id);
        Assert.NotNull(target);

        var dir = HarnessCatalog.DefaultHarnessDir(target!);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.StartsWith(Path.Combine(home, firstSegment), dir, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectDir_SitsUnderTheRepository()
    {
        var dir = HarnessCatalog.DefaultHarnessDir(HarnessCatalog.OpenCode, "/repo");
        Assert.Equal(Path.Combine("/repo", ".opencode", "plugins"), dir);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndUnknownIdsAreNull()
    {
        Assert.Same(HarnessCatalog.ClaudeCode, HarnessCatalog.Find("CLAUDE"));
        Assert.Same(HarnessCatalog.OpenCode, HarnessCatalog.Find("OpenCode"));
        Assert.Null(HarnessCatalog.Find("nonesuch"));
    }

    [Fact]
    public void HookCommand_ReferencesTheScriptAgentAndEvent()
    {
        var command = HarnessInstaller.HookCommand("/home/u/.codex/agentnotify/agentnotify_hook.py", "codex", "permission");
        Assert.Contains("agentnotify_hook.py", command, StringComparison.Ordinal);
        Assert.Contains("codex permission", command, StringComparison.Ordinal);
    }
}

public sealed class InstallHarnessCliTests
{
    [Theory]
    [InlineData("opencode")]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    [InlineData("muse")]
    [InlineData("kilo")]
    [InlineData("openclaw")]
    [InlineData("hermes")]
    [InlineData("pi")]
    public async Task InstallHarness_WritesBundledHarness(string agent)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-harness-{Guid.NewGuid():N}");
        try
        {
            var exitCode = await AgentNotify.Cli.Program.Main(["install-harness", agent, "--path", root]);

            Assert.Equal(0, exitCode);
            if (agent is "opencode" or "kilo")
            {
                var plugin = Path.Combine(root, "agentnotify.js");
                Assert.True(File.Exists(plugin));
                var text = await File.ReadAllTextAsync(plugin);
                Assert.Contains("AgentNotify", text, StringComparison.Ordinal);
                if (agent == "kilo")
                {
                    Assert.Contains("const AGENT = \"kilo\"", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("const AGENT = \"opencode\"", text, StringComparison.Ordinal);
                }
            }
            else if (agent == "hermes")
            {
                Assert.True(File.Exists(Path.Combine(root, "agentnotify", "plugin.yaml")));
                Assert.True(File.Exists(Path.Combine(root, "agentnotify", "__init__.py")));
            }
            else if (agent == "pi")
            {
                var extension = Path.Combine(root, "agentnotify.ts");
                Assert.True(File.Exists(extension));
                Assert.Contains("agentnotify", await File.ReadAllTextAsync(extension), StringComparison.Ordinal);
            }
            else if (agent == "openclaw")
            {
                var script = Path.Combine(root, "agentnotify", "agentnotify_openclaw.py");
                Assert.True(File.Exists(script));
                Assert.Contains("agentnotify", await File.ReadAllTextAsync(script), StringComparison.Ordinal);
            }
            else
            {
                var script = Path.Combine(root, "agentnotify", "agentnotify_hook.py");
                Assert.True(File.Exists(script));
                Assert.Contains("agentnotify", await File.ReadAllTextAsync(script), StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallHarness_ProtectsExistingCustomizationUnlessForced()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-harness-{Guid.NewGuid():N}");
        var plugin = Path.Combine(root, "agentnotify.js");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(plugin, "// custom plugin");

            Assert.Equal(1, await AgentNotify.Cli.Program.Main(["install-harness", "opencode", "--path", root]));
            Assert.Equal("// custom plugin", await File.ReadAllTextAsync(plugin));

            Assert.Equal(0, await AgentNotify.Cli.Program.Main(["install-harness", "opencode", "--path", root, "--force"]));
            Assert.Contains("AgentNotify", await File.ReadAllTextAsync(plugin), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallHarness_DryRunDoesNotWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-harness-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, await AgentNotify.Cli.Program.Main(["install-harness", "codex", "--path", root, "--dry-run"]));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallHarness_RejectsUnknownHosts()
    {
        Assert.Equal(1, await AgentNotify.Cli.Program.Main(["install-harness", "nonesuch"]));
    }
}
