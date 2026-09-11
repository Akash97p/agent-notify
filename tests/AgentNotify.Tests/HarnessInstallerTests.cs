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
}

public sealed class HarnessCatalogTests
{
    [Theory]
    [InlineData("opencode", ".config")]
    [InlineData("codex", ".codex")]
    [InlineData("claude", ".claude")]
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
    public async Task InstallHarness_WritesBundledHarness(string agent)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-harness-{Guid.NewGuid():N}");
        try
        {
            var exitCode = await AgentNotify.Cli.Program.Main(["install-harness", agent, "--path", root]);

            Assert.Equal(0, exitCode);
            if (agent == "opencode")
            {
                var plugin = Path.Combine(root, "agentnotify.js");
                Assert.True(File.Exists(plugin));
                Assert.Contains("AgentNotify", await File.ReadAllTextAsync(plugin), StringComparison.Ordinal);
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
