using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentNotify.Protocol;

namespace AgentNotify.Tests;

public sealed class InteractionApiTests
{
    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value, AgentNotify.Protocol.Json.Options),
            System.Text.Encoding.UTF8, "application/json");

    private static object PermissionRequest(string prompt = "Deploy to prod?") => new
    {
        agent = "codex",
        project = "shop",
        session_id = "sess-1",
        kind = "permission",
        prompt,
        choices = new[]
        {
            new { id = "allow-once", label = "Allow once" },
            new { id = "deny", label = "Deny" }
        },
        ttl_seconds = 300
    };

    [Fact]
    public async Task Request_CreatesAndReturnsDigest()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var resp = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request", JsonBody(PermissionRequest()));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);
        Assert.NotNull(dto);
        Assert.Equal(InteractionStatus.Pending, dto!.Status);
        Assert.Equal(64, dto.RequestDigest.Length);
        Assert.NotEmpty(dto.Nonce);
        Assert.Equal(2, dto.Choices.Count);
    }

    [Fact]
    public async Task Request_RejectsInvalidBody()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var resp = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request",
            JsonBody(new { kind = "permission", prompt = "x", choices = new[] { new { id = "only", label = "Only" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Request_RequiresAuth()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.ClientWithToken(null);
        var resp = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request", JsonBody(PermissionRequest()));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Respond_RoundTrip_AndConflict()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = await (await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request", JsonBody(PermissionRequest())))
            .Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);

        var answer = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/{created!.Id}/respond",
            JsonBody(new { response_id = "r1", request_digest = created.RequestDigest, choice_id = "deny", source = "desktop" }));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var answered = await answer.Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);
        Assert.Equal(InteractionStatus.Answered, answered!.Status);
        Assert.Equal("deny", answered.Response!.ChoiceId);

        var replay = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/{created.Id}/respond",
            JsonBody(new { response_id = "r1", request_digest = created.RequestDigest, choice_id = "deny", source = "desktop" }));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var conflict = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/{created.Id}/respond",
            JsonBody(new { response_id = "r2", request_digest = created.RequestDigest, choice_id = "allow-once", source = "relay" }));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Respond_RejectsStaleDigest()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = await (await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request", JsonBody(PermissionRequest())))
            .Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);

        var resp = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/{created!.Id}/respond",
            JsonBody(new { response_id = "r1", request_digest = new string('0', 64), choice_id = "deny", source = "cli" }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Wait_ReturnsAfterAnswer()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = await (await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request", JsonBody(PermissionRequest())))
            .Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);

        using var waiter = fx.AuthedClient();
        var waitTask = waiter.GetAsync($"{fx.BaseUrl}/v1/interactions/{created!.Id}/wait?timeout=20");
        await Task.Delay(300);
        var answer = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/{created.Id}/respond",
            JsonBody(new { response_id = "w1", request_digest = created.RequestDigest, choice_id = "allow-once", source = "cli" }));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        var waited = await waitTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, waited.StatusCode);
        var dto = await waited.Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);
        Assert.Equal(InteractionStatus.Answered, dto!.Status);
    }

    [Fact]
    public async Task Publish_AutoPublishesOnRequest_AndEndpointIsIdempotent()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();

        var profiles = new AgentNotify.Core.Delivery.ProviderProfileService(
            fx.DeliveryRepository,
            new AgentNotify.Core.Delivery.AesGcmSecretProtector(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        var profile = await profiles.SaveAsync(null, "Relay", "relay", true,
            "{\"relay_url\":\"https://relay.example.com\"}",
            new Dictionary<string, string> { { "installation_token", "inst_testtoken1234567890" } });
        await fx.DeliveryRepository.UpsertRouteAsync(new AgentNotify.Core.Delivery.DeliveryRoute
        {
            Name = "Phone",
            ProviderId = profile.Id,
            Enabled = true,
            IncludeMessage = true
        });

        var created = await (await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request", JsonBody(PermissionRequest())))
            .Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);

        // Auto-publish ran inside the request: one sealed interaction payload is queued.
        var outbox = await fx.DeliveryRepository.ListOutboxAsync();
        var item = Assert.Single(outbox);
        Assert.Equal(profile.Id, item.ProviderId);
        using var envelope = JsonDocument.Parse(item.PayloadJson);
        Assert.Equal("interaction-request", envelope.RootElement.GetProperty("payload_kind").GetString());

        // Manual republish is idempotent: the deterministic outbox id already exists.
        var republish = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/{created!.Id}/publish",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, republish.StatusCode);
        Assert.Single(await fx.DeliveryRepository.ListOutboxAsync());

        var missing = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/does-not-exist/publish",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task List_FiltersPending_AndCancelSettles()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = await (await client.PostAsync($"{fx.BaseUrl}/v1/interactions/request",
                JsonBody(new { agent = "claude", kind = "text", prompt = "Name?", text_max_length = 50 })))
            .Content.ReadFromJsonAsync<InteractionDto>(AgentNotify.Protocol.Json.Options);

        var pending = await client.GetFromJsonAsync<List<InteractionDto>>(
            $"{fx.BaseUrl}/v1/interactions?pending=true", AgentNotify.Protocol.Json.Options);
        Assert.Contains(pending!, i => i.Id == created!.Id);

        var cancel = await client.PostAsync($"{fx.BaseUrl}/v1/interactions/{created!.Id}/cancel",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var pendingAfter = await client.GetFromJsonAsync<List<InteractionDto>>(
            $"{fx.BaseUrl}/v1/interactions?pending=true", AgentNotify.Protocol.Json.Options);
        Assert.DoesNotContain(pendingAfter!, i => i.Id == created.Id);

        var missing = await client.GetAsync($"{fx.BaseUrl}/v1/interactions/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
