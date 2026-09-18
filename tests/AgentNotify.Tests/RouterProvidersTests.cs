using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Router;
using AgentNotify.Core.Router.Connect;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Tests;

/// <summary>
/// One-step providers: presets with per-model wires, model discovery, subscription sign-ins, and
/// what the proxy sends to each kind.
/// </summary>
public sealed class RouterProvidersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"an-router-providers-{Guid.NewGuid():N}");
    private readonly RouterRepository _repo;
    private readonly RouterConfigService _config;
    private readonly FakeHandler _handler = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));

    public RouterProvidersTests()
    {
        Directory.CreateDirectory(_dir);
        _repo = new RouterRepository(Path.Combine(_dir, "agentnotify.db"));
        _config = new RouterConfigService(_repo, new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)),
            new ConfigStore(Path.Combine(_dir, "cfg"), applyEnvOverrides: false));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CodexHome => Path.Combine(_dir, "codex");
    private string MuseHome => Path.Combine(_dir, "muse");
    private string OpenCodeAuth => Path.Combine(_dir, "opencode-auth.json");

    private RouterCredentialSource Credentials(HttpClient http) =>
        new(_config, http, _clock, CodexHome, MuseHome, OpenCodeAuth);

    private RouterProxy Proxy() =>
        new(_config, _repo, handler: _handler, clock: _clock, headersTimeout: TimeSpan.FromSeconds(5),
            idleTimeout: TimeSpan.FromSeconds(5), credentials: Credentials);

    private static string Jwt(DateTimeOffset expires)
    {
        static string Part(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part(new { alg = "none" })}.{Part(new { exp = expires.ToUnixTimeSeconds() })}.sig";
    }

    private void WriteCodexAuth(string access, string refresh = "refresh-1")
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            OPENAI_API_KEY = (string?)null,
            tokens = new { access_token = access, refresh_token = refresh, id_token = "id-1", account_id = "acct-1" },
            last_refresh = "2026-09-10T00:00:00Z"
        }));
    }

    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/event-stream")
    };

    private const string AnthropicSse =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"x\",\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private const string ResponsesSse =
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\n" +
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}}\n\n";

    // ---- presets and per-model wires -----------------------------------------------------

    [Fact]
    public void OpenCodePresets_AssignEachModelFamilyItsWire()
    {
        var go = RouterPresetCatalog.FindById("opencode-go")!;
        var wires = go.ModelWires(["minimax-m3", "kimi-k3", "gpt-5.6-luna", "qwen3.8-max", "glm-5.3"]);
        Assert.Equal(RouterWire.AnthropicMessages, wires["minimax-m3"]);
        Assert.Equal(RouterWire.AnthropicMessages, wires["qwen3.8-max"]);
        Assert.Equal(RouterWire.OpenAiResponses, wires["gpt-5.6-luna"]);
        // Models on the preset's own wire need no override.
        Assert.False(wires.ContainsKey("kimi-k3"));

        var zen = RouterPresetCatalog.FindById("opencode")!;
        Assert.Equal(RouterWire.AnthropicMessages, zen.WireFor("claude-opus-5"));
        // Zen's MiniMax is on Chat Completions, unlike Go's.
        Assert.Equal(RouterWire.OpenAiChat, zen.WireFor("minimax-m3"));
        Assert.False(zen.Supports("gemini-3.8-flash"));
    }

    [Fact]
    public async Task ModelWires_RoundTripThroughStorage_AndNarrowTheResolvedTarget()
    {
        await _config.CreateUpstreamAsync("opencode-go", "OpenCode Go", RouterWire.OpenAiChat, "https://opencode.ai/zen/go/v1",
            "sk-go-12345678", ["minimax-m3", "kimi-k3"], modelWires: new Dictionary<string, string>
            {
                ["minimax-m3"] = RouterWire.AnthropicMessages,
                // Same as the upstream's own wire, and an undeclared model: both dropped.
                ["kimi-k3"] = RouterWire.OpenAiChat,
                ["ghost"] = RouterWire.OpenAiResponses
            });

        var stored = (await _config.GetSnapshotAsync()).Upstreams.Single();
        Assert.Equal(RouterAuth.ApiKey, stored.Auth);
        Assert.Equal(new Dictionary<string, string> { ["minimax-m3"] = RouterWire.AnthropicMessages }, stored.ModelWires);

        var snapshot = await _config.GetSnapshotAsync();
        Assert.Equal(RouterWire.AnthropicMessages, RouteResolver.Resolve(snapshot, "opencode-go/minimax-m3").Targets.Single().Upstream.Wire);
        Assert.Equal(RouterWire.OpenAiChat, RouteResolver.Resolve(snapshot, "opencode-go/kimi-k3").Targets.Single().Upstream.Wire);
        Assert.Equal(RouterWire.AnthropicMessages, RouteResolver.Resolve(snapshot, "minimax-m3").Targets.Single().Upstream.Wire);
    }

    [Fact]
    public async Task OlderDatabase_GainsAuthAndModelWireColumns_KeepingItsRows()
    {
        var db = Path.Combine(_dir, "old.db");
        await using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE router_upstreams (id TEXT PRIMARY KEY, slug TEXT NOT NULL, label TEXT NOT NULL, wire TEXT NOT NULL,
                  base_url TEXT NOT NULL, encrypted_key TEXT, models TEXT NOT NULL, enabled INTEGER NOT NULL,
                  created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO router_upstreams VALUES ('ru_1','deepseek','DeepSeek','openai_chat','https://api.deepseek.com/v1',NULL,'["deepseek-chat"]',1,
                  '2026-09-01T00:00:00+00:00','2026-09-01T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var repo = new RouterRepository(db);
        await repo.InitializeAsync();
        await repo.InitializeAsync(); // idempotent
        var upstream = (await repo.ListUpstreamsAsync()).Single();
        Assert.Equal("deepseek", upstream.Slug);
        Assert.Equal(RouterAuth.ApiKey, upstream.Auth);
        Assert.Empty(upstream.ModelWires);
    }

    [Fact]
    public async Task SubscriptionUpstream_StoresNoKey()
    {
        var created = await _config.CreateUpstreamAsync("chatgpt", "ChatGPT plan", RouterWire.OpenAiResponses,
            RouterPresetCatalog.CodexChatGptBaseUrl, "should-not-be-kept", ["gpt-5.5"], auth: RouterAuth.CodexChatGpt);
        Assert.Equal(RouterAuth.CodexChatGpt, created.Auth);
        Assert.False(created.HasKey);
        await Assert.ThrowsAsync<ArgumentException>(() => _config.CreateUpstreamAsync("x", "X", RouterWire.OpenAiChat,
            "https://api.example.com/v1", null, ["m"], auth: "cookie_jar"));
    }

    // ---- discovery ------------------------------------------------------------------------

    [Fact]
    public void ModelList_AcceptsTheCommonShapes()
    {
        Assert.Equal(["a", "b"], RouterModelDiscovery.ParseModelList("""{"object":"list","data":[{"id":"a"},{"id":"b"}]}"""u8.ToArray()));
        Assert.Equal(["a", "b"], RouterModelDiscovery.ParseModelList("""["a","b"]"""u8.ToArray()));
        Assert.Equal(["llama3"], RouterModelDiscovery.ParseModelList("""{"models":[{"name":"llama3"}]}"""u8.ToArray()));
        Assert.Throws<JsonException>(() => RouterModelDiscovery.ParseModelList("""{"nope":1}"""u8.ToArray()));
    }

    [Fact]
    public async Task Discovery_SendsTheKeyOnlyToTheBaseUrl_AndDropsUnsupportedFamilies()
    {
        using var proxy = Proxy();
        _handler.Enqueue(request =>
        {
            Assert.Equal("https://opencode.ai/zen/v1/models", request.RequestUri!.ToString());
            Assert.Equal("sk-zen-12345678", request.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"claude-opus-5"},{"id":"gemini-3.8-flash"},{"id":"kimi-k3"}]}""")
            };
        });
        var models = await proxy.Discovery.FetchAsync("https://opencode.ai/zen/v1", RouterWire.OpenAiChat, RouterAuth.ApiKey,
            "sk-zen-12345678", RouterPresetCatalog.FindById("opencode"), CancellationToken.None);
        Assert.Equal(["claude-opus-5", "kimi-k3"], models);

        await Assert.ThrowsAsync<ArgumentException>(() => proxy.Discovery.FetchAsync("http://example.com/v1",
            RouterWire.OpenAiChat, RouterAuth.ApiKey, "sk-12345678", null, CancellationToken.None));
    }

    [Fact]
    public async Task Discovery_RejectedKey_SaysSo()
    {
        using var proxy = Proxy();
        _handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.Discovery.FetchAsync(
            "https://api.deepseek.com/v1", RouterWire.OpenAiChat, RouterAuth.ApiKey, "sk-bad-12345678", null, CancellationToken.None));
        Assert.Contains("refused the key", error.Message);
    }

    [Fact]
    public async Task ChatGptPlanModels_ComeFromCodexsCache_WithoutHiddenOnes()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "models_cache.json"),
            """{"models":[{"slug":"gpt-5.5","visibility":"list"},{"slug":"codex-auto-review","visibility":"hide"},{"slug":"gpt-6-astra","visibility":"list"}]}""");
        using var proxy = Proxy();
        var models = await proxy.Discovery.FetchAsync(null, RouterWire.OpenAiResponses, RouterAuth.CodexChatGpt, null,
            RouterPresetCatalog.FindById("chatgpt"), CancellationToken.None);
        Assert.Equal(["gpt-5.5", "gpt-6-astra"], models);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void OpenCodeKeys_AreReusedOnlyWhenTheyArePlainApiKeys()
    {
        File.WriteAllText(OpenCodeAuth,
            """{"opencode-go":{"type":"api","key":"sk-go-abcdefgh"},"openai":{"type":"oauth","access":"x","refresh":"y"}}""");
        var credentials = Credentials(new HttpClient(_handler));
        Assert.Equal("sk-go-abcdefgh", credentials.ReadOpenCodeKey("opencode-go"));
        Assert.Null(credentials.ReadOpenCodeKey("openai"));
        Assert.Null(credentials.ReadOpenCodeKey("deepseek"));
    }

    // ---- subscription sign-ins ------------------------------------------------------------

    [Fact]
    public async Task ChatGptPlan_UsesCodexsSignIn_AndAdaptsTheBody()
    {
        WriteCodexAuth(Jwt(_clock.GetUtcNow().AddDays(5)));
        await _config.CreateUpstreamAsync("chatgpt", "ChatGPT plan", RouterWire.OpenAiResponses,
            RouterPresetCatalog.CodexChatGptBaseUrl, null, ["gpt-5.5"], auth: RouterAuth.CodexChatGpt);
        using var proxy = Proxy();
        _handler.Enqueue(request =>
        {
            Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.RequestUri!.ToString());
            Assert.Equal("acct-1", request.Headers.GetValues("chatgpt-account-id").Single());
            Assert.StartsWith("agentnotify-router/", request.Headers.UserAgent.ToString());
            return Sse(ResponsesSse);
        });

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "chatgpt/gpt-5.5", stream = true, store = true, max_output_tokens = 100,
            input = new object[] { new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "hi" } } } }
        });
        var result = await proxy.ExecuteAsync(new RouterInbound(RouterWire.OpenAiResponses, body), new Sink(), CancellationToken.None);
        Assert.Equal(200, result.Status);

        var sent = JsonNode.Parse(_handler.Bodies.Single())!.AsObject();
        Assert.False((bool)sent["store"]!);
        Assert.False(sent.ContainsKey("max_output_tokens"));
        Assert.Equal("", (string?)sent["instructions"]);
        Assert.Equal("gpt-5.5", (string?)sent["model"]);
    }

    [Fact]
    public async Task ChatGptPlan_ExpiredSignIn_IsRenewedAndWrittenBackForCodex()
    {
        WriteCodexAuth(Jwt(_clock.GetUtcNow().AddMinutes(-1)));
        var renewed = Jwt(_clock.GetUtcNow().AddDays(10));
        var credentials = Credentials(new HttpClient(_handler));
        _handler.Enqueue(request =>
        {
            Assert.Equal(RouterCredentialSource.CodexTokenUrl, request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { access_token = renewed, refresh_token = "refresh-2", id_token = "id-2" }))
            };
        });

        var upstream = new StoredRouterUpstream("ru", "chatgpt", "ChatGPT", RouterWire.OpenAiResponses,
            RouterPresetCatalog.CodexChatGptBaseUrl, null, [], true, _clock.GetUtcNow(), _clock.GetUtcNow()) { Auth = RouterAuth.CodexChatGpt };
        var credential = await credentials.GetAsync(upstream, renew: false, CancellationToken.None);
        Assert.Equal(renewed, credential.Secret);

        var sentRefresh = JsonNode.Parse(_handler.Bodies.Single())!;
        Assert.Equal("refresh-1", (string?)sentRefresh["refresh_token"]);
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(CodexHome, "auth.json")))!;
        Assert.Equal(renewed, (string?)file["tokens"]!["access_token"]);
        Assert.Equal("refresh-2", (string?)file["tokens"]!["refresh_token"]);
        // Everything else Codex keeps in the file is left alone.
        Assert.Equal("chatgpt", (string?)file["auth_mode"]);
        Assert.Equal("acct-1", (string?)file["tokens"]!["account_id"]);
    }

    [Fact]
    public async Task ChatGptPlan_NotSignedIn_FailsTheAttemptWithoutSending()
    {
        await _config.CreateUpstreamAsync("chatgpt", "ChatGPT plan", RouterWire.OpenAiResponses,
            RouterPresetCatalog.CodexChatGptBaseUrl, null, ["gpt-5.5"], auth: RouterAuth.CodexChatGpt);
        using var proxy = Proxy();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { model = "chatgpt/gpt-5.5", stream = true, input = "hi" });
        var result = await proxy.ExecuteAsync(new RouterInbound(RouterWire.OpenAiResponses, body), new Sink(), CancellationToken.None);
        Assert.Equal("subscription_signed_out", result.Attempts.Single().ErrorCode);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task MuseCodePlan_MintsAKeyOnce_AndReusesIt()
    {
        Directory.CreateDirectory(MuseHome);
        File.WriteAllText(Path.Combine(MuseHome, "auth.json"), """{"access_token":"identity-1","refresh_token":"r"}""");
        var credentials = Credentials(new HttpClient(_handler));
        _handler.Enqueue(request =>
        {
            Assert.Equal(RouterCredentialSource.MuseKeyUrl, request.RequestUri!.ToString());
            Assert.Equal("identity-1", request.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"api_key":"LLM|minted"}""") };
        });
        var upstream = new StoredRouterUpstream("ru", "muse-code", "Muse Code", RouterWire.OpenAiResponses,
            RouterPresetCatalog.MetaBaseUrl, null, [], true, _clock.GetUtcNow(), _clock.GetUtcNow()) { Auth = RouterAuth.MuseCode };

        Assert.Equal("LLM|minted", (await credentials.GetAsync(upstream, false, CancellationToken.None)).Secret);
        Assert.Equal("LLM|minted", (await credentials.GetAsync(upstream, false, CancellationToken.None)).Secret);
        Assert.Single(_handler.Requests);
    }

    // ---- OpenCode Go ----------------------------------------------------------------------

    [Fact]
    public async Task OpenCodeHost_GetsTheAgentsSessionId()
    {
        await _config.CreateUpstreamAsync("opencode-go", "OpenCode Go", RouterWire.OpenAiChat, "https://opencode.ai/zen/go/v1",
            "sk-go-12345678", ["minimax-m3"], modelWires: new Dictionary<string, string> { ["minimax-m3"] = RouterWire.AnthropicMessages });
        using var proxy = Proxy();
        _handler.Enqueue(request =>
        {
            // Resolved to the model's own wire, not the upstream's.
            Assert.Equal("https://opencode.ai/zen/go/v1/messages", request.RequestUri!.ToString());
            Assert.Equal("session-42", request.Headers.GetValues("x-opencode-session").Single());
            return Sse(AnthropicSse);
        });
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "opencode-go/minimax-m3", max_tokens = 10, stream = true,
            messages = new[] { new { role = "user", content = "hi" } }
        });
        var inbound = new RouterInbound(RouterWire.AnthropicMessages, body) { SessionId = "session-42" };
        var result = await proxy.ExecuteAsync(inbound, new Sink(), CancellationToken.None);
        Assert.Equal(200, result.Status);
    }

    // ---- Codex catalogue ------------------------------------------------------------------

    [Fact]
    public async Task CodexCatalogue_ReusesCodexsOwnEntryForChatGptPlanModels()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "models_cache.json"),
            """{"models":[{"slug":"gpt-5.5","display_name":"GPT-5.5","context_window":272000,"shell_type":"unified_exec","visibility":"list","priority":9,"model_messages":{"instructions_template":"native"}}]}""");
        await _config.CreateUpstreamAsync("chatgpt", "ChatGPT plan", RouterWire.OpenAiResponses,
            RouterPresetCatalog.CodexChatGptBaseUrl, null, ["gpt-5.5"], auth: RouterAuth.CodexChatGpt);
        await _config.CreateUpstreamAsync("deepseek", "DeepSeek", RouterWire.OpenAiChat, "https://api.deepseek.com/v1",
            "sk-ds-12345678", ["deepseek-chat"]);

        var catalog = CodexModelCatalog.Build(await _config.GetSnapshotAsync(), nativeEntries: CodexModelCatalog.ReadNative(CodexHome));
        var models = catalog["models"]!.AsArray().Select(m => m!.AsObject()).ToList();
        var plan = models.Single(m => (string?)m["slug"] == "chatgpt/gpt-5.5");
        Assert.Equal(272000, (int)plan["context_window"]!);
        Assert.Equal("native", (string?)plan["model_messages"]!["instructions_template"]);
        Assert.Equal("GPT-5.5 · ChatGPT plan", (string?)plan["display_name"]);
        var routed = models.Single(m => (string?)m["slug"] == "deepseek/deepseek-chat");
        Assert.Equal(CodexModelCatalog.DefaultShellType, (string?)routed["shell_type"]);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();
        public readonly List<HttpRequestMessage> Requests = [];
        public readonly List<byte[]> Bodies = [];
        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond) => _queue.Enqueue(respond);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add(request);
            if (_queue.Count == 0) throw new InvalidOperationException("No queued response for " + request.RequestUri);
            return _queue.Dequeue()(request);
        }
    }

    private sealed class Sink : IRouterClientSink
    {
        public bool HasStarted { get; private set; }
        public Task SetStatusAndHeadersAsync(int statusCode, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            HasStarted = true;
            return Task.CompletedTask;
        }
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
