using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Protocol;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Domain;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Tests;

public sealed class InteractionRelayPublisherTests : IAsyncLifetime
{
    private string _db = null!;
    private SqliteDeliveryRepository _repository = null!;
    private ProviderProfileService _profiles = null!;

    public async Task InitializeAsync()
    {
        _db = Path.Combine(Path.GetTempPath(), $"an-ixrelay-{Guid.NewGuid():N}.db");
        _repository = new SqliteDeliveryRepository(_db);
        await _repository.InitializeAsync();
        _profiles = new ProviderProfileService(
            _repository,
            new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_db + suffix); } catch { }
        }
        return Task.CompletedTask;
    }

    private static Interaction MakeInteraction() => new()
    {
        Agent = "codex",
        Project = "shop",
        SessionId = "sess-1",
        Kind = InteractionKind.Permission,
        Prompt = "Deploy to prod?",
        Choices =
        [
            new InteractionChoice { Id = "allow", Label = "Allow once" },
            new InteractionChoice { Id = "deny", Label = "Deny" }
        ],
        RequestDigest = new string('a', 64),
        Nonce = "nonce-1",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
    };

    private async Task<(ProviderProfile Profile, DeliveryRoute Route)> RelayRouteAsync(
        bool includeMessage = true,
        string? typeId = null,
        NotificationPriority minimum = NotificationPriority.Low)
    {
        var profile = await _profiles.SaveAsync(null, "Relay", "relay", true, "{\"relay_url\":\"https://relay.example.com\"}",
            new Dictionary<string, string> { { "installation_token", "inst_testtoken1234567890" } });
        var route = new DeliveryRoute
        {
            Name = "Phone",
            ProviderId = profile.Id,
            Enabled = true,
            TypeId = typeId,
            MinimumPriority = minimum,
            IncludeMessage = includeMessage
        };
        await _repository.UpsertRouteAsync(route);
        return (profile, route);
    }

    [Fact]
    public async Task Publish_EnqueuesSealedPayloadForRelayRoutes()
    {
        var (profile, _) = await RelayRouteAsync();
        var publisher = new InteractionRelayPublisher(_repository);
        var interaction = MakeInteraction();
        var count = await publisher.PublishAsync(ApiDtoMapper.ToDto(interaction));

        Assert.Equal(1, count);
        var outbox = await _repository.ListOutboxAsync();
        var item = Assert.Single(outbox);
        Assert.Equal(profile.Id, item.ProviderId);
        Assert.Equal($"interaction:{interaction.Id}:{profile.Id}", item.Id);

        using var doc = JsonDocument.Parse(item.PayloadJson);
        Assert.Equal("interaction-request", doc.RootElement.GetProperty("payload_kind").GetString());
        Assert.Equal("1", doc.RootElement.GetProperty("contract_version").GetString());
        var payload = doc.RootElement.GetProperty("interaction");
        Assert.Equal("permission", payload.GetProperty("kind").GetString());
        Assert.Equal(new string('a', 64), payload.GetProperty("request_digest").GetString());
        Assert.Equal("nonce-1", payload.GetProperty("nonce").GetString());
        Assert.Equal(2, payload.GetProperty("choices").GetArrayLength());
    }

    [Fact]
    public async Task Publish_IsIdempotent()
    {
        await RelayRouteAsync();
        var publisher = new InteractionRelayPublisher(_repository);
        var dto = ApiDtoMapper.ToDto(MakeInteraction());
        Assert.Equal(1, await publisher.PublishAsync(dto));
        Assert.Equal(0, await publisher.PublishAsync(dto));
        Assert.Single(await _repository.ListOutboxAsync());
    }

    [Fact]
    public async Task Publish_SkipsBodylessNonRelayAndFilteredRoutes()
    {
        // Bodyless relay route: choices/nonces are message content, never sent.
        await RelayRouteAsync(includeMessage: false);
        // Non-relay route with bodies: never carries interaction payloads.
        var webhook = await _profiles.SaveAsync(null, "Hook", "webhook", true, "{}",
            new Dictionary<string, string>());
        await _repository.UpsertRouteAsync(new DeliveryRoute
        {
            Name = "Hook route", ProviderId = webhook.Id, Enabled = true, IncludeMessage = true
        });
        // Critical-only relay route: permission (High) does not reach it.
        var critical = await _profiles.SaveAsync(null, "Relay 2", "relay", true, "{}",
            new Dictionary<string, string>());
        await _repository.UpsertRouteAsync(new DeliveryRoute
        {
            Name = "Critical", ProviderId = critical.Id, Enabled = true,
            MinimumPriority = NotificationPriority.Critical, IncludeMessage = true
        });

        var publisher = new InteractionRelayPublisher(_repository);
        Assert.Equal(0, await publisher.PublishAsync(ApiDtoMapper.ToDto(MakeInteraction())));
        Assert.Empty(await _repository.ListOutboxAsync());
    }

    [Fact]
    public async Task Publish_HonorsTypeProjectAndAgentFilters()
    {
        var (profile, _) = await RelayRouteAsync(typeId: "input_required");
        var publisher = new InteractionRelayPublisher(_repository);
        // Permission maps to permission_required, which this route does not take.
        Assert.Equal(0, await publisher.PublishAsync(ApiDtoMapper.ToDto(MakeInteraction())));

        await _repository.UpsertRouteAsync(new DeliveryRoute
        {
            Name = "Shop codex", ProviderId = profile.Id, Enabled = true,
            Project = "shop", Agent = "codex", IncludeMessage = true
        });
        Assert.Equal(1, await publisher.PublishAsync(ApiDtoMapper.ToDto(MakeInteraction())));
    }

    private static class ApiDtoMapper
    {
        public static InteractionDto ToDto(Interaction item) => AgentNotify.Api.DtoMapper.ToDto(item);
    }
}

