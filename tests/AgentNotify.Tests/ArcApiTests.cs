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
        var notification = (await response.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))?.Notification;
        Assert.NotNull(notification);
        Assert.Equal("Tests passed", notification!.Title);
        Assert.Equal("Build finished", notification.Message);
        Assert.Equal(NotificationTypes.Success, notification.Type);
        Assert.Equal(NotificationPriority.High, notification.Priority);
        Assert.Equal("build-finished", notification.Key);
        Assert.Equal("codex", notification.Agent);
        Assert.Equal("agent-notify", notification.Project);
        Assert.Equal("0.2", notification.Metadata!["arcVersion"].GetString());
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
        var notification = (await response.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;
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
        var first = (await firstResponse.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;
        var secondResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);
        var second = (await secondResponse.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;
        await client.PatchAsJsonAsync(
            $"{fx.BaseUrl}/v1/notifications/{first!.Id}",
            new UpdateNotificationRequest { Status = NotificationStatus.Resolved },
            Json.Options);
        var thirdResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);
        var third = (await thirdResponse.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;

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
        var first = (await firstResponse.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;

        var update = Event(ArcEventTypes.RequestUpdated, ArcRequestKinds.Blocked, "updated");
        update.Request!.Key = "shared-condition";
        var updateResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", update, Json.Options);
        var updated = (await updateResponse.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;

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
        var notification = (await createdResponse.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;

        var resolved = Event(ArcEventTypes.RequestResolved, null, null);
        resolved.Request!.Key = "choice";
        var firstResolution = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", resolved, Json.Options);
        var secondResolution = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", resolved, Json.Options);
        var result = (await secondResolution.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options))!.Notification;

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
    [InlineData("0.1", "request.created", "information")]
    [InlineData("0.2", "notification.sent", "information")]
    [InlineData("0.2", "request.created", "unsupported")]
    [InlineData("0.2", "request.updated", "information")]
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

    [Fact]
    public async Task AnswerableRequest_OpensAnInteractionAndAcceptsOneAnswer()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = Answerable("release-approval", "May I publish the release?");

        var createdResponse = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", created, Json.Options);
        var opened = await createdResponse.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options);

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.NotNull(opened!.Notification);
        Assert.NotNull(opened.Interaction);
        Assert.Equal(InteractionKind.Permission, opened.Interaction!.Kind);
        Assert.Equal("May I publish the release?", opened.Interaction.Prompt);
        Assert.Equal(["allow_once", "deny"], opened.Interaction.Choices!.Select(choice => choice.Id));
        Assert.Equal(InteractionStatus.Pending, opened.Interaction.Status);

        var answer = Answer("release-approval", opened.Interaction.RequestDigest, "allow_once", opened.Interaction.Nonce);
        var answered = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", answer, Json.Options);
        var accepted = await answered.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal(InteractionStatus.Answered, accepted!.Interaction!.Status);
        Assert.Equal("allow_once", accepted.Interaction.Response!.ChoiceId);
        // An answered condition is no longer waiting for attention.
        Assert.Equal(NotificationStatus.Resolved, accepted.Notification!.Status);

        // The first valid answer wins; a different one afterwards is refused.
        var second = Answer("release-approval", opened.Interaction.RequestDigest, "deny", opened.Interaction.Nonce);
        var refused = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", second, Json.Options);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task AnswerWithAStaleDigest_IsRefused()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = Answerable("deploy", "Deploy to production?");
        await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", created, Json.Options);

        var answer = Answer("deploy", new string('a', 64), "allow_once");
        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", answer, Json.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnswerToAnUnknownCondition_Returns404()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();

        var response = await client.PostAsJsonAsync(
            $"{fx.BaseUrl}/v1/events",
            Answer("never-asked", new string('b', 64), "allow_once"),
            Json.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResolvingAnAnswerableCondition_WithdrawsTheOpenQuestion()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", Answerable("stale", "Still needed?"), Json.Options);

        var resolved = Event(ArcEventTypes.RequestResolved, null, null);
        resolved.Request!.Key = "stale";
        resolved.Request.Outcome = ArcOutcomes.NotNeeded;
        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", resolved, Json.Options);
        var closed = await response.Content.ReadFromJsonAsync<ArcEventResponse>(Json.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(NotificationStatus.Resolved, closed!.Notification!.Status);
        Assert.Equal(InteractionStatus.Cancelled, closed.Interaction!.Status);
    }

    [Fact]
    public async Task AnInvalidQuestion_IsRejectedBeforeAnyNotificationIsStored()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        var created = Answerable("bad-question", "Pick one");
        // A choice kind needs at least two options.
        created.Request!.Response!.Choices = [new ArcChoice { Id = "only", Label = "Only" }];

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", created, Json.Options);
        var stored = await client.GetFromJsonAsync<List<NotificationDto>>($"{fx.BaseUrl}/v1/notifications", Json.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(stored!);
    }

    [Theory]
    // A response event may carry only the key alongside its answer.
    [InlineData("response_on_request")]
    // An outcome belongs to a resolution, not to a created request.
    [InlineData("outcome_on_create")]
    // Exactly one of choice_id or text.
    [InlineData("both_answer_fields")]
    public async Task MalformedBidirectionalEnvelope_Returns400(string shape)
    {
        await using var fx = await ApiFixture.StartAsync();
        using var client = fx.AuthedClient();
        ArcEvent source;
        switch (shape)
        {
            case "response_on_request":
                source = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Information, "text");
                source.Response = new ArcResponse { ResponseId = "r1", Digest = new string('c', 64), ChoiceId = "x" };
                break;
            case "outcome_on_create":
                source = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Information, "text");
                source.Request!.Outcome = ArcOutcomes.Answered;
                break;
            default:
                source = Answer("key", new string('d', 64), "allow_once");
                source.Response!.Text = "also text";
                break;
        }

        var response = await client.PostAsJsonAsync($"{fx.BaseUrl}/v1/events", source, Json.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static ArcEvent Answerable(string key, string message)
    {
        var source = Event(ArcEventTypes.RequestCreated, ArcRequestKinds.Permission, message);
        source.Request!.Key = key;
        source.Request.Response = new ArcResponseSpec
        {
            Kind = ArcResponseKinds.Permission,
            Choices =
            [
                new ArcChoice { Id = "allow_once", Label = "Allow once" },
                new ArcChoice { Id = "deny", Label = "Deny" }
            ],
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        return source;
    }

    private static ArcEvent Answer(string key, string digest, string choiceId, string? nonce = null)
    {
        var source = Event(ArcEventTypes.ResponseSubmitted, null, null);
        source.Request!.Key = key;
        source.Response = new ArcResponse
        {
            ResponseId = $"resp_{Guid.NewGuid():N}",
            Digest = digest,
            Nonce = nonce,
            ChoiceId = choiceId,
            Source = "cli"
        };
        return source;
    }

    private static Dictionary<string, JsonElement> AgentNotifyExtension(object value) => new()
    {
        ["x-agentnotify"] = JsonSerializer.SerializeToElement(value)
    };

    private static ArcEvent Event(string eventType, string? kind, string? message) => new()
    {
        ArcVersion = "0.2",
        EventId = $"arc_{Guid.NewGuid():N}",
        EventType = eventType,
        OccurredAt = DateTimeOffset.UtcNow,
        Sender = new ArcSender { Id = "codex", Name = "Codex", InstanceId = "test-session" },
        Request = new ArcRequest { Kind = kind, Message = message }
    };
}
