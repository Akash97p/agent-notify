using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class PushbulletChannelTests
{
    private const string AccessToken = "o.123456789012345678901234567890";
    private const string Success = "{\"iden\":\"ujpah72o0sjAoRtnM0jc\",\"type\":\"note\",\"active\":true}";

    [Fact]
    public async Task SendsNoteToExactEndpointWithTokenOnlyInHeader()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.pushbullet.com/v2/pushes", handler.Uri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal(AccessToken, handler.AccessToken);
        Assert.DoesNotContain(AccessToken, handler.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, handler.Body!, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("note", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("Build failed", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(32, json.RootElement.GetProperty("guid").GetString()!.Length);
    }

    [Fact]
    public async Task StableGuidMakesRetryMostlyIdempotent()
    {
        var firstHandler = new Handler(HttpStatusCode.OK, Success);
        var secondHandler = new Handler(HttpStatusCode.OK, Success);
        using var first = new PushbulletChannelAdapter(new HttpClient(firstHandler));
        using var second = new PushbulletChannelAdapter(new HttpClient(secondHandler));

        await first.DeliverAsync(MakeDelivery(outboxId: "same-outbox"), CancellationToken.None);
        await second.DeliverAsync(MakeDelivery(outboxId: "same-outbox"), CancellationToken.None);

        using var firstJson = JsonDocument.Parse(firstHandler.Body!);
        using var secondJson = JsonDocument.Parse(secondHandler.Body!);
        Assert.Equal(
            firstJson.RootElement.GetProperty("guid").GetString(),
            secondJson.RootElement.GetProperty("guid").GetString());
    }

    [Fact]
    public async Task DifferentOutboxGetsDifferentGuid()
    {
        var firstHandler = new Handler(HttpStatusCode.OK, Success);
        var secondHandler = new Handler(HttpStatusCode.OK, Success);
        using var first = new PushbulletChannelAdapter(new HttpClient(firstHandler));
        using var second = new PushbulletChannelAdapter(new HttpClient(secondHandler));
        await first.DeliverAsync(MakeDelivery(outboxId: "first"), CancellationToken.None);
        await second.DeliverAsync(MakeDelivery(outboxId: "second"), CancellationToken.None);
        using var firstJson = JsonDocument.Parse(firstHandler.Body!);
        using var secondJson = JsonDocument.Parse(secondHandler.Body!);
        Assert.NotEqual(firstJson.RootElement.GetProperty("guid").GetString(), secondJson.RootElement.GetProperty("guid").GetString());
    }

    [Fact]
    public async Task HonorsRouteRedactionAndOmitsActiveContentFields()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Build\",\"message\":null,\"priority\":\"high\",\"agent\":\"codex\"}"),
            CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.DoesNotContain("Compiler failed", root.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains("Priority: high", root.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("url", out _));
        Assert.False(root.TryGetProperty("file_url", out _));
        Assert.False(root.TryGetProperty("source_device_iden", out _));
    }

    [Fact]
    public async Task BroadcastOmitsEveryTargetField()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(config: Config("all"), target: "ignored"), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.False(json.RootElement.TryGetProperty("device_iden", out _));
        Assert.False(json.RootElement.TryGetProperty("channel_tag", out _));
        Assert.False(json.RootElement.TryGetProperty("email", out _));
        Assert.False(json.RootElement.TryGetProperty("client_iden", out _));
    }

    [Theory]
    [InlineData("device", "ujpah72o0sjAoRtnM0jc", "device_iden")]
    [InlineData("channel", "agentnotify_alerts", "channel_tag")]
    [InlineData("email", "owner@example.com", "email")]
    public async Task SendsOneExplicitEncryptedTarget(string targetType, string target, string property)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(targetType), target: target),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(target, json.RootElement.GetProperty(property).GetString());
        Assert.Equal(1, new[] { "device_iden", "channel_tag", "email", "client_iden" }
            .Count(name => json.RootElement.TryGetProperty(name, out _)));
    }

    [Theory]
    [InlineData("device", "bad device")]
    [InlineData("channel", "bad/channel")]
    [InlineData("email", "not-an-email")]
    [InlineData("client", "identifier")]
    public async Task RejectsInvalidOrUnsupportedTarget(string targetType, string target)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(targetType), target: target),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task RequiresTargetForNonBroadcastProfile()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config("device"), target: null),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task RequiresExplicitQuotaAcknowledgement()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(quotaAcknowledged: false)),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("token-with-a-newline\nvalue")]
    public async Task RejectsUnsafeAccessToken(string token)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(token: token), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task BoundsUnicodePayloadByUtf8Bytes()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new
        {
            title = string.Concat(Enumerable.Repeat("😀", 600)),
            message = string.Concat(Enumerable.Repeat("😀", 10_000))
        });
        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);
        Assert.True(Encoding.UTF8.GetByteCount(handler.Body!) <= 32 * 1024);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.True(Encoding.UTF8.GetByteCount(json.RootElement.GetProperty("title").GetString()!) <= 1024);
        Assert.True(Encoding.UTF8.GetByteCount(json.RootElement.GetProperty("body").GetString()!) <= 8 * 1024);
        Assert.EndsWith("…", json.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("…", json.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true, "pushbullet_408")]
    [InlineData(HttpStatusCode.TooManyRequests, true, "pushbullet_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "pushbullet_503")]
    [InlineData(HttpStatusCode.Unauthorized, false, "pushbullet_401")]
    [InlineData(HttpStatusCode.Forbidden, false, "pushbullet_403")]
    [InlineData(HttpStatusCode.Redirect, false, "pushbullet_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"iden\":\"id\",\"type\":\"link\"}")]
    [InlineData("{\"iden\":\"id\",\"type\":\"note\",\"active\":false}")]
    [InlineData("not-json")]
    public async Task RetriesInvalidSuccessResponse(string body)
    {
        var handler = new Handler(HttpStatusCode.OK, body);
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Retryable);
        Assert.Equal("pushbullet_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsResponseBody()
    {
        var handler = new Handler(HttpStatusCode.OK, new string('x', 33 * 1024));
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("pushbullet_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task ConvertsNetworkFailureToSanitizedRetry()
    {
        var handler = new Handler(new HttpRequestException("token and target might appear here"));
        using var adapter = new PushbulletChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Retryable);
        Assert.Equal("network_error", result.ErrorCode);
    }

    private static string Config(string targetType = "all", bool quotaAcknowledged = true) =>
        JsonSerializer.Serialize(new
        {
            accessTokenSecretName = "access_token",
            targetType,
            targetSecretName = "target",
            quotaAcknowledged
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? config = null,
        string token = AccessToken,
        string? target = null,
        string outboxId = "pushbullet-outbox")
    {
        var secrets = new Dictionary<string, string> { ["access_token"] = token };
        if (target is not null) secrets["target"] = target;
        return new OutboundDelivery(
            outboxId,
            "notification",
            payload,
            new ProviderProfile
            {
                Id = "pushbullet",
                Name = "Pushbullet",
                Kind = "pushbullet",
                Enabled = true,
                ConfigJson = config ?? Config()
            },
            secrets);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response = "";
        private readonly Exception? _exception;

        public Handler(HttpStatusCode status, string response)
        {
            _status = status;
            _response = response;
        }

        public Handler(Exception exception) => _exception = exception;

        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public string? AccessToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_exception is not null) throw _exception;
            Uri = request.RequestUri;
            Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType?.MediaType;
            AccessToken = request.Headers.TryGetValues("Access-Token", out var values) ? values.Single() : null;
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}
