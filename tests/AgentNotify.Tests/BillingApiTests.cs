using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Api;
using AgentNotify.Api.WebUi;
using AgentNotify.Core.Billing;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;
using Microsoft.AspNetCore.Builder;

namespace AgentNotify.Tests;

public sealed class BillingApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"an-billing-api-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private DeliveryDispatcher _dispatcher = null!;
    private int _port;
    private string Base => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _port = FreePort();
        var store = new ConfigStore(_dir, applyEnvOverrides: false);
        var config = new AgentNotifyConfig { Port = _port, AuthToken = "tok-" + Guid.NewGuid().ToString("N") };
        var db = Path.Combine(_dir, "agentnotify.db");
        var repository = new SqliteNotificationRepository(db);
        await repository.InitializeAsync();
        var interactions = new SqliteInteractionRepository(db);
        await interactions.InitializeAsync();
        var delivery = new SqliteDeliveryRepository(db);
        await delivery.InitializeAsync();
        var profiles = new ProviderProfileService(delivery, new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));
        _dispatcher = new DeliveryDispatcher(delivery, profiles, [], logger: null);
        var billingRepository = new BillingAccountRepository(db);
        await billingRepository.InitializeAsync();
        var billing = new BillingService(billingRepository,
            new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)),
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"5.00"}]}""",
                    Encoding.UTF8, "application/json")
            }));
        var options = new WebUiOptions
        {
            ConfigStore = store,
            Providers = profiles,
            Routes = new DeliveryRouteService(delivery),
            Dispatcher = _dispatcher,
            Billing = billing,
            SecretProtection = "test protector",
            DesktopSurface = "test"
        };
        _app = ApiHost.Build(config, repository, new NotificationService(repository, config), url: Base,
            interactions: new InteractionService(interactions), webUi: options);
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

    private HttpClient Page()
    {
        var client = new HttpClient { BaseAddress = new Uri(Base) };
        client.DefaultRequestHeaders.Add(WebUiEndpoints.CsrfHeader, "1");
        return client;
    }

    [Fact]
    public async Task BillingCrudRequiresAcknowledgementAndNeverReturnsKeys()
    {
        using var browser = Page();
        var empty = await browser.GetFromJsonAsync<JsonElement>("/ui/api/billing");
        Assert.Equal("1", empty.GetProperty("contract_version").GetString());
        Assert.Empty(empty.GetProperty("accounts").EnumerateArray());

        var catalog = await browser.GetFromJsonAsync<JsonElement>("/ui/api/billing/accounts");
        Assert.Equal(6, catalog.GetProperty("providers").GetArrayLength());
        Assert.Contains(catalog.GetProperty("providers").EnumerateArray(),
            p => p.GetProperty("id").GetString() == "deepseek" && p.GetProperty("host").GetString() == "api.deepseek.com");
        Assert.Contains(catalog.GetProperty("providers").EnumerateArray(),
            p => p.GetProperty("id").GetString() == "openai_admin" && p.GetProperty("key_type").GetString() == "Admin key");

        const string secret = "billing-secret-value-abcdef1234567890";
        var denied = await browser.PostAsJsonAsync("/ui/api/billing/accounts",
            new { provider = "deepseek", label = "Main", api_key = secret, acknowledge_risk = false });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        var missing = await browser.PostAsJsonAsync("/ui/api/billing/accounts",
            new { provider = "deepseek", label = "Main", api_key = secret });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var created = await browser.PostAsJsonAsync("/ui/api/billing/accounts",
            new { provider = "deepseek", label = "Main", api_key = secret, acknowledge_risk = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, createdText);
        var item = JsonDocument.Parse(createdText).RootElement;
        var id = item.GetProperty("id").GetString()!;
        Assert.StartsWith("b_", id);
        Assert.True(item.GetProperty("has_key").GetBoolean());

        var listedText = await browser.GetStringAsync("/ui/api/billing/accounts");
        Assert.DoesNotContain(secret, listedText);

        var reportText = await browser.GetStringAsync("/ui/api/billing");
        Assert.DoesNotContain(secret, reportText);
        var report = JsonDocument.Parse(reportText).RootElement;
        var snapshot = Assert.Single(report.GetProperty("accounts").EnumerateArray());
        Assert.Equal("ok", snapshot.GetProperty("status").GetString());
        Assert.True(snapshot.GetProperty("available").GetBoolean());

        var renamed = await browser.PutAsJsonAsync($"/ui/api/billing/accounts/{id}", new { label = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedText = await renamed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, renamedText);

        var replaced = await browser.PutAsJsonAsync($"/ui/api/billing/accounts/{id}",
            new { label = "Renamed", api_key = "replacement-key-1234567890abcdef" });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.DoesNotContain("replacement-key-1234567890abcdef", await replaced.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await browser.DeleteAsync($"/ui/api/billing/accounts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.DeleteAsync($"/ui/api/billing/accounts/{id}")).StatusCode);
        Assert.Empty((await browser.GetFromJsonAsync<JsonElement>("/ui/api/billing/accounts")).GetProperty("accounts").EnumerateArray());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
