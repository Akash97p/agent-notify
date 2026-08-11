using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using AgentNotify.Api;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;
using Microsoft.AspNetCore.Builder;

namespace AgentNotify.Tests;

internal sealed class ApiFixture : IAsyncDisposable
{
    public string Token { get; private set; } = "";
    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public SqliteNotificationRepository Repository { get; private set; } = null!;
    public WebApplication App { get; private set; } = null!;
    public ApiCallbacks Callbacks { get; private set; } = null!;
    private string _dbPath = "";

    public HttpClient AuthedClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return c;
    }

    public HttpClient ClientWithToken(string? token)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<ApiFixture> StartAsync()
    {
        var port = GetFreePort();
        var dbPath = Path.Combine(Path.GetTempPath(), $"an-api-{Guid.NewGuid():N}.db");
        var token = "tok-" + Guid.NewGuid().ToString("N");
        var config = new AgentNotifyConfig { Port = port, AuthToken = token };
        var repo = new SqliteNotificationRepository(dbPath);
        await repo.InitializeAsync();
        var service = new NotificationService(repo, config);
        var callbacks = new ApiCallbacks();
        var app = ApiHost.Build(config, repo, service, logger: null, url: $"http://127.0.0.1:{port}", callbacks: callbacks);
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilReady(port);
        return new ApiFixture { Port = port, _dbPath = dbPath, Repository = repo, App = app, Token = token, Callbacks = callbacks };
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitUntilReady(int port)
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var i = 0; i < 20; i++)
        {
            try { var r = await c.GetAsync($"http://127.0.0.1:{port}/health"); if (r.IsSuccessStatusCode) return; } catch { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"API did not become ready on port {port}.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await App.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }
}
