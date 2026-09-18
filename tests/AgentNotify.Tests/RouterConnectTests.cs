using System.Security.Cryptography;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Router;
using AgentNotify.Core.Router.Connect;

namespace AgentNotify.Tests;

/// <summary>
/// Connecting an agent edits a file that belongs to another program, so these tests are mostly about
/// what must survive that edit: the owner's own settings, the rest of the file, and a way back.
/// </summary>
public sealed class RouterConnectTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"an-router-connect-{Guid.NewGuid():N}");
    private string _home = "";
    private string _stateDir = "";
    private RouterConfigService _config = null!;
    private RouterConnectService _connect = null!;

    public async Task InitializeAsync()
    {
        _home = Path.Combine(_root, "home");
        _stateDir = Path.Combine(_root, "state", "router");
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(_stateDir);

        var store = new ConfigStore(Path.Combine(_root, "config"), applyEnvOverrides: false);
        var config = new AgentNotifyConfig { Port = 47821, AuthToken = "tok" };
        var repository = new RouterRepository(Path.Combine(_root, "router.db"));
        await repository.InitializeAsync();
        _config = new RouterConfigService(
            repository,
            new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)),
            store,
            config);
        await _config.SetRouterEnabledAsync(true);
        await _config.CreateUpstreamAsync("deepseek", "DeepSeek", RouterWire.OpenAiChat,
            "https://api.deepseek.com/v1", "sk-test-abcdefgh", ["deepseek-chat", "deepseek-reasoner"]);
        await _config.CreateRouteAsync("coding", RouterKind.Combo, ["deepseek/deepseek-chat"]);
        _connect = new RouterConnectService(_config, _stateDir, () => config.Port, _home);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private string CodexConfig => Path.Combine(_home, ".codex", "config.toml");
    private string ClaudeSettings => Path.Combine(_home, ".claude", "settings.json");

    [Fact]
    public void Profiles_AlwaysIncludeBuiltIns_AddOtherNativeAccounts_AndGroupByHost()
    {
        var accounts = new[]
        {
            new QuotaAccountDefinition("codex:default", "codex", "Work laptop", Path.Combine(_home, ".codex")),
            new QuotaAccountDefinition("claude_code:home:second", "claude_code", "Profile · second", Path.Combine(_home, ".claude-second")),
            new QuotaAccountDefinition("codex:home:second", "codex", "Profile · second", Path.Combine(_home, ".codex-second")),
            // Same directory as the built-in account: not listed twice.
            new QuotaAccountDefinition("q_" + new string('a', 32), "claude_code", "Dup", Path.Combine(_home, ".claude")),
        };
        var profiles = RouterAgentProfile.FromAccounts(accounts, _home);
        Assert.Equal(["codex", "codex:home:second", "claude_code", "claude_code:home:second"], profiles.Select(p => p.Id));
        Assert.Equal("Work laptop", profiles[0].Label);
        Assert.True(profiles[0].IsDefault);
        Assert.False(profiles[1].IsDefault);
    }

    [Fact]
    public async Task SecondAccount_ConnectsIntoItsOwnFiles_LeavingTheBuiltInAlone()
    {
        var second = Path.Combine(_home, ".codex-second");
        Directory.CreateDirectory(second);
        var connect = new RouterConnectService(_config, _stateDir, () => 47821, _home, profiles: () => RouterAgentProfile.FromAccounts(
            [new QuotaAccountDefinition("codex:home:second", "codex", "Profile · second", second)], _home));

        var result = await connect.ConnectAsync("codex:home:second", new RouterConnectRequest("deepseek/deepseek-chat"));
        Assert.True(result.Agent.Connected);
        Assert.Equal("codex", result.Agent.Kind);
        Assert.Equal("Codex · Profile · second", result.Agent.DisplayName);
        var toml = File.ReadAllText(Path.Combine(second, "config.toml"));
        Assert.Contains("model = \"deepseek/deepseek-chat\"", toml);
        // Its own catalogue, not the built-in account's.
        Assert.Contains("codex-model-catalog-codex-home-second.json", toml);
        Assert.False(File.Exists(CodexConfig));

        var listed = await connect.ListAsync();
        Assert.False(listed.Single(a => a.Id == "codex").Connected);
        Assert.True(listed.Single(a => a.Id == "codex:home:second").Connected);

        await connect.DisconnectAsync("codex:home:second");
        Assert.DoesNotContain("agentnotify", File.ReadAllText(Path.Combine(second, "config.toml")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => connect.ConnectAsync("codex:home:nope", new RouterConnectRequest()));
    }

    [Fact]
    public async Task Listing_ReportsBothAgentsAsDetectedAndNotConnected()
    {
        var agents = await _connect.ListAsync();
        Assert.Equal(["codex", "claude_code"], agents.Select(a => a.Id));
        Assert.All(agents, agent =>
        {
            Assert.True(agent.Detected);
            Assert.False(agent.Connected);
            Assert.Null(agent.Blocked);
        });
    }

    [Fact]
    public async Task Connecting_Codex_KeepsEveryOtherSettingAndRestoresTheModelOnDisconnect()
    {
        const string original =
            """
            model = "gpt-5.6-luna"
            model_reasoning_effort = "high"

            [projects."/Users/someone/work"]
            trust_level = "trusted"

            [tui]
            hide_agent_reasoning = false
            """;
        File.WriteAllText(CodexConfig, original);

        await _connect.ConnectAsync("codex", new RouterConnectRequest("coding"));
        var written = File.ReadAllText(CodexConfig);

        // The owner's own tables survive untouched.
        Assert.Contains("[projects.\"/Users/someone/work\"]", written);
        Assert.Contains("trust_level = \"trusted\"", written);
        Assert.Contains("hide_agent_reasoning = false", written);

        // Bare keys must precede the first table, or TOML would read them as part of it.
        Assert.True(written.IndexOf("model_provider = \"agentnotify\"", StringComparison.Ordinal)
            < written.IndexOf("[projects.", StringComparison.Ordinal));
        Assert.Contains("model = \"coding\"", written);
        Assert.Contains("model_catalog_json = ", written);
        Assert.Contains("[model_providers.agentnotify]", written);
        Assert.Contains("wire_api = \"responses\"", written);
        Assert.Contains("# was: model = \"gpt-5.6-luna\"", written);

        var agent = (await _connect.ListAsync()).Single(a => a.Id == "codex");
        Assert.True(agent.Connected);
        Assert.Equal("coding", agent.SelectedModel);
        Assert.True(agent.CatalogModelCount > 0);

        await _connect.DisconnectAsync("codex");
        var restored = File.ReadAllText(CodexConfig);
        Assert.Contains("model = \"gpt-5.6-luna\"", restored);
        Assert.Contains("model_reasoning_effort = \"high\"", restored);
        Assert.DoesNotContain("agentnotify", restored);
        Assert.Contains("trust_level = \"trusted\"", restored);
        Assert.False((await _connect.ListAsync()).Single(a => a.Id == "codex").Connected);
    }

    [Fact]
    public async Task Connecting_Codex_LeavesNoDuplicateKey_WhichWouldStopCodexParsingTheFile()
    {
        File.WriteAllText(CodexConfig,
            """
            model = "gpt-5.6-luna"
            model_reasoning_effort = "high"
            approval_policy = "on-request"

            [tui]
            hide_agent_reasoning = false
            """);

        await _connect.ConnectAsync("codex", new RouterConnectRequest("coding"));
        var lines = File.ReadAllLines(CodexConfig);

        // TOML refuses a key assigned twice, so the owner's own assignments are commented out rather
        // than left in place beside the managed ones.
        foreach (var key in new[] { "model", "model_reasoning_effort" })
        {
            var assignments = lines.Count(line => line.TrimStart().StartsWith(key + " =", StringComparison.Ordinal));
            Assert.Equal(1, assignments);
        }

        Assert.Contains(lines, line => line.Contains("# agentnotify disabled: model = \"gpt-5.6-luna\"", StringComparison.Ordinal));
        // Settings the router does not own keep working untouched.
        Assert.Contains(lines, line => line.Trim() == "approval_policy = \"on-request\"");

        await _connect.DisconnectAsync("codex");
        var after = File.ReadAllLines(CodexConfig);
        Assert.Contains(after, line => line.Trim() == "model = \"gpt-5.6-luna\"");
        Assert.Contains(after, line => line.Trim() == "model_reasoning_effort = \"high\"");
        Assert.DoesNotContain(after, line => line.Contains("agentnotify", StringComparison.Ordinal));
        Assert.Equal(1, after.Count(line => line.TrimStart().StartsWith("model =", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Connecting_Codex_EmbedsTheRouterKeyAndWritesTheCatalogue()
    {
        var key = await _config.GetRouterKeyAsync();
        await _connect.ConnectAsync("codex", new RouterConnectRequest("deepseek/deepseek-chat"));

        Assert.Contains($"experimental_bearer_token = \"{key}\"", File.ReadAllText(CodexConfig));

        var catalog = JsonDocument.Parse(File.ReadAllText(_connect.CatalogPath));
        var slugs = catalog.RootElement.GetProperty("models").EnumerateArray()
            .Select(model => model.GetProperty("slug").GetString()).ToList();
        Assert.Contains("coding", slugs);
        Assert.Contains("combo/coding", slugs);
        Assert.Contains("deepseek/deepseek-chat", slugs);
        Assert.Contains("deepseek/deepseek-reasoner", slugs);

        // Codex refuses a catalogue entry without these, so a missing one would break its startup.
        foreach (var model in catalog.RootElement.GetProperty("models").EnumerateArray())
        {
            foreach (var required in new[]
                     {
                         "slug", "display_name", "supported_reasoning_levels", "shell_type", "visibility",
                         "supported_in_api", "priority", "support_verbosity", "truncation_policy",
                         "experimental_supported_tools", "model_messages", "base_instructions"
                     })
            {
                Assert.True(model.TryGetProperty(required, out _), $"catalogue entry is missing '{required}'");
            }
        }
    }

    [Fact]
    public async Task Connecting_Codex_WritesSubagentAndReviewSettings()
    {
        await _connect.ConnectAsync("codex", new RouterConnectRequest(
            "coding",
            Options: new Dictionary<string, string>
            {
                ["reasoning_effort"] = "high",
                ["subagent_model"] = "deepseek/deepseek-chat",
                ["subagent_reasoning_effort"] = "low",
                ["review_model"] = "deepseek/deepseek-reasoner"
            }));

        var written = File.ReadAllText(CodexConfig);
        Assert.Contains("model_reasoning_effort = \"high\"", written);
        Assert.Contains("default_subagent_model = \"deepseek/deepseek-chat\"", written);
        Assert.Contains("default_subagent_reasoning_effort = \"low\"", written);
        Assert.Contains("review_model = \"deepseek/deepseek-reasoner\"", written);

        var agent = (await _connect.ListAsync()).Single(a => a.Id == "codex");
        Assert.Equal("high", agent.OptionValues["reasoning_effort"]);
        Assert.Equal("deepseek/deepseek-chat", agent.OptionValues["subagent_model"]);
        Assert.Contains(agent.Options, option => option.Id == "subagent_model" && option.IsModelSelector);
    }

    [Fact]
    public async Task Connecting_RefusesAModelTheRouterDoesNotServe()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _connect.ConnectAsync("codex", new RouterConnectRequest("gpt-5.6-luna")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _connect.ConnectAsync("codex", new RouterConnectRequest("coding",
                Options: new Dictionary<string, string> { ["subagent_model"] = "not/real" })));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _connect.ConnectAsync("codex", new RouterConnectRequest("coding",
                Options: new Dictionary<string, string> { ["reasoning_effort"] = "extreme" })));
        Assert.False(File.Exists(CodexConfig));
    }

    [Fact]
    public async Task Connecting_ClaudeCode_MapsMenuSlotsAndKeepsOtherSettings()
    {
        File.WriteAllText(ClaudeSettings,
            """
            {
              "model": "opus",
              "env": { "MY_OWN": "keep me", "ANTHROPIC_MODEL": "claude-opus-4" },
              "permissions": { "allow": ["Bash(ls:*)"] }
            }
            """);

        await _connect.ConnectAsync("claude_code", new RouterConnectRequest(
            "coding",
            ModelSlots: new Dictionary<string, string>
            {
                ["sonnet"] = "deepseek/deepseek-chat",
                ["small_fast"] = "deepseek/deepseek-chat"
            }));

        var root = JsonDocument.Parse(File.ReadAllText(ClaudeSettings)).RootElement;
        var env = root.GetProperty("env");
        Assert.Equal("http://127.0.0.1:47821/router", env.GetProperty("ANTHROPIC_BASE_URL").GetString());
        Assert.Equal(await _config.GetRouterKeyAsync(), env.GetProperty("ANTHROPIC_AUTH_TOKEN").GetString());
        Assert.Equal("coding", env.GetProperty("ANTHROPIC_MODEL").GetString());
        Assert.Equal("deepseek/deepseek-chat", env.GetProperty("ANTHROPIC_DEFAULT_SONNET_MODEL").GetString());
        Assert.Equal("deepseek/deepseek-chat", env.GetProperty("ANTHROPIC_SMALL_FAST_MODEL").GetString());
        Assert.Equal("keep me", env.GetProperty("MY_OWN").GetString());
        Assert.Equal("opus", root.GetProperty("model").GetString());
        Assert.Equal("Bash(ls:*)", root.GetProperty("permissions").GetProperty("allow")[0].GetString());

        await _connect.DisconnectAsync("claude_code");
        var after = JsonDocument.Parse(File.ReadAllText(ClaudeSettings)).RootElement.GetProperty("env");
        Assert.Equal("claude-opus-4", after.GetProperty("ANTHROPIC_MODEL").GetString());
        Assert.False(after.TryGetProperty("ANTHROPIC_BASE_URL", out _));
        Assert.False(after.TryGetProperty("ANTHROPIC_AUTH_TOKEN", out _));
        Assert.False(after.TryGetProperty("ANTHROPIC_DEFAULT_SONNET_MODEL", out _));
        Assert.Equal("keep me", after.GetProperty("MY_OWN").GetString());
    }

    [Fact]
    public async Task Connecting_ClaudeCode_AddsRoutedModelsToItsOwnPicker()
    {
        await _connect.ConnectAsync("claude_code", new RouterConnectRequest(
            "coding",
            Options: new Dictionary<string, string>
            {
                ["behaves_as"] = "claude-opus-4-5",
                ["replace_built_in_options"] = "on"
            }));

        var picker = JsonDocument.Parse(File.ReadAllText(ClaudeSettings)).RootElement.GetProperty("modelPicker");
        Assert.True(picker.GetProperty("replaceBuiltInOptions").GetBoolean());
        var rows = picker.GetProperty("options").EnumerateArray().ToList();
        var models = rows.Select(row => row.GetProperty("model").GetString()).ToList();
        Assert.Contains("coding", models);
        Assert.Contains("combo/coding", models);
        Assert.Contains("deepseek/deepseek-chat", models);
        // Without behavesAs, Claude Code cannot tell a routed model's context window or capabilities.
        Assert.All(rows, row => Assert.Equal("claude-opus-4-5", row.GetProperty("behavesAs").GetString()));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("description").GetString())));

        var agent = (await _connect.ListAsync()).Single(a => a.Id == "claude_code");
        Assert.Equal("claude-opus-4-5", agent.OptionValues["behaves_as"]);
        Assert.Equal("on", agent.OptionValues["replace_built_in_options"]);

        await _connect.DisconnectAsync("claude_code");
        Assert.False(JsonDocument.Parse(File.ReadAllText(ClaudeSettings)).RootElement.TryGetProperty("modelPicker", out _));
    }

    [Fact]
    public async Task Disconnecting_ClaudeCode_PutsTheOwnersOwnPickerRowsBack()
    {
        File.WriteAllText(ClaudeSettings,
            """
            {
              "modelPicker": { "options": [{ "model": "my-own-model", "label": "Mine" }], "replaceBuiltInOptions": true }
            }
            """);

        await _connect.ConnectAsync("claude_code", new RouterConnectRequest("coding"));
        Assert.Contains("deepseek/deepseek-chat", File.ReadAllText(ClaudeSettings));

        await _connect.DisconnectAsync("claude_code");
        var picker = JsonDocument.Parse(File.ReadAllText(ClaudeSettings)).RootElement.GetProperty("modelPicker");
        Assert.Equal("my-own-model", picker.GetProperty("options")[0].GetProperty("model").GetString());
        Assert.True(picker.GetProperty("replaceBuiltInOptions").GetBoolean());
    }

    [Fact]
    public async Task Connecting_RefusesToRewriteSettingsThatAreNotValidJson()
    {
        File.WriteAllText(ClaudeSettings, "{ this is not json");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _connect.ConnectAsync("claude_code", new RouterConnectRequest("coding")));
        Assert.Contains("not valid JSON", error.Message);
        Assert.Equal("{ this is not json", File.ReadAllText(ClaudeSettings));
    }

    [Fact]
    public async Task EveryChange_KeepsACopyThatCanBeRestored()
    {
        File.WriteAllText(CodexConfig, "model = \"gpt-5.6-luna\"\n");
        await _connect.ConnectAsync("codex", new RouterConnectRequest("coding"));

        var agent = (await _connect.ListAsync()).Single(a => a.Id == "codex");
        var backup = Assert.Single(agent.Backups);
        Assert.Equal("before connecting", backup.Reason);

        // Something else edits the file afterwards; restoring must bring back the copy, not the
        // AgentNotify-managed version.
        File.AppendAllText(CodexConfig, "\n# a later edit\n");
        var result = await _connect.RestoreAsync("codex", backup.Id);
        var restored = File.ReadAllText(CodexConfig);
        Assert.Equal("model = \"gpt-5.6-luna\"\n", restored);
        Assert.DoesNotContain("a later edit", restored);
        Assert.False(result.Agent.Connected);
        // The pre-restore file is itself kept, so a restore can be stepped back.
        Assert.Equal(2, result.Agent.Backups.Count);
    }

    [Fact]
    public async Task Reconnecting_StillRemembersTheOwnersOriginalModel()
    {
        File.WriteAllText(CodexConfig, "model = \"gpt-5.6-luna\"\n");
        await _connect.ConnectAsync("codex", new RouterConnectRequest("coding"));
        await _connect.ConnectAsync("codex", new RouterConnectRequest("deepseek/deepseek-chat"));
        await _connect.DisconnectAsync("codex");
        Assert.Contains("model = \"gpt-5.6-luna\"", File.ReadAllText(CodexConfig));
    }

    [Fact]
    public async Task ChangingTheRouter_RewritesTheCatalogueOfAConnectedAgent()
    {
        await _connect.ConnectAsync("codex", new RouterConnectRequest("deepseek/deepseek-chat"));
        await _config.CreateUpstreamAsync("groq", "Groq", RouterWire.OpenAiChat,
            "https://api.groq.com/openai/v1", "sk-test-ijklmnop", ["llama-3.3-70b"]);

        await _connect.RefreshAsync();
        var slugs = JsonDocument.Parse(File.ReadAllText(_connect.CatalogPath))
            .RootElement.GetProperty("models").EnumerateArray()
            .Select(model => model.GetProperty("slug").GetString()).ToList();
        Assert.Contains("groq/llama-3.3-70b", slugs);
    }

    [Fact]
    public async Task ChangingTheRouter_DropsASubagentModelThatNoLongerResolves()
    {
        await _config.CreateUpstreamAsync("groq", "Groq", RouterWire.OpenAiChat,
            "https://api.groq.com/openai/v1", null, ["llama-3.3-70b"]);
        await _connect.ConnectAsync("codex", new RouterConnectRequest("deepseek/deepseek-chat",
            Options: new Dictionary<string, string> { ["subagent_model"] = "groq/llama-3.3-70b" }));
        Assert.Contains("default_subagent_model", File.ReadAllText(CodexConfig));

        var groq = (await _config.ListUpstreamsAsync()).Single(u => u.Slug == "groq");
        await _config.DeleteUpstreamAsync(groq.Id);
        await _connect.RefreshAsync();

        // Codex would otherwise send a selector the router now refuses.
        Assert.DoesNotContain("default_subagent_model", File.ReadAllText(CodexConfig));
        Assert.Contains("model = \"deepseek/deepseek-chat\"", File.ReadAllText(CodexConfig));
    }

    [Fact]
    public async Task ConnectingAnAgentThatIsNotInstalled_IsRefusedAndReported()
    {
        Directory.Delete(Path.Combine(_home, ".codex"), recursive: true);
        var agent = (await _connect.ListAsync()).Single(a => a.Id == "codex");
        Assert.False(agent.Detected);
        Assert.Contains("not installed", agent.Blocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _connect.ConnectAsync("codex", new RouterConnectRequest("coding")));
    }

    [Fact]
    public async Task ConnectingBeforeTheRouterHasAnyModel_IsRefused()
    {
        var upstream = (await _config.ListUpstreamsAsync()).Single(u => u.Slug == "deepseek");
        var route = (await _config.ListRoutesAsync()).Single();
        await _config.DeleteRouteAsync(route.Id);
        await _config.DeleteUpstreamAsync(upstream.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _connect.ConnectAsync("codex", new RouterConnectRequest(null)));
        Assert.Contains("enabled upstream", error.Message);
    }

    [Fact]
    public async Task UnknownAgent_IsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _connect.ConnectAsync("cursor", new RouterConnectRequest("coding")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _connect.DisconnectAsync("cursor"));
    }
}
