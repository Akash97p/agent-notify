using System.Net;
using System.Net.Http.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Tests;

public sealed class DedupTests
{
    [Fact]
    public async Task SameKey_UpdatesInPlace()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var key = "dedup-" + Guid.NewGuid().ToString("N");

        var r1 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "first", message = "m1", key }, Json.Options);
        var d1 = await r1.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        var r2 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "second", message = "m2", key }, Json.Options);
        var d2 = await r2.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        Assert.Equal(d1!.Id, d2!.Id);
        Assert.Equal("second", d2.Title);
        Assert.Equal("m2", d2.Message);
    }

    [Fact]
    public async Task DifferentKeys_CreateSeparate()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();

        var r1 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "a", message = "m", key = "k-" + Guid.NewGuid().ToString("N") }, Json.Options);
        var r2 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "b", message = "m", key = "k-" + Guid.NewGuid().ToString("N") }, Json.Options);
        var d1 = await r1.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        var d2 = await r2.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.NotEqual(d1!.Id, d2!.Id);
    }

    [Fact]
    public async Task NoKey_AlwaysCreatesNew()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var r1 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "a", message = "m" }, Json.Options);
        var r2 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "a", message = "m" }, Json.Options);
        var d1 = await r1.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        var d2 = await r2.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.NotEqual(d1!.Id, d2!.Id);
    }

    [Fact]
    public async Task ResolvedKey_AllowsNewNotification()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var key = "reuse-" + Guid.NewGuid().ToString("N");

        var r1 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "first", message = "m", key }, Json.Options);
        var d1 = await r1.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        var patch = new HttpRequestMessage(new HttpMethod("PATCH"), $"{fx.BaseUrl}/v1/notifications/{d1!.Id}")
        {
            Content = JsonContent.Create(new { status = "resolved" }, options: Json.Options)
        };
        await c.SendAsync(patch);

        var r2 = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "again", message = "m", key }, Json.Options);
        var d2 = await r2.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.NotEqual(d1.Id, d2!.Id);
    }

    [Fact]
    public async Task ConcurrentSameKey_CreatesOneActiveNotification()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var key = "concurrent-" + Guid.NewGuid().ToString("N");
        var requests = Enumerable.Range(0, 12).Select(i =>
            c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
                new { title = $"update {i}", message = "m", key }, Json.Options));
        var responses = await Task.WhenAll(requests);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var active = await fx.Repository.QueryAsync(new AgentNotify.Core.Persistence.NotificationQuery
        {
            Unresolved = true,
            Limit = 500
        });
        Assert.Single(active, n => n.Key == key);
    }
}
