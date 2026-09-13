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

    private async Task<HttpClient> SignedInBrowser()
    {
        var (browser, _) = Browser();
        using var bearer = Bearer();
        var launch = await bearer.PostAsync("/v1/ui/launch", null);
        var url = new Uri((await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!);
        var redeemed = await browser.GetAsync(url.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
        browser.DefaultRequestHeaders.Add(WebUiEndpoints.CsrfHeader, "1");
        return browser;
    }

    [Fact]
    public async Task LaunchCodesNeedTheBearerTokenAndWorkOnce()
    {
        var (anonymous, _) = Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v1/ui/launch", null)).StatusCode);

        using var bearer = Bearer();
        var body = await (await bearer.PostAsync("/v1/ui/launch", null)).Content.ReadFromJsonAsync<JsonElement>();
        var url = new Uri(body.GetProperty("url").GetString()!);
        Assert.Equal("127.0.0.1", url.Host);
        Assert.DoesNotContain(_config.AuthToken, url.AbsoluteUri);

        var first = await anonymous.GetAsync(url.PathAndQuery);
        Assert.Equal("/ui/", first.Headers.Location!.OriginalString);
        var cookie = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/ui", cookie, StringComparison.OrdinalIgnoreCase);

        var (other, _) = Browser();
        var replay = await other.GetAsync(url.PathAndQuery);
        Assert.Contains("signin", replay.Headers.Location!.OriginalString);
        Assert.False(replay.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ApiNeedsASession()
    {
        var (browser, _) = Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui/api/overview")).StatusCode);

        var session = await browser.GetFromJsonAsync<JsonElement>("/ui/api/session");
        Assert.False(session.GetProperty("authenticated").GetBoolean());

        var signedIn = await SignedInBrowser();
        var overview = await signedIn.GetFromJsonAsync<JsonElement>("/ui/api/overview");
        Assert.Equal("test protector", overview.GetProperty("secret_protection").GetString());
    }

    [Fact]
    public async Task ForeignHostNamesAreRefused()
    {
        var browser = await SignedInBrowser();
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/ui/api/overview");
        rebinding.Headers.Host = $"attacker.example:{_port}";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await browser.SendAsync(rebinding)).StatusCode);

        using var page = new HttpRequestMessage(HttpMethod.Get, "/ui/");
        page.Headers.Host = $"attacker.example:{_port}";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await browser.SendAsync(page)).StatusCode);
    }

    [Fact]
    public async Task StateChangesNeedTheUiHeaderAndASameOriginOrigin()
    {
        var browser = await SignedInBrowser();
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
    public async Task TokenSignInIsThrottled()
    {
        var (browser, _) = Browser();
        browser.DefaultRequestHeaders.Add(WebUiEndpoints.CsrfHeader, "1");
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/ui/api/session", new { token = "wrong" })).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.PostAsJsonAsync("/ui/api/session", new { token = _config.AuthToken })).StatusCode);
    }

    [Fact]
    public async Task TokenSignInStartsASession()
    {
        var (browser, _) = Browser();
        browser.DefaultRequestHeaders.Add(WebUiEndpoints.CsrfHeader, "1");
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/ui/api/session", new { token = _config.AuthToken })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/ui/api/settings")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.DeleteAsync("/ui/api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui/api/settings")).StatusCode);
    }

    [Fact]
    public async Task InvalidSettingsChangeNothing()
    {
        var browser = await SignedInBrowser();
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
        var browser = await SignedInBrowser();
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
        var browser = await SignedInBrowser();
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

        var browser = await SignedInBrowser();
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui/api/nothing-here")).StatusCode);
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
        var browser = await SignedInBrowser();
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
