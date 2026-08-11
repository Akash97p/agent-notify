using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task Create_Minimal()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var resp = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "hello", message = "world" }, Json.Options);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.NotNull(dto);
        Assert.Equal("hello", dto!.Title);
        Assert.Equal(NotificationStatus.Active, dto.Status);
    }

    [Fact]
    public async Task Create_Invalid_MissingTitle_Returns400()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var resp = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "", message = "x" }, Json.Options);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidJson_Returns400()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var resp = await c.PostAsync($"{fx.BaseUrl}/v1/notifications",
            new StringContent("{ not valid json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_NullBody_Returns400()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var resp = await c.PostAsync($"{fx.BaseUrl}/v1/notifications",
            new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsCreated()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "a", message = "m", agent = "alpha" }, Json.Options);
        await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "b", message = "m", agent = "beta" }, Json.Options);

        var list = await c.GetFromJsonAsync<List<NotificationDto>>($"{fx.BaseUrl}/v1/notifications", Json.Options);
        Assert.NotNull(list);
        Assert.True(list!.Count >= 2);
    }

    [Fact]
    public async Task List_FilterByAgent()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "t", message = "m", agent = "only-me" }, Json.Options);

        var filtered = await c.GetFromJsonAsync<List<NotificationDto>>($"{fx.BaseUrl}/v1/notifications?agent=only-me", Json.Options);
        Assert.NotNull(filtered);
        Assert.All(filtered!, n => Assert.Equal("only-me", n.Agent));
    }

    [Fact]
    public async Task List_FilterAcceptsHyphenatedType()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "decision", message = "m", type = "input_required" }, Json.Options);

        var filtered = await c.GetFromJsonAsync<List<NotificationDto>>(
            $"{fx.BaseUrl}/v1/notifications?type=input-required", Json.Options);
        Assert.NotNull(filtered);
        Assert.NotEmpty(filtered!);
        Assert.All(filtered!, n => Assert.Equal(NotificationTypes.InputRequired, n.Type));
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var resp = await c.GetAsync($"{fx.BaseUrl}/v1/notifications/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetById_Found()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var created = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "find me", message = "m" }, Json.Options);
        var dto = await created.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        var fetched = await c.GetFromJsonAsync<NotificationDto>($"{fx.BaseUrl}/v1/notifications/{dto!.Id}", Json.Options);
        Assert.NotNull(fetched);
        Assert.Equal(dto.Id, fetched!.Id);
    }

    [Fact]
    public async Task Patch_Resolve()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var created = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "patch me", message = "m" }, Json.Options);
        var dto = await created.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        var patch = new HttpRequestMessage(new HttpMethod("PATCH"), $"{fx.BaseUrl}/v1/notifications/{dto!.Id}")
        {
            Content = JsonContent.Create(new { status = "resolved" }, options: Json.Options)
        };
        var resp = await c.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var updated = await resp.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.Equal(NotificationStatus.Resolved, updated!.Status);
    }

    [Fact]
    public async Task Dismiss_Convenience()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var created = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "dismiss me", message = "m" }, Json.Options);
        var dto = await created.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        var resp = await c.PostAsync($"{fx.BaseUrl}/v1/notifications/{dto!.Id}/dismiss",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var updated = await resp.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.Equal(NotificationStatus.Dismissed, updated!.Status);
    }

    [Fact]
    public async Task Patch_InvalidTransition_Returns400()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var created = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "t", message = "m" }, Json.Options);
        var dto = await created.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        // Active -> dismissed is fine, then dismissed -> resolved should fail.
        var p1 = new HttpRequestMessage(new HttpMethod("PATCH"), $"{fx.BaseUrl}/v1/notifications/{dto!.Id}")
        {
            Content = JsonContent.Create(new { status = "dismissed" }, options: Json.Options)
        };
        var r1 = await c.SendAsync(p1);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var p2 = new HttpRequestMessage(new HttpMethod("PATCH"), $"{fx.BaseUrl}/v1/notifications/{dto.Id}")
        {
            Content = JsonContent.Create(new { status = "resolved" }, options: Json.Options)
        };
        var r2 = await c.SendAsync(p2);
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }

    [Fact]
    public async Task Health_V1_HasFields()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var dto = await c.GetFromJsonAsync<HealthResponse>($"{fx.BaseUrl}/v1/health", Json.Options);
        Assert.NotNull(dto);
        Assert.Equal("ok", dto!.Status);
        Assert.True(dto.UptimeSeconds >= 0);
        Assert.True(dto.Pid > 0);
    }

    [Fact]
    public async Task Callbacks_Fire()
    {
        await using var fx = await ApiFixture.StartAsync();
        var created = new TaskCompletionSource<string>();
        var updated = new TaskCompletionSource<string>();
        fx.Callbacks.Created = n => created.TrySetResult(n.Id);
        fx.Callbacks.Updated = n => updated.TrySetResult(n.Id);

        using var c = fx.AuthedClient();
        var resp = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "cb", message = "m", key = "cb-key-" + Guid.NewGuid().ToString("N") }, Json.Options);
        var dto = await resp.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        var gotCreated = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(dto!.Id, gotCreated);

        // Dedup should trigger OnUpdated.
        await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "cb updated", message = "m2", key = dto.Key }, Json.Options);
        var gotUpdated = await updated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(dto.Id, gotUpdated);
    }

    [Fact]
    public async Task CallbackFailure_DoesNotFailRequest()
    {
        await using var fx = await ApiFixture.StartAsync();
        fx.Callbacks.Created = _ => throw new InvalidOperationException("simulated UI failure");
        using var c = fx.AuthedClient();
        var resp = await c.PostAsJsonAsync($"{fx.BaseUrl}/v1/notifications",
            new { title = "still persists", message = "m" }, Json.Options);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
