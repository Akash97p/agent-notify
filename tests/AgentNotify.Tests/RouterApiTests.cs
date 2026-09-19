using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Api;
using AgentNotify.Api.WebUi;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Router;
using AgentNotify.Core.Services;
using Microsoft.AspNetCore.Builder;

namespace AgentNotify.Tests;

public sealed class RouterApiTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _q = new();
        public readonly List<HttpRequestMessage> Requests = new();
        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> fn) => _q.Enqueue(fn);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (_q.Count == 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":123,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}", Encoding.UTF8, "application/json") });
            return Task.FromResult(_q.Dequeue()(request));
        }
    }

    private sealed class TestApp : IAsyncDisposable
    {
        public string Dir { get; }
        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";
        public WebApplication App { get; }
        public RouterConfigService RouterConfig { get; }
        public RouterRepository RouterRepo { get; }
        public FakeHandler Handler { get; }
        public AgentNotifyConfig Config { get; }
        public DeliveryDispatcher Dispatcher { get; }
        private readonly string _dbPath;

        private TestApp(string dir, int port, WebApplication app, RouterConfigService rc, RouterRepository repo, FakeHandler handler, AgentNotifyConfig cfg, DeliveryDispatcher disp, string db)
        {
            Dir = dir; Port = port; App = app; RouterConfig = rc; RouterRepo = repo; Handler = handler; Config = cfg; Dispatcher = disp; _dbPath = db;
        }

        public static async Task<TestApp> CreateAsync(bool routerEnabled = false, FakeHandler? handler = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"an-router-api-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var port = FreePort();
            var store = new ConfigStore(dir, applyEnvOverrides: false);
            var config = new AgentNotifyConfig { Port = port, AuthToken = "tok-" + Guid.NewGuid().ToString("N"), RouterEnabled = routerEnabled };
            if (routerEnabled && string.IsNullOrWhiteSpace(config.RouterKey))
                config.RouterKey = AgentNotifyConfig.GenerateRouterKey();
            store.Save(config);
            var loaded = store.Load();
            // Ensure config object is same as used in ApiHost (live)
            config = loaded;
            var db = Path.Combine(dir, "agentnotify.db");
            var repo = new SqliteNotificationRepository(db);
            await repo.InitializeAsync();
            var interactions = new SqliteInteractionRepository(db);
            await interactions.InitializeAsync();
            var delivery = new SqliteDeliveryRepository(db);
            await delivery.InitializeAsync();
            var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
            var routerRepo = new RouterRepository(db);
            await routerRepo.InitializeAsync();
            var routerConfig = new RouterConfigService(routerRepo, protector, store, config);
            // if routerEnabled, ensure key exists (already set)
            handler ??= new FakeHandler();
            var routerProxy = new RouterProxy(routerConfig, routerRepo, logger: null, handler: handler, clock: null);
            var profiles = new ProviderProfileService(delivery, protector);
            var dispatcher = new DeliveryDispatcher(delivery, profiles, [], logger: null);
            var service = new NotificationService(repo, config);
            var options = new WebUiOptions
            {
                ConfigStore = store,
                Providers = profiles,
                Routes = new DeliveryRouteService(delivery),
                Dispatcher = dispatcher,
                Router = routerProxy,
                RouterConfig = routerConfig,
                SecretProtection = "test",
                DesktopSurface = "test"
            };
            var app = ApiHost.Build(config, repo, service, logger: null, url: $"http://127.0.0.1:{port}", interactions: new InteractionService(interactions), webUi: options);
            await app.StartAsync();
            // wait ready
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (int i = 0; i < 20; i++)
            {
                try { var r = await c.GetAsync($"http://127.0.0.1:{port}/health"); if (r.IsSuccessStatusCode) break; } catch { }
                await Task.Delay(100);
            }
            return new TestApp(dir, port, app, routerConfig, routerRepo, handler, config, dispatcher, db);
        }

        public HttpClient Browser()
        {
            var c = new HttpClient { BaseAddress = new Uri(BaseUrl) };
            c.DefaultRequestHeaders.Add(WebUiEndpoints.CsrfHeader, "1");
            return c;
        }

        public HttpClient RouterClient(string? key = null)
        {
            var c = new HttpClient { BaseAddress = new Uri(BaseUrl) };
            if (key != null)
                c.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);
            return c;
        }

        public async ValueTask DisposeAsync()
        {
            try { await App.StopAsync(); } catch { }
            try { await App.DisposeAsync(); } catch { }
            try { await Dispatcher.DisposeAsync(); } catch { }
            // dispose router proxy? It was created inside TestApp but not stored; need to dispose
            // We'll dispose via App's WebUiOptions? Instead just clear pools
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, true); } catch { }
            try { File.Delete(_dbPath); } catch { }
            try { File.Delete(_dbPath + "-wal"); } catch { }
            try { File.Delete(_dbPath + "-shm"); } catch { }
        }
    }

    [Fact]
    public async Task Disabled_Returns404()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: false);
        using var client = app.RouterClient(app.Config.RouterKey); // key empty? config.RouterKey is empty when disabled? Actually we didn't set, so empty. But we still send whatever
        // Use wrong key anyway, but disabled should be checked first? spec says if disabled -> 404 even with correct key? But we test disabled with any key
        var resp = await client.PostAsJsonAsync("/router/v1/chat/completions", new { model = "any", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("router_disabled", body);
        // For anthropic shape, should be anthropic error shape
        using var client2 = app.RouterClient(app.Config.RouterKey);
        var resp2 = await client2.PostAsJsonAsync("/router/v1/messages", new { model = "any", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.NotFound, resp2.StatusCode);
        var body2 = await resp2.Content.ReadAsStringAsync();
        Assert.Contains("router_disabled", body2);
        Assert.Contains("\"type\":\"error\"", body2);
    }

    [Fact]
    public async Task WrongOrMissingKey_Returns401()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        var correct = app.Config.RouterKey;
        Assert.False(string.IsNullOrWhiteSpace(correct));
        // Missing key
        using var noAuth = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        var resp1 = await noAuth.PostAsJsonAsync("/router/v1/chat/completions", new { model = "any", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.Unauthorized, resp1.StatusCode);
        var b1 = await resp1.Content.ReadAsStringAsync();
        Assert.Contains("unauthorized", b1);
        // Wrong key
        using var wrong = app.RouterClient("wrong-key-123");
        var resp2 = await wrong.PostAsJsonAsync("/router/v1/chat/completions", new { model = "any", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode);
        // x-api-key variant also should work with correct key
        using var xapi = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        xapi.DefaultRequestHeaders.Add("x-api-key", correct);
        var resp3 = await xapi.PostAsJsonAsync("/router/v1/messages", new { model = "any", messages = new[] { new { role = "user", content = "hi" } } });
        // This will be 404 unknown_model or similar, but not 401 (since auth passes, but no upstream configured)
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp3.StatusCode);
        // Anthropic shape for messages should be anthropic error shape for unknown_model
        var b3 = await resp3.Content.ReadAsStringAsync();
        // Should be 404 unknown_model in anthropic shape
        Assert.Contains("unknown_model", b3);
    }

    [Fact]
    public async Task PresetCreate_DerivesModelWires_AndFetchListsModelsWithTheirWire()
    {
        var handler = new FakeHandler();
        handler.Enqueue(req =>
        {
            Assert.Equal("https://opencode.ai/zen/go/v1/models", req.RequestUri!.ToString());
            Assert.Equal("sk-go-12345678", req.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"minimax-m3\"},{\"id\":\"kimi-k3\"}]}", Encoding.UTF8, "application/json")
            };
        });
        await using var app = await TestApp.CreateAsync(routerEnabled: true, handler: handler);
        using var browser = app.Browser();

        var fetch = await browser.PostAsJsonAsync("/ui/api/router/models/fetch", new { preset_id = "opencode-go", api_key = "sk-go-12345678" });
        Assert.Equal(HttpStatusCode.OK, fetch.StatusCode);
        using (var doc = JsonDocument.Parse(await fetch.Content.ReadAsStringAsync()))
        {
            var models = doc.RootElement.GetProperty("models").EnumerateArray()
                .ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("wire").GetString()!);
            Assert.Equal(RouterWire.AnthropicMessages, models["minimax-m3"]);
            Assert.Equal(RouterWire.OpenAiChat, models["kimi-k3"]);
        }

        // No model_wires sent: the preset's rules supply them. A typed key still needs the acknowledgement.
        var refused = await browser.PostAsJsonAsync("/ui/api/router/upstreams", new
        {
            preset_id = "opencode-go", slug = "opencode-go", label = "OpenCode Go", wire = "openai_chat",
            base_url = "https://opencode.ai/zen/go/v1", api_key = "sk-go-12345678", models = new[] { "minimax-m3", "kimi-k3" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var created = await browser.PostAsJsonAsync("/ui/api/router/upstreams", new
        {
            preset_id = "opencode-go", slug = "opencode-go", label = "OpenCode Go", wire = "openai_chat",
            base_url = "https://opencode.ai/zen/go/v1", api_key = "sk-go-12345678", ack_key_storage = true,
            models = new[] { "minimax-m3", "kimi-k3" }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("api_key", body.RootElement.GetProperty("auth").GetString());
        Assert.True(body.RootElement.GetProperty("has_key").GetBoolean());
        var wires = body.RootElement.GetProperty("model_wires");
        Assert.Equal(RouterWire.AnthropicMessages, wires.GetProperty("minimax-m3").GetString());
        Assert.False(wires.TryGetProperty("kimi-k3", out _));
    }

    [Fact]
    public async Task OriginHeader_Returns403()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        using var client = app.RouterClient(app.Config.RouterKey);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.com");
        var resp = await client.PostAsJsonAsync("/router/v1/chat/completions", new { model = "any", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("forbidden", body.ToLower());
    }

    [Fact]
    public async Task ForeignHost_Returns421()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        using var client = app.RouterClient(app.Config.RouterKey);
        // Manually set Host header to evil.com
        var req = new HttpRequestMessage(HttpMethod.Post, app.BaseUrl + "/router/v1/chat/completions");
        req.Headers.Add("Authorization", "Bearer " + app.Config.RouterKey);
        req.Headers.Host = "evil.com";
        req.Content = JsonContent.Create(new { model = "any", messages = new[] { new { role = "user", content = "hi" } } });
        var resp = await client.SendAsync(req);
        Assert.Equal((HttpStatusCode)421, resp.StatusCode);
    }

    [Fact]
    public async Task BodyLargerThan64KiB_AcceptedUpToRouterLimit()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        // Create upstream for this model
        await app.RouterConfig.CreateUpstreamAsync("test", "Test", RouterWire.OpenAiChat, "https://api.example.com/v1", "sk-test-1234567890", ["my-model"]);
        var largeText = new string('a', 70 * 1024); // 70 KiB
        var body = new { model = "test/my-model", messages = new[] { new { role = "user", content = largeText } } };
        using var client = app.RouterClient(app.Config.RouterKey);
        var resp = await client.PostAsJsonAsync("/router/v1/chat/completions", body);
        // Should not be 413, should be 200 (proxied success)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var respBody = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ok", respBody);
    }

    [Fact]
    public async Task ModelsLists()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        await app.RouterConfig.CreateUpstreamAsync("openai", "OpenAI", RouterWire.OpenAiChat, "https://api.openai.com/v1", "sk-test-12345678", ["gpt-4o"]);
        await app.RouterConfig.CreateRouteAsync("my-alias", RouterKind.Alias, ["openai/gpt-4o"]);
        await app.RouterConfig.CreateRouteAsync("coding", RouterKind.Combo, ["openai/gpt-4o"]);
        using var client = app.RouterClient(app.Config.RouterKey);
        var resp = await client.GetAsync("/router/v1/models");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains("openai/gpt-4o", ids);
        Assert.Contains("my-alias", ids);
        Assert.Contains("coding", ids);
        Assert.Contains("combo/coding", ids);
    }

    [Fact]
    public async Task Management_EnableReturnsKeyOnce_AndGetNeverReturnsKey()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: false);
        using var browser = app.Browser();
        // Initially disabled
        var get1 = await browser.GetStringAsync("/ui/api/router");
        Assert.DoesNotContain("anr_", get1);
        // Enable without key generation? Use POST enable with enabled true
        var enableResp = await browser.PostAsJsonAsync("/ui/api/router/enable", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode);
        var enableText = await enableResp.Content.ReadAsStringAsync();
        Assert.Contains("anr_", enableText);
        var enableJson = JsonDocument.Parse(enableText).RootElement;
        Assert.True(enableJson.TryGetProperty("key", out var keyProp));
        var generatedKey = keyProp.GetString()!;
        Assert.StartsWith("anr_", generatedKey);
        // GET should not contain key
        var get2 = await browser.GetStringAsync("/ui/api/router");
        Assert.DoesNotContain(generatedKey, get2);
        Assert.Contains("has_key", get2);
        // Second enable should not return key
        var enable2 = await browser.PostAsJsonAsync("/ui/api/router/enable", new { enabled = true });
        var enable2Text = await enable2.Content.ReadAsStringAsync();
        Assert.DoesNotContain("anr_", enable2Text); // should not contain new key (since already has key)
        // Regenerate should return new key
        var regen = await browser.PostAsync("/ui/api/router/key/regenerate", null);
        Assert.Equal(HttpStatusCode.OK, regen.StatusCode);
        var regenText = await regen.Content.ReadAsStringAsync();
        Assert.Contains("anr_", regenText);
        var regenKey = JsonDocument.Parse(regenText).RootElement.GetProperty("key").GetString()!;
        Assert.NotEqual(generatedKey, regenKey);
        Assert.DoesNotContain(regenKey, await browser.GetStringAsync("/ui/api/router"));
    }

    [Fact]
    public async Task CsrfRequired_OnChanges()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        using var noCsrf = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        // No CSRF header
        var resp = await noCsrf.PostAsJsonAsync("/ui/api/router/enable", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task UpstreamCrud_Validation_AndNoKeyLeak()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        using var browser = app.Browser();
        const string secret = "sk-super-secret-1234567890abcdef";
        // Create without ack should fail
        var bad = await browser.PostAsJsonAsync("/ui/api/router/upstreams", new { slug = "openai", label = "OpenAI", wire = "openai_chat", base_url = "https://api.openai.com/v1", api_key = secret, models = new[] { "gpt-4o" }, enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        // With ack
        var good = await browser.PostAsJsonAsync("/ui/api/router/upstreams", new { slug = "openai", label = "OpenAI", wire = "openai_chat", base_url = "https://api.openai.com/v1", api_key = secret, ack_key_storage = true, models = new[] { "gpt-4o" }, enabled = true });
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        var goodText = await good.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, goodText);
        Assert.Contains("has_key", goodText);
        var id = JsonDocument.Parse(goodText).RootElement.GetProperty("id").GetString()!;
        // GET list should not contain secret
        var listText = await browser.GetStringAsync("/ui/api/router");
        Assert.DoesNotContain(secret, listText);
        // Update
        var upd = await browser.PutAsJsonAsync($"/ui/api/router/upstreams/{id}", new { slug = "openai", label = "OpenAI2", wire = "openai_chat", base_url = "https://api.openai.com/v1", models = new[] { "gpt-4o" }, enabled = true });
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        var updText = await upd.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, updText);
        // Invalid slug
        var bad2 = await browser.PostAsJsonAsync("/ui/api/router/upstreams", new { slug = "BadSlug", label = "X", wire = "openai_chat", base_url = "https://api.openai.com/v1", ack_key_storage = true, models = Array.Empty<string>(), enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad2.StatusCode);
        // Delete
        var del = await browser.DeleteAsync($"/ui/api/router/upstreams/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    [Fact]
    public async Task RouteCrud_AndDefaultRoute()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        using var browser = app.Browser();
        // Need upstreams first
        var up = await browser.PostAsJsonAsync("/ui/api/router/upstreams", new { slug = "openai", label = "OpenAI", wire = "openai_chat", base_url = "https://api.openai.com/v1", ack_key_storage = true, models = new[] { "gpt-4o" }, enabled = true });
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        // Create alias route
        var route = await browser.PostAsJsonAsync("/ui/api/router/routes", new { name = "my-alias", kind = "alias", targets = new[] { "openai/gpt-4o" }, enabled = true });
        Assert.Equal(HttpStatusCode.Created, route.StatusCode);
        var routeId = JsonDocument.Parse(await route.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        // Update
        var upd = await browser.PutAsJsonAsync($"/ui/api/router/routes/{routeId}", new { name = "my-alias2", kind = "alias", targets = new[] { "openai/gpt-4o" }, enabled = true });
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        // Set default
        var def = await browser.PutAsJsonAsync("/ui/api/router/default", new { route = "my-alias2" });
        Assert.Equal(HttpStatusCode.OK, def.StatusCode);
        var defBody = await def.Content.ReadAsStringAsync();
        Assert.Contains("my-alias2", defBody);
        // Clear default
        var clear = await browser.PutAsJsonAsync("/ui/api/router/default", new { route = (string?)null });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        // Delete route
        var del = await browser.DeleteAsync($"/ui/api/router/routes/{routeId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    [Fact]
    public async Task RequestsLedger_VisibleAfterProxiedCall()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        await app.RouterConfig.CreateUpstreamAsync("test", "Test", RouterWire.OpenAiChat, "https://api.example.com/v1", "sk-test-12345678", ["m"]);
        using var browser = app.Browser();
        using var router = app.RouterClient(app.Config.RouterKey);
        // Before, ledger empty
        var before = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router/requests?limit=50");
        var beforeCount = before.GetProperty("requests").GetArrayLength();
        // Make proxied call
        var resp = await router.PostAsJsonAsync("/router/v1/chat/completions", new { model = "test/m", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // After, ledger should have one more
        var after = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router/requests?limit=50");
        var afterCount = after.GetProperty("requests").GetArrayLength();
        Assert.Equal(beforeCount + 1, afterCount);
        var first = after.GetProperty("requests").EnumerateArray().First();
        Assert.True(first.GetProperty("request").TryGetProperty("upstream_slug", out _));
        Assert.True(first.GetProperty("attempts").GetArrayLength() >= 1);
        // Summary
        var summary = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router/summary?days=1");
        Assert.True(summary.TryGetProperty("summary", out _));
        // Invalid days
        var bad = await browser.GetAsync("/ui/api/router/summary?days=5");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task ClaudeCodeOwnModels_GoToAnthropicWithItsOwnSignIn_AndRoutedOnesUseTheProvidersKey()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        await app.RouterConfig.CreateUpstreamAsync("deepseek", "DeepSeek", RouterWire.OpenAiChat, "https://api.deepseek.com/v1", "sk-deepseek-12345678", ["deepseek-chat"]);
        await app.RouterConfig.SetDefaultRouteAsync("deepseek/deepseek-chat");

        // How Claude Code talks to the router once connected: its own sign-in in Authorization, the
        // router key in a header of its own.
        using var client = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        client.DefaultRequestHeaders.Add("Authorization", "Bearer sk-ant-oat01-client-own-token");
        client.DefaultRequestHeaders.Add(RouterNative.RouterKeyHeader, app.Config.RouterKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        client.DefaultRequestHeaders.Add("anthropic-beta", "claude-code-20250219,oauth-2025-04-20");
        client.DefaultRequestHeaders.Add("x-app", "cli");

        app.Handler.Enqueue(req =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages?beta=true", req.RequestUri!.ToString());
            Assert.Equal("Bearer sk-ant-oat01-client-own-token", req.Headers.GetValues("Authorization").Single());
            Assert.Equal("claude-code-20250219,oauth-2025-04-20", req.Headers.GetValues("anthropic-beta").Single());
            Assert.Equal("cli", req.Headers.GetValues("x-app").Single());
            Assert.False(req.Headers.Contains(RouterNative.RouterKeyHeader));
            Assert.False(req.Headers.Contains("x-api-key"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}", Encoding.UTF8, "application/json") };
        });
        var own = await client.PostAsJsonAsync("/router/v1/messages?beta=true", new { model = "claude-opus-5", max_tokens = 10, messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // A routed model from the same client uses the provider's key; Claude's sign-in stays home.
        app.Handler.Enqueue(req =>
        {
            Assert.StartsWith("https://api.deepseek.com/v1/", req.RequestUri!.ToString());
            Assert.Equal("sk-deepseek-12345678", req.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":123,\"model\":\"deepseek-chat\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}", Encoding.UTF8, "application/json") };
        });
        var routed = await client.PostAsJsonAsync("/router/v1/messages?beta=true", new { model = "deepseek/deepseek-chat", max_tokens = 10, messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);

        var ledger = await app.RouterRepo.ListRecentRequestsAsync(10);
        var native = ledger.Single(entry => entry.Request.RequestedModel == "claude-opus-5").Request;
        Assert.Equal(RouterRouteKind.Native, native.RouteKind);
        Assert.Equal("anthropic", native.UpstreamSlug);
    }

    [Fact]
    public async Task RouterKeyAsTheBearer_NeverSendsClaudeModelsToAnthropic()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        await app.RouterConfig.CreateUpstreamAsync("deepseek", "DeepSeek", RouterWire.OpenAiChat, "https://api.deepseek.com/v1", "sk-deepseek-12345678", ["deepseek-chat"]);
        await app.RouterConfig.SetDefaultRouteAsync("deepseek/deepseek-chat");
        using var client = app.RouterClient(app.Config.RouterKey);

        // With no credential of its own to forward, a Claude model is just an unrouted name.
        var resp = await client.PostAsJsonAsync("/router/v1/messages", new { model = "claude-opus-5", max_tokens = 10, messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("https://api.deepseek.com/v1/", app.Handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task WrongRouterKeyHeader_Returns401_EvenWithAClientSignIn()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + app.Config.RouterKey);
        client.DefaultRequestHeaders.Add(RouterNative.RouterKeyHeader, "not-the-key");
        var resp = await client.PostAsJsonAsync("/router/v1/messages", new { model = "claude-opus-5", max_tokens = 10, messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(app.Handler.Requests);
    }

    [Fact]
    public async Task SmartRouting_SwitchesOnFromThePage_AndListsWhatItCanSwitch()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        await app.RouterConfig.CreateUpstreamAsync("deepseek", "DeepSeek", RouterWire.OpenAiChat, "https://api.deepseek.com/v1", "sk-test-12345678", ["deepseek-v4-flash"]);
        await app.RouterConfig.CreateUpstreamAsync("opencode-go", "Go", RouterWire.OpenAiChat, "https://opencode.ai/zen/go/v1", "sk-go-12345678", ["deepseek-v4-flash", "kimi-k3"]);
        using var browser = app.Browser();

        var before = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router");
        Assert.False(before.GetProperty("smart_routing").GetBoolean());
        var group = before.GetProperty("smart_groups").EnumerateArray().Single();
        Assert.Equal("deepseek-v4-flash", group.GetProperty("model").GetString());
        Assert.Equal(["opencode-go/deepseek-v4-flash", "deepseek/deepseek-v4-flash"],
            group.GetProperty("targets").EnumerateArray().Select(t => t.GetString()));

        var put = await browser.PutAsJsonAsync("/ui/api/router/switch-settings", new { strategy = "ordered", claude_fallback_route = (string?)null });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router");
        Assert.True(after.GetProperty("smart_routing").GetBoolean());
        Assert.Equal("ordered", after.GetProperty("switch_strategy").GetString());
    }

    [Fact]
    public async Task CountTokens_ForwardOnlyToAnthropic()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        await app.RouterConfig.CreateUpstreamAsync("openai", "OpenAI", RouterWire.OpenAiChat, "https://api.openai.com/v1", "sk-test-12345678", ["gpt-4o"]);
        using var client = app.RouterClient(app.Config.RouterKey);
        // Request count_tokens with model that resolves to openai (non-anthropic) should 404 not_supported
        var resp = await client.PostAsJsonAsync("/router/v1/messages/count_tokens", new { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not_supported", body);
        // Now with anthropic upstream
        await app.RouterConfig.CreateUpstreamAsync("anthropic", "Anthropic", RouterWire.AnthropicMessages, "https://api.anthropic.com/v1", "sk-anthropic-1234567890", ["claude"]);
        app.Handler.Enqueue(req =>
        {
            Assert.EndsWith("/messages/count_tokens", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"type\":\"message\",\"usage\":{\"input_tokens\":5}}", Encoding.UTF8, "application/json") };
        });
        var resp2 = await client.PostAsJsonAsync("/router/v1/messages/count_tokens", new { model = "anthropic/claude", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
    }

    [Fact]
    public async Task EffortMappings_GroupByFamily_OverrideResetAndPrecedence()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        await app.RouterConfig.CreateUpstreamAsync("deepseek", "DeepSeek", RouterWire.OpenAiChat, "https://api.deepseek.com/v1", "sk-test-12345678", ["deepseek-v4", "deepseek-r2"]);
        await app.RouterConfig.CreateUpstreamAsync("openai", "OpenAI", RouterWire.OpenAiChat, "https://api.openai.com/v1", "sk-test-12345678", ["gpt-5"]);
        using var browser = app.Browser();

        var before = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router/effort-mappings");
        var deepseekFamily = before.GetProperty("families").EnumerateArray().Single(f => f.GetProperty("family").GetString() == "deepseek");
        Assert.Equal("automatic", deepseekFamily.GetProperty("source").GetString());
        Assert.Equal(2, deepseekFamily.GetProperty("models").GetArrayLength());
        Assert.Equal(["low", "medium", "high", "xhigh", "xhigh"],
            deepseekFamily.GetProperty("level_map").EnumerateArray().Select(v => v.GetString()));
        var first = before.GetProperty("mappings").EnumerateArray().Single(m => m.GetProperty("model").GetString() == "deepseek-v4");
        var upstreamId = first.GetProperty("upstream_id").GetString();
        Assert.Equal("inferred", first.GetProperty("source").GetString());

        var put = await browser.PutAsJsonAsync("/ui/api/router/effort-mappings", new
        {
            family = "deepseek",
            supported_values = new[] { "low", "medium", "high" },
            level_map = new[] { "low", "low", "medium", "high", "high" },
            default_value = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router/effort-mappings");
        Assert.Equal("family", after.GetProperty("families").EnumerateArray()
            .Single(f => f.GetProperty("family").GetString() == "deepseek").GetProperty("source").GetString());
        Assert.Equal("family", after.GetProperty("mappings").EnumerateArray()
            .Single(m => m.GetProperty("model").GetString() == "deepseek-v4").GetProperty("source").GetString());
        Assert.Equal("known", after.GetProperty("mappings").EnumerateArray()
            .Single(m => m.GetProperty("model").GetString() == "gpt-5").GetProperty("source").GetString());

        var modelPut = await browser.PutAsJsonAsync("/ui/api/router/effort-mappings", new
        {
            upstream_id = upstreamId,
            model = "deepseek-v4",
            supported_values = new[] { "low" },
            level_map = new[] { "low", "low", "low", "low", "low" },
            default_value = "low",
        });
        Assert.Equal(HttpStatusCode.OK, modelPut.StatusCode);
        var counted = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router/effort-mappings");
        Assert.Equal(1, counted.GetProperty("families").EnumerateArray()
            .Single(f => f.GetProperty("family").GetString() == "deepseek").GetProperty("model_override_count").GetInt32());

        var del = await browser.DeleteAsync("/ui/api/router/effort-mappings?family=deepseek");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var reset = await browser.GetFromJsonAsync<JsonElement>("/ui/api/router/effort-mappings");
        var backDeepseek = reset.GetProperty("families").EnumerateArray().Single(f => f.GetProperty("family").GetString() == "deepseek");
        Assert.Equal("automatic", backDeepseek.GetProperty("source").GetString());
        Assert.Equal(1, backDeepseek.GetProperty("model_override_count").GetInt32());

        var badFamily = await browser.PutAsJsonAsync("/ui/api/router/effort-mappings", new
        {
            family = "nosuch",
            supported_values = new[] { "low" },
            level_map = new[] { "low", "low", "low", "low", "low" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badFamily.StatusCode);
        var both = await browser.PutAsJsonAsync("/ui/api/router/effort-mappings", new
        {
            family = "deepseek",
            upstream_id = upstreamId,
            model = "deepseek-v4",
            supported_values = new[] { "low" },
            level_map = new[] { "low", "low", "low", "low", "low" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task ModelRouterPageAssetsAreServedAndRegistered()
    {
        await using var app = await TestApp.CreateAsync(routerEnabled: true);
        using var browser = app.Browser();

        foreach (var module in new[]
                 {
                     "router-shared.js", "router-providers.js", "router-routing.js",
                     "router-agents.js", "router-activity.js", "router-settings.js", "router-effort.js"
                 })
        {
            var asset = await browser.GetAsync($"/ui/js/views/{module}");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal("text/javascript", asset.Content.Headers.ContentType!.MediaType);
        }

        var appJs = await browser.GetStringAsync("/ui/js/app.js");
        Assert.Contains("Model router", appJs);
        foreach (var path in new[] { "router", "router-routing", "router-agents", "router-activity", "router-settings", "router-effort" })
            Assert.Contains($"path: \"{path}\"", appJs);
    }
}
