using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentNotify.Protocol;

namespace AgentNotify.Tests;

public sealed class AepApiTests
{
    [Fact]
    public async Task NotificationSent_MapsAgentNotifyExtension()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(AepEventTypes.NotificationSent, AepContentTypes.Notification, "Build finished");
        source.Workspace = new AepWorkspace { Cwd = "/work/agent-notify", ProjectPath = "/work/agent-notify" };
        source.Extensions = new Dictionary<string, JsonElement>
        {
            ["x-agentnotify"] = JsonSerializer.SerializeToElement(new
            {
                title = "Tests passed",
                notification_type = "success",
                priority = "high",
                key = "build-finished",
                project = "agent-notify"
            })
        };

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var notification = await response.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.NotNull(notification);
        Assert.Equal("Tests passed", notification!.Title);
        Assert.Equal("Build finished", notification.Message);
        Assert.Equal(NotificationTypes.Success, notification.Type);
        Assert.Equal(NotificationPriority.High, notification.Priority);
        Assert.Equal("build-finished", notification.Key);
        Assert.Equal("codex", notification.Agent);
        Assert.Equal("agent-notify", notification.Project);
        Assert.Equal("0.1", notification.Metadata!["aepVersion"].GetString());
        Assert.Equal(source.Id, notification.Metadata["aepEventId"].GetString());
    }

    [Fact]
    public async Task QuestionAsked_UsesAttentionDefaults()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(AepEventTypes.QuestionAsked, AepContentTypes.Question, "Allow the migration?");

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var notification = await response.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.Equal("Codex needs input", notification!.Title);
        Assert.Equal(NotificationTypes.InputRequired, notification.Type);
        Assert.Equal(NotificationPriority.High, notification.Priority);
        Assert.StartsWith("aep:", notification.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayedEvent_ReturnsTheOriginalNotificationAfterResolution()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(AepEventTypes.NotificationSent, AepContentTypes.Notification, "first");
        var outboundEnqueues = 0;
        fx.Callbacks.PersistOutbound = (_, _) =>
        {
            Interlocked.Increment(ref outboundEnqueues);
            return Task.CompletedTask;
        };

        var firstResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);
        var first = await firstResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        var secondResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);
        var second = await secondResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        var resolveResponse = await client.PatchAsJsonAsync(
            $"{fx.BaseUrl}/v1/notifications/{first!.Id}",
            new UpdateNotificationRequest { Status = NotificationStatus.Resolved },
            Json.Options);
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);

        var thirdResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);
        var third = await thirdResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(first.Id, third!.Id);
        Assert.Equal(NotificationStatus.Resolved, third.Status);
        Assert.Equal(1, outboundEnqueues);
    }

    [Fact]
    public async Task ExplicitConditionKey_UpdatesTheActiveNotification()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var firstEvent = Event(AepEventTypes.NotificationSent, AepContentTypes.Notification, "first");
        firstEvent.Extensions = AgentNotifyExtension(new { key = "shared-condition" });

        var firstResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", firstEvent, Json.Options);
        var first = await firstResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        var secondEvent = Event(AepEventTypes.NotificationSent, AepContentTypes.Notification, "updated");
        secondEvent.Extensions = AgentNotifyExtension(new { key = "shared-condition" });
        var secondResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", secondEvent, Json.Options);
        var second = await secondResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal("updated", second.Message);
    }

    [Theory]
    [InlineData("1.0", "notification.sent", "notification")]
    [InlineData("0.1", "model.response", "response")]
    [InlineData("0.1", "question.asked", "notification")]
    public async Task UnsupportedEnvelope_Returns400(string version, string eventType, string contentType)
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(eventType, contentType, "text");
        source.AepVersion = version;

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EventsEndpoint_RequiresBearerAuthentication()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.ClientWithToken(null);

        var response = await client.PostAsJsonAsync(
            $"{fx.BaseUrl}/v1/events",
            Event(AepEventTypes.NotificationSent, AepContentTypes.Notification, "text"),
            Json.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownVendorExtension_IsIgnored()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(AepEventTypes.NotificationSent, AepContentTypes.Notification, "text");
        source.Extensions = new Dictionary<string, JsonElement>
        {
            ["x-example"] = JsonSerializer.SerializeToElement(new { anything = true })
        };

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UnknownAgentNotifyExtensionField_IsRejected()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(AepEventTypes.NotificationSent, AepContentTypes.Notification, "text");
        source.Extensions = AgentNotifyExtension(new { future_field = true });

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Dictionary<string, JsonElement> AgentNotifyExtension(object value) => new()
    {
        ["x-agentnotify"] = JsonSerializer.SerializeToElement(value)
    };

    private static AepEvent Event(string eventType, string contentType, string text) => new()
    {
        AepVersion = "0.1",
        Id = $"evt_{Guid.NewGuid():N}",
        Type = eventType,
        Time = DateTimeOffset.UtcNow,
        Agent = new AepAgent { Slug = "codex", DisplayName = "Codex", InstanceId = "test-session" },
        Session = new AepSession { ConversationId = "conversation-1", TurnId = "turn-1" },
        Content = [new AepContent { Type = contentType, Text = text, Style = "plain_text" }]
    };
}