public sealed class RelayCursorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"an-cursor-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Cursor_RoundTripsPerProvider()
    {
        var store = new RelayCursorStore(_dir);
        Assert.Equal("", await store.GetAsync("p1"));
        await store.SetAsync("p1", "cursor-42");
        Assert.Equal("cursor-42", await store.GetAsync("p1"));
        Assert.Equal("", await store.GetAsync("p2"));
        await store.SetAsync("p1", "cursor-43");
        Assert.Equal("cursor-43", await store.GetAsync("p1"));
    }
}

public sealed class InteractionResponseSyncTests
{
    private const string BrokerBase = "http://127.0.0.1:9";

    private static RelayPollTarget Target() => new(
        "provider-1", "Relay", "https://relay.example.com", false,
        "inst_testtoken1234567890", "install-1");

    private static HttpClient RelayClient(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public List<HttpRequestMessage> Seen { get; } = [];
        public StubHandler(HttpResponseMessage response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(_response);
        }
    }

    private sealed class BrokerStub : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public List<string> Bodies { get; } = [];
        public BrokerStub(HttpStatusCode status = HttpStatusCode.OK) => _status = status;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task Poll_AppliesValidAnswer()
    {
        var relay = RelayClient("""{"responses":[{"contract_version":"1","response_id":"r1","interaction_id":"ix","request_digest":"d","nonce":"n","choice_id":"allow","installation_id":"install-1","device_id":"phone"}],"next_cursor":"c2"}""");
        var broker = new BrokerStub();
        var sync = new InteractionResponseSync(relay, new HttpClient(broker), BrokerBase);

        var outcome = await sync.PollOnceAsync(Target(), "c1");

        Assert.True(outcome.Succeeded);
        Assert.Equal("c2", outcome.NextCursor);
        var answer = Assert.Single(outcome.Answers);
        Assert.True(answer.Applied);
        Assert.Single(broker.Bodies);
        Assert.Contains("/v1/interactions/ix/respond", broker.Bodies[0]);
    }

    [Fact]
    public async Task Poll_DropsWrongInstallationBeforeBroker()
    {
        var relay = RelayClient("""{"responses":[{"response_id":"r1","interaction_id":"ix","request_digest":"d","installation_id":"other-install"}],"next_cursor":"c2"}""");
        var broker = new BrokerStub();
        var sync = new InteractionResponseSync(relay, new HttpClient(broker), BrokerBase);

        var outcome = await sync.PollOnceAsync(Target(), "");

        Assert.True(outcome.Succeeded);
        Assert.False(Assert.Single(outcome.Answers).Applied);
        Assert.Empty(broker.Bodies);
    }

    [Fact]
    public async Task Poll_SkipsMalformedItems_ButAdvancesCursor()
    {
        var relay = RelayClient("""{"responses":[{"response_id":"","interaction_id":""}],"next_cursor":"c9"}""");
        var broker = new BrokerStub();
        var sync = new InteractionResponseSync(relay, new HttpClient(broker), BrokerBase);

        var outcome = await sync.PollOnceAsync(Target(), "c8");

        Assert.True(outcome.Succeeded);
        Assert.Equal("c9", outcome.NextCursor);
        Assert.Empty(broker.Bodies);
    }

    [Fact]
    public async Task Poll_RelayAuthFailure_KeepsCursor()
    {
        var relay = RelayClient("{}", HttpStatusCode.Unauthorized);
        var sync = new InteractionResponseSync(relay, new HttpClient(new BrokerStub()), BrokerBase);

        var outcome = await sync.PollOnceAsync(Target(), "c1");

        Assert.False(outcome.Succeeded);
        Assert.Equal("c1", outcome.NextCursor);
        Assert.Contains("token", outcome.Error);
    }

    [Fact]
    public async Task Poll_InvalidRelayJson_KeepsCursor()
    {
        var relay = RelayClient("not json");
        var sync = new InteractionResponseSync(relay, new HttpClient(new BrokerStub()), BrokerBase);

        var outcome = await sync.PollOnceAsync(Target(), "c1");

        Assert.False(outcome.Succeeded);
        Assert.Equal("c1", outcome.NextCursor);
    }

    [Fact]
    public async Task Poll_BrokerConflict_IsNotApplied_ButPollSucceeds()
    {
        var relay = RelayClient("""{"responses":[{"response_id":"r2","interaction_id":"ix","request_digest":"d"}],"next_cursor":"c3"}""");
        var broker = new BrokerStub(HttpStatusCode.Conflict);
        var sync = new InteractionResponseSync(relay, new HttpClient(broker), BrokerBase);

        var outcome = await sync.PollOnceAsync(Target(), "c2");

        Assert.True(outcome.Succeeded);
        Assert.Equal("c3", outcome.NextCursor);
        var answer = Assert.Single(outcome.Answers);
        Assert.False(answer.Applied);
        Assert.Contains("already answered", answer.Note);
    }
}
