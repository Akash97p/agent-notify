using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentNotify.Protocol;

namespace AgentNotify.Tests;

public sealed class ArcApiTests
{
    [Fact]
    public async Task RequestCreated_MapsPortableFieldsAndAgentNotifyExtension()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Completion, "Build finished");
        source.Request!.Key = "build-finished";
        source.Request.Title = "Tests passed";
        source.Request.Priority = NotificationPriority.High;
        source.Context = new ArcContext
        {
            Project = "agent-notify",
            Cwd = "/work/agent-notify",
            SessionId = "test-session",
            CorrelationId = "turn-1"
        };
        source.Extensions = AgentNotifyExtension(new { notification_type = "success" });

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
        Assert.Equal("0.1", notification.Metadata!["arcVersion"].GetString());
        Assert.Equal(source.EventId, notification.Metadata["arcEventId"].GetString());
        Assert.Equal("turn-1", notification.Metadata["arcCorrelationId"].GetString());
    }

    [Theory]
    [InlineData(ArcRequestKinds.Information, NotificationTypes.Info, NotificationPriority.Normal, "Update from Codex")]
    [InlineData(ArcRequestKinds.Question, NotificationTypes.InputRequired, NotificationPriority.High, "Codex needs input")]
    [InlineData(ArcRequestKinds.Permission, NotificationTypes.PermissionRequired, NotificationPriority.High, "Codex needs permission")]
    [InlineData(ArcRequestKinds.Blocked, NotificationTypes.Blocked, NotificationPriority.High, "Codex is blocked")]
    [InlineData(ArcRequestKinds.Failure, NotificationTypes.Error, NotificationPriority.High, "Codex reported a failure")]
    [InlineData(ArcRequestKinds.Completion, NotificationTypes.Completed, NotificationPriority.Normal, "Codex completed work")]
    public async Task RequestCreated_UsesKindDefaults(
        string kind,
        string expectedType,
        NotificationPriority expectedPriority,
        string expectedTitle)
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();

        var response = await client.PostAsJsonAsync(
            $"{fx.BaseUrl}/v1/events",
            Event(ArcEventTypes.RequestCreated, kind, "Attention required"),
            Json.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var notification = await response.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);
        Assert.Equal(expectedTitle, notification!.Title);
        Assert.Equal(expectedType, notification.Type);
        Assert.Equal(expectedPriority, notification.Priority);
        Assert.StartsWith("arc:", notification.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayedUnkeyedEvent_ReturnsOriginalAfterResolutionWithoutAnotherDelivery()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Information, "first");
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
        await client.PatchAsJsonAsync(
            $"{fx.BaseUrl}/v1/notifications/{first!.Id}",
            new UpdateNotificationRequest { Status = NotificationStatus.Resolved },
            Json.Options);
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
    public async Task RequestUpdated_RequiresAndUpdatesAnActiveCondition()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Blocked, "first");
        created.Request!.Key = "shared-condition";
        var firstResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", created, Json.Options);
        var first = await firstResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        var update = Event(ArcEventTypes.RequestUpdated, ArcRequestKinds.Blocked, "updated");
        update.Request!.Key = "shared-condition";
        var updateResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", update, Json.Options);
        var updated = await updateResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal(first!.Id, updated!.Id);
        Assert.Equal("updated", updated.Message);

        update.Request.Key = "missing-condition";
        var missingResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", update, Json.Options);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task RequestResolved_ClosesConditionAndIsIdempotent()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Question, "Choose A or B");
        created.Request!.Key = "choice";
        var createdResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", created, Json.Options);
        var notification = await createdResponse.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        var resolved = Event(ArcEventTypes.RequestResolved, null, null);
        resolved.Request!.Key = "choice";
        var firstResolution = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", resolved, Json.Options);
        var secondResolution = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", resolved, Json.Options);
        var result = await secondResolution.Content.ReadFromJsonAsync<NotificationDto>(Json.Options);

        Assert.Equal(HttpStatusCode.OK, firstResolution.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResolution.StatusCode);
        Assert.Equal(notification!.Id, result!.Id);
        Assert.Equal(NotificationStatus.Resolved, result.Status);

        resolved.Request.Key = "unknown";
        var missingResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", resolved, Json.Options);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Theory]
    [InlineData("1.0", "request.created", "information")]
    [InlineData("0.1", "notification.sent", "information")]
    [InlineData("0.1", "request.created", "unsupported")]
    [InlineData("0.1", "request.updated", "information")]
    public async Task UnsupportedOrIncompleteEnvelope_Returns400(string version, string eventType, string? kind)
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(eventType, kind, "text");
        source.ArcVersion = version;

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
            Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Information, "text"),
            Json.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownVendorExtensionIsIgnored_ButUnknownContractFieldsAreRejected()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Information, "text");
        source.Extensions = new Dictionary<string, JsonElement>
        {
            ["x-example"] = JsonSerializer.SerializeToElement(new { anything = true })
        };
        var accepted = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        source.EventId = $"arc_{Guid.NewGuid():N}";
        source.UnknownFields = new Dictionary<string, JsonElement>
        {
            ["future_field"] = JsonSerializer.SerializeToElement(true)
        };
        var rejected = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task UnknownAgentNotifyExtensionField_IsRejected()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var source = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Information, "text");
        source.Extensions = AgentNotifyExtension(new { future_field = true });

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Dictionary<string, JsonElement> AgentNotifyExtension(object value) => new()
    {
        ["x-agentnotify"] = JsonSerializer.SerializeToElement(value)
    };

    private static ArcEvent Event(string eventType, string? kind, string? message) => new()
    {
        ArcVersion = "0.1",
        EventId = $"arc_{Guid.NewGuid():N}",
        EventType = eventType,
        OccurredAt = DateTimeOffset.UtcNow,
        Sender = new ArcSender { Id = "codex", Name = "Codex", InstanceId = "test-session" },
        Request = new ArcRequest { Kind = kind, Message = message }
    };
}
