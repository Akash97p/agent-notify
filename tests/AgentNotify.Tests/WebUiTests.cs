using System.Net;
using System.Net.Http.Headers;
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
using AgentNotify.Core.Services;
using AgentNotify.Core.Usage;
using AgentNotify.Core.Quota;
using AgentNotify.Core.Wsl;
using Microsoft.AspNetCore.Builder;

namespace AgentNotify.Tests;

public sealed class WebUiTests : IAsyncLifetime
{
    private const string Secret = "https://hooks.example.com/very-secret-endpoint-7f3a";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"an-webui-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private DeliveryDispatcher _dispatcher = null!;
    private AgentNotifyConfig _config = null!;
    private int _port;
    private int _configSaves;
    private string Base => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _port = FreePort();
        var store = new ConfigStore(_dir, applyEnvOverrides: false);
        _config = new AgentNotifyConfig { Port = _port, AuthToken = "tok-" + Guid.NewGuid().ToString("N") };
        var db = Path.Combine(_dir, "agentnotify.db");
        var repository = new SqliteNotificationRepository(db);
        await repository.InitializeAsync();
        var interactionRepository = new SqliteInteractionRepository(db);
        await interactionRepository.InitializeAsync();
        var delivery = new SqliteDeliveryRepository(db);
        await delivery.InitializeAsync();
        var profiles = new ProviderProfileService(delivery, new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));
        _dispatcher = new DeliveryDispatcher(delivery, profiles, [], logger: null);

        var options = new WebUiOptions
        {
            ConfigStore = store,
            Providers = profiles,
            Routes = new DeliveryRouteService(delivery),
            Dispatcher = _dispatcher,
            Usage = new LocalUsageService([Path.Combine(_dir, "usage-claude")], [Path.Combine(_dir, "usage-codex")],
                Path.Combine(_dir, "usage-opencode.db")),
            Quota = new LiveQuotaService([new WebQuotaProbe()]),
            // Never the real discovery: a Windows test machine may have WSL distributions running.
            Wsl = new FakeWsl(new WslHome("Ubuntu-Test", "/home/tester", Path.Combine(_dir, "wsl-home"))),
            SecretProtection = "test protector",
            DesktopSurface = "test",
            ConfigSaved = (_, _) => Interlocked.Increment(ref _configSaves)
        };
        _app = ApiHost.Build(_config, repository, new NotificationService(repository, _config), url: Base,
            interactions: new InteractionService(interactionRepository), webUi: options);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        try { await _app.StopAsync(); } catch { }
        try { await _app.DisposeAsync(); } catch { }
        await _dispatcher.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private (HttpClient Client, CookieContainer Cookies) Browser()
    {
        var cookies = new CookieContainer();
        var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(Base)
        };
        return (client, cookies);
    }

    private HttpClient Bearer()
    {
        var client = new HttpClient { BaseAddress = new Uri(Base) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
        return client;
    }

    private HttpClient Page()
    {
        var (browser, _) = Browser();
        browser.DefaultRequestHeaders.Add(WebUiEndpoints.CsrfHeader, "1");
        return browser;
    }

    [Fact]
    public async Task UsagePageReportsLocalTokensWithoutExposingLogContents()
    {
        var dir = Path.Combine(_dir, "usage-claude");
        Directory.CreateDirectory(dir);
        var marker = "private-prompt-that-must-not-return";
        File.WriteAllText(Path.Combine(dir, "sample.jsonl"), JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = DateTimeOffset.UtcNow, cwd = _dir,
            message = new
            {
                id = "m1", model = "claude-opus-5", content = marker,
                usage = new { input_tokens = 12, output_tokens = 3 }
            }
        }) + Environment.NewLine);
        using var browser = Page();
        var response = await browser.GetAsync("/ui/api/usage?days=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(marker, text);
        Assert.DoesNotContain(_dir, text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(15, body.GetProperty("totals").GetProperty("total").GetInt64());
        Assert.Equal(0.000135m, body.GetProperty("cost").GetProperty("priced_usd").GetDecimal());
        Assert.Equal("2026-09-17", body.GetProperty("pricing_as_of").GetString());
        Assert.Equal("3", body.GetProperty("contract_version").GetString());
        Assert.Single(body.GetProperty("projects").EnumerateArray());
        Assert.Equal("claude_code", body.GetProperty("sources")[0].GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync("/ui/api/usage?days=1")).StatusCode);
    }

    [Fact]
    public async Task UsagePageIncludesOpenCodeProviderAndProjectWithoutMessageText()
    {
        var path = Path.Combine(_dir, "usage-opencode.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT);
                CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT);
                INSERT INTO session VALUES ('s1', $project);
                INSERT INTO message VALUES ('m1', 's1', $time, $data);
                """;
            command.Parameters.AddWithValue("$project", Path.Combine(_dir, "opencode-project"));
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new
            {
                role = "assistant", providerID = "openai", modelID = "gpt-5.6-sol",
                content = "private-opencode-response", tokens = new { input = 10, output = 2, reasoning = 1 }
            }));
            command.ExecuteNonQuery();
        }
        using var browser = Page();
        var response = await browser.GetAsync("/ui/api/usage?days=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-opencode-response", text);
        Assert.DoesNotContain(_dir, text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("opencode", body.GetProperty("sources")[0].GetProperty("source").GetString());
        Assert.Equal("openai", body.GetProperty("models")[0].GetProperty("provider").GetString());
        Assert.Equal("opencode-project", body.GetProperty("projects")[0].GetProperty("name").GetString());
        Assert.Equal(0.0001m, body.GetProperty("cost").GetProperty("priced_usd").GetDecimal());
    }

    [Fact]
    public async Task QuotaPageReturnsAccountWindowsWithoutSecretsAndRequiresUiHeaderToRefresh()
    {
        using var browser = Page();
        var response = await browser.GetAsync("/ui/api/quota");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("fixture-secret", text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("codex", body.GetProperty("providers")[0].GetProperty("provider").GetString());
        Assert.Equal(24, body.GetProperty("providers")[0].GetProperty("windows")[0].GetProperty("used_percent").GetDouble());
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/ui/api/quota/refresh", JsonContent.Create(new { }))).StatusCode);
        using var otherPage = Browser().Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await otherPage.PostAsync("/ui/api/quota/refresh", JsonContent.Create(new { }))).StatusCode);
    }

    [Fact]
    public async Task AdditionalQuotaAccountCanBeAddedListedAndRemovedWithoutCredentials()
    {
        using var browser = Page();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-agentnotify-test-" + Guid.NewGuid().ToString("N"));
        var added = await browser.PostAsJsonAsync("/ui/api/quota/accounts",
            new { provider = "codex", label = "Second", directory });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.StartsWith("q_", id);
        var listed = await browser.GetFromJsonAsync<JsonElement>("/ui/api/quota/accounts");
        Assert.Equal(3, listed.GetProperty("accounts").GetArrayLength());
        Assert.Equal("Second", listed.GetProperty("accounts")[2].GetProperty("label").GetString());
        Assert.Single(new ConfigStore(_dir, applyEnvOverrides: false).Load().QuotaAccounts);

        Assert.Equal(HttpStatusCode.OK, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/codex:default",
            new { label = "Primary Codex" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/" + id,
            new { label = "Backup Codex" })).StatusCode);
        listed = await browser.GetFromJsonAsync<JsonElement>("/ui/api/quota/accounts");
        Assert.Equal("Primary Codex", listed.GetProperty("accounts")[0].GetProperty("label").GetString());
        Assert.Equal("Backup Codex", listed.GetProperty("accounts")[2].GetProperty("label").GetString());
        var saved = new ConfigStore(_dir, applyEnvOverrides: false).Load();
        Assert.Equal("Primary Codex", saved.DefaultQuotaAccountLabels["codex"]);
        Assert.Equal("Backup Codex", Assert.Single(saved.QuotaAccounts).Label);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/" + id,
            new { label = "   " })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/ui/api/quota/accounts",
            new { provider = "codex", label = "Duplicate", directory })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.DeleteAsync("/ui/api/quota/accounts/" + id)).StatusCode);
        Assert.Empty(new ConfigStore(_dir, applyEnvOverrides: false).Load().QuotaAccounts);
    }

    [Fact]
    public async Task DetectedQuotaAccountsCanBeRemovedRestoredAndMoved()
    {
        using var browser = Page();
        Directory.CreateDirectory(Path.Combine(_dir, "wsl-home", ".claude"));
        static string[] Ids(JsonElement list, string property) =>
            list.GetProperty(property).EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();

        var listed = await browser.GetFromJsonAsync<JsonElement>("/ui/api/quota/accounts");
        Assert.Equal(["codex:default", "claude_code:default", "claude_code:wsl:Ubuntu-Test"], Ids(listed, "accounts"));

        Assert.Equal(HttpStatusCode.OK, (await browser.DeleteAsync("/ui/api/quota/accounts/claude_code:wsl:Ubuntu-Test")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.DeleteAsync("/ui/api/quota/accounts/claude_code:wsl:Ubuntu-Test")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/claude_code:wsl:Ubuntu-Test",
            new { label = "Hidden" })).StatusCode);
        listed = await browser.GetFromJsonAsync<JsonElement>("/ui/api/quota/accounts");
        Assert.Equal(["codex:default", "claude_code:default"], Ids(listed, "accounts"));
        var removed = Assert.Single(listed.GetProperty("removed").EnumerateArray());
        Assert.Equal("Ubuntu-Test", removed.GetProperty("wsl").GetString());
        Assert.Equal(["claude_code:wsl:Ubuntu-Test"], new ConfigStore(_dir, applyEnvOverrides: false).Load().RemovedQuotaAccounts);

        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/ui/api/quota/accounts/claude_code:wsl:Ubuntu-Test/restore", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsJsonAsync("/ui/api/quota/accounts/claude_code:wsl:Ubuntu-Test/restore", new { })).StatusCode);
        listed = await browser.GetFromJsonAsync<JsonElement>("/ui/api/quota/accounts");
        Assert.Equal(3, listed.GetProperty("accounts").GetArrayLength());
        Assert.Empty(listed.GetProperty("removed").EnumerateArray());

        // Saving an unchanged directory only renames; a new directory turns the built-in account into an added one.
        var current = listed.GetProperty("accounts")[0].GetProperty("directory").GetString();
        Assert.Equal(HttpStatusCode.OK, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/codex:default",
            new { label = "Windows Codex", directory = current })).StatusCode);
        Assert.Empty(new ConfigStore(_dir, applyEnvOverrides: false).Load().QuotaAccounts);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-agentnotify-test-" + Guid.NewGuid().ToString("N"));
        var moved = await browser.PutAsJsonAsync("/ui/api/quota/accounts/codex:default",
            new { label = "Moved Codex", directory });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var movedId = (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.StartsWith("q_", movedId);
        listed = await browser.GetFromJsonAsync<JsonElement>("/ui/api/quota/accounts");
        Assert.Equal(["claude_code:default", "claude_code:wsl:Ubuntu-Test", movedId], Ids(listed, "accounts"));
        Assert.Equal(["codex:default"], Ids(listed, "removed"));
        var saved = new ConfigStore(_dir, applyEnvOverrides: false).Load();
        Assert.Equal(directory, Assert.Single(saved.QuotaAccounts).Directory);

        // An added account can be moved in place, but not onto a directory another account monitors.
        var other = directory + "-other";
        Assert.Equal(HttpStatusCode.OK, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/" + movedId,
            new { label = "Moved Codex", directory = other })).StatusCode);
        Assert.Equal(other, Assert.Single(new ConfigStore(_dir, applyEnvOverrides: false).Load().QuotaAccounts).Directory);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/claude_code:default",
            new { label = "Clash", directory = Path.Combine(_dir, "wsl-home", ".claude") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PutAsJsonAsync("/ui/api/quota/accounts/" + movedId,
            new { label = "Moved Codex", directory = "relative/path" })).StatusCode);
    }

    [Fact]
    public async Task SkillsCanBeInstalledIntoARunningWslHomeByDistributionName()
    {
        using var browser = Page();
        var agents = await browser.GetFromJsonAsync<JsonElement>("/ui/api/agents");
        var wslClaude = agents.GetProperty("skills").EnumerateArray()
            .Single(skill => skill.GetProperty("id").GetString() == "claude" &&
                             skill.GetProperty("wsl").ValueKind == JsonValueKind.String);
        Assert.Equal("Ubuntu-Test", wslClaude.GetProperty("wsl").GetString());
        Assert.Equal("WSL · Ubuntu-Test", wslClaude.GetProperty("environment").GetString());
        var expected = Path.Combine(_dir, "wsl-home", ".claude", "skills", "agentnotify");
        Assert.Equal(expected, wslClaude.GetProperty("destination").GetString());

        var installed = await browser.PostAsJsonAsync("/ui/api/agents/skills/claude", new { force = false, wsl = "Ubuntu-Test" });
        Assert.Equal(HttpStatusCode.OK, installed.StatusCode);
        Assert.True(File.Exists(Path.Combine(expected, "SKILL.md")));
        var skill = (await installed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("skill");
        Assert.Equal("up_to_date", skill.GetProperty("state").GetString());
        Assert.Equal("Ubuntu-Test", skill.GetProperty("wsl").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsJsonAsync("/ui/api/agents/skills/claude",
            new { force = false, wsl = "Stopped-Distro" })).StatusCode);
    }

    private sealed class WebQuotaProbe : ILiveQuotaProbe
    {
        public string Provider => "codex";
        public string ScopeKey() => "fixture";
        public Task<LiveQuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(new LiveQuotaSnapshot("codex", "ok", "fixture", now, "pro", null,
                [new LiveQuotaWindow("session", "5-hour", 24, 76, 300, now.AddHours(5))], null));
    }

    [Fact]
    public async Task NeedsNoSignInOrToken()
    {
        var (browser, _) = Browser();
        var overview = await browser.GetAsync("/ui/api/overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Equal("test protector", (await overview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret_protection").GetString());

        // The agent API keeps its bearer token; only the page is open to this machine.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/v1/notifications")).StatusCode);
    }

    [Fact]
    public async Task ForeignHostNamesAreRefused()
    {
        var browser = Page();
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/ui/api/overview");
        rebinding.Headers.Host = $"attacker.example:{_port}";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await browser.SendAsync(rebinding)).StatusCode);

        using var page = new HttpRequestMessage(HttpMethod.Get, "/ui/");
        page.Headers.Host = $"attacker.example:{_port}";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await browser.SendAsync(page)).StatusCode);
    }

    [Fact]
    public async Task SshForwardedLoopbackPortServesPageAndAcceptsOnlyItsOwnOrigin()
    {
        using var browser = Page();
        const int forwardedPort = 47822;
        var forwardedHost = $"127.0.0.1:{forwardedPort}";

        using var page = new HttpRequestMessage(HttpMethod.Get, "/ui/");
        page.Headers.Host = forwardedHost;
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(page)).StatusCode);

        using var read = new HttpRequestMessage(HttpMethod.Get, "/ui/api/overview");
        read.Headers.Host = forwardedHost;
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(read)).StatusCode);

        using var wrongOrigin = new HttpRequestMessage(HttpMethod.Put, "/ui/api/settings")
        { Content = JsonContent.Create(new { pause_notifications = true }) };
        wrongOrigin.Headers.Host = forwardedHost;
        wrongOrigin.Headers.Add("Origin", Base);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(wrongOrigin)).StatusCode);
        Assert.False(_config.PauseNotifications);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Put, "/ui/api/settings")
        { Content = JsonContent.Create(new { pause_notifications = true }) };
        sameOrigin.Headers.Host = forwardedHost;
        sameOrigin.Headers.Add("Origin", $"http://{forwardedHost}");
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(sameOrigin)).StatusCode);
        Assert.True(_config.PauseNotifications);
    }

    [Fact]
    public async Task StateChangesNeedTheUiHeaderAndASameOriginOrigin()
    {
        var browser = Page();
        browser.DefaultRequestHeaders.Remove(WebUiEndpoints.CsrfHeader);
        var body = JsonContent.Create(new { pause_notifications = true });

        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PutAsync("/ui/api/settings", body)).StatusCode);

        using var crossSite = new HttpRequestMessage(HttpMethod.Put, "/ui/api/settings") { Content = JsonContent.Create(new { pause_notifications = true }) };
        crossSite.Headers.Add(WebUiEndpoints.CsrfHeader, "1");
        crossSite.Headers.Add("Origin", "https://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(crossSite)).StatusCode);
        Assert.False(_config.PauseNotifications);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Put, "/ui/api/settings") { Content = JsonContent.Create(new { pause_notifications = true }) };
        sameOrigin.Headers.Add(WebUiEndpoints.CsrfHeader, "1");
        sameOrigin.Headers.Add("Origin", Base);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(sameOrigin)).StatusCode);
        Assert.True(_config.PauseNotifications);
    }

    [Fact]
    public async Task InvalidSettingsChangeNothing()
    {
        var browser = Page();
        var response = await browser.PutAsJsonAsync("/ui/api/settings", new { history_retention_days = 90, port = 70000 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(30, _config.HistoryRetentionDays);
        Assert.Equal(0, _configSaves);

        var saved = await browser.PutAsJsonAsync("/ui/api/settings", new { history_retention_days = 90, toast_durations = new Dictionary<string, int> { ["error"] = 0 } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(90, _config.HistoryRetentionDays);
        Assert.Equal(0, _config.ToastDurationSeconds("error"));
        Assert.Equal(1, _configSaves);
        Assert.Equal(90, new ConfigStore(_dir, applyEnvOverrides: false).Load().HistoryRetentionDays);
    }

    [Fact]
    public async Task StoredSecretsNeverComeBack()
    {
        var browser = Page();
        var created = await browser.PostAsJsonAsync("/ui/api/providers", new
        {
            name = "Hook",
            kind = "webhook",
            enabled = true,
            values = new Dictionary<string, string> { ["allow_private_network"] = "false" },
            secrets = new Dictionary<string, string> { ["endpoint_url"] = Secret, ["hmac_secret"] = "hmac-value-xyz" }
        });
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("\"secret_names\"", createdText);
        Assert.DoesNotContain(Secret, createdText);
        Assert.DoesNotContain("hmac-value-xyz", createdText);

        var listed = await browser.GetStringAsync("/ui/api/providers");
        Assert.DoesNotContain(Secret, listed);
        Assert.Contains("endpoint_url", listed);

        var invalid = await browser.PostAsJsonAsync("/ui/api/providers", new { name = "Empty", kind = "webhook", enabled = true });
        var invalidText = await invalid.Content.ReadAsStringAsync();
        // Also guards against the handler binding to RequestDelegate, which answers 200 with no body.
        Assert.True(invalid.StatusCode == HttpStatusCode.BadRequest, $"{(int)invalid.StatusCode}: {invalidText}");
        Assert.Contains("webhook HTTPS endpoint", invalidText);
    }

    [Fact]
    public async Task RoutesSaveAndRefuseAnUnknownChannel()
    {
        var browser = Page();
        var provider = await (await browser.PostAsJsonAsync("/ui/api/providers", new
        {
            name = "Hook", kind = "webhook", enabled = true,
            secrets = new Dictionary<string, string> { ["endpoint_url"] = Secret }
        })).Content.ReadFromJsonAsync<JsonElement>();

        var saved = await browser.PostAsJsonAsync("/ui/api/routes", new
        {
            name = "Urgent", provider_id = provider.GetProperty("id").GetString(), enabled = true, minimum_priority = "high", include_message = false
        });
        var route = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("high", route.GetProperty("minimum_priority").GetString());
        Assert.False(route.GetProperty("include_message").GetBoolean());

        var orphan = await browser.PostAsJsonAsync("/ui/api/routes", new { name = "Nowhere", provider_id = "missing", minimum_priority = "low" });
        Assert.Equal(HttpStatusCode.BadRequest, orphan.StatusCode);
        Assert.Contains("existing provider", await orphan.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnsweringFromTheBrowserBindsToTheQuestionTheUserSaw()
    {
        using var bearer = Bearer();
        var asked = await bearer.PostAsJsonAsync("/v1/interactions/request", new
        {
            agent = "claude",
            project = "agent-notify",
            kind = "single_choice",
            prompt = "Which database?",
            choices = new[] { new { id = "pg", label = "PostgreSQL" }, new { id = "lite", label = "SQLite" } },
            ttl_seconds = 600
        });
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);

        var browser = Page();
        var pending = await browser.GetFromJsonAsync<JsonElement>("/ui/api/interactions");
        var question = Assert.Single(pending.EnumerateArray());
        Assert.Equal("", question.GetProperty("nonce").GetString());
        var id = question.GetProperty("id").GetString();

        var stale = await browser.PostAsJsonAsync($"/ui/api/interactions/{id}/respond", new { request_digest = new string('0', 64), choice_id = "pg" });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var answered = await browser.PostAsJsonAsync($"/ui/api/interactions/{id}/respond",
            new { request_digest = question.GetProperty("request_digest").GetString(), choice_id = "pg" });
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        var settled = await bearer.GetFromJsonAsync<JsonElement>($"/v1/interactions/{id}");
        Assert.Equal("answered", settled.GetProperty("status").GetString());
        Assert.Equal("pg", settled.GetProperty("response").GetProperty("choice_id").GetString());
        Assert.Equal("web", settled.GetProperty("response").GetProperty("source").GetString());
    }

    [Fact]
    public async Task ServesThePageWithALockedDownPolicy()
    {
        var (browser, _) = Browser();
        var page = await browser.GetAsync("/ui/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var policy = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.DoesNotContain("unsafe-inline", policy);

        var script = await browser.GetAsync("/ui/js/app.js");
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/ui/js/missing.js")).StatusCode);
        Assert.Equal("text/html", (await browser.GetAsync("/ui/channels")).Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/ui/api/nothing-here")).StatusCode);
    }

    [Fact]
    public async Task EveryModuleThePageImportsIsServed()
    {
        var (browser, _) = Browser();
        var pending = new Queue<string>(["js/app.js"]);
        var seen = new HashSet<string>();
        while (pending.Count > 0)
        {
            var path = pending.Dequeue();
            if (!seen.Add(path)) continue;
            var response = await browser.GetAsync($"/ui/{path}");
            Assert.True(response.IsSuccessStatusCode, $"{path} is referenced but not served");
            var source = await response.Content.ReadAsStringAsync();
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(source, "from \"(\\.{1,2}/[^\"]+)\""))
            {
                var resolved = new Uri(new Uri($"http://x/ui/{path}"), match.Groups[1].Value).AbsolutePath["/ui/".Length..];
                pending.Enqueue(resolved);
            }
        }
        Assert.True(seen.Count > 10);
    }

    [Fact]
    public async Task UploadedSoundsAreImportedAndPlayable()
    {
        var browser = Page();
        using var form = new MultipartFormDataContent();
        var wav = new ByteArrayContent(Encoding.ASCII.GetBytes("RIFF----WAVEfmt fake"));
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(wav, "file", "../../Build Done!.wav");

        var upload = await browser.PostAsync("/ui/api/sounds", form);
        var body = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var name = body.GetProperty("file_name").GetString()!;
        Assert.StartsWith("Build-Done-", name);
        Assert.True(File.Exists(Path.Combine(_dir, "sounds", name)));

        var played = await browser.GetAsync($"/ui/api/sounds/{name}");
        Assert.Equal("audio/wav", played.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/ui/api/sounds/..%2Fconfig.json")).StatusCode);
    }
}
