using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class DiscordChannelTests
{
    [Fact]
    public async Task PostsConfirmedWebhookMessageWithMentionsDisabled()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));
        var delivery = MakeDelivery("{\"username\":\"Build Agent\",\"threadId\":\"123456789012345678\"}");

        var result = await adapter.DeliverAsync(delivery, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("discord.com", handler.RequestUri!.Host);
        Assert.Equal("wait=true&thread_id=123456789012345678", handler.RequestUri.Query.TrimStart('?'));
        Assert.DoesNotContain(WebhookToken, handler.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("Build Agent", root.GetProperty("username").GetString());
        Assert.False(root.GetProperty("tts").GetBoolean());
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.False(root.GetProperty("allowed_mentions").GetProperty("replied_user").GetBoolean());
        var content = root.GetProperty("content").GetString()!;
        Assert.Contains("**[CRITICAL] Build failed**", content, StringComparison.Ordinal);
        Assert.Contains("Compiler \\*error\\* @everyone", content, StringComparison.Ordinal);
        Assert.Contains("Agent:** codex", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HonorsRouteMessageRedaction()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Secret build\",\"message\":null}"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("Compiler", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/token_token_token_token")]
    [InlineData("https://evil.example/api/webhooks/123456789012345678/token_token_token_token")]
    [InlineData("https://discord.com:444/api/webhooks/123456789012345678/token_token_token_token")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/token_token_token_token?wait=false")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/token_token_token_token#secret")]
    [InlineData("https://discord.com/api/not-webhooks/123456789012345678/token_token_token_token")]
    [InlineData("https://discord.com/api/webhooks/not-an-id/token_token_token_token")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/short")]
    public async Task RejectsNonOfficialOrMalformedWebhookUrls(string endpoint)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));
        var secrets = new Dictionary<string, string> { ["webhook_url"] = endpoint };

        var result = await adapter.DeliverAsync(MakeDelivery(secrets: secrets), CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task AcceptsVersionedOfficialWebhookPath()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));
        var secrets = new Dictionary<string, string>
        {
            ["webhook_url"] = $"https://discord.com/api/v10/webhooks/123456789012345678/{WebhookToken}"
        };

        var result = await adapter.DeliverAsync(MakeDelivery(secrets: secrets), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/api/v10/webhooks/123456789012345678/" + WebhookToken, handler.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("thread")]
    [InlineData("1234")]
    public async Task RejectsInvalidThreadId(string threadId)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(JsonSerializer.Serialize(new { threadId })),
            CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task TruncatesContentWithoutSplittingSurrogatePair()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new
        {
            title = "Build",
            message = new string('a', 1990) + "😀"
        });

        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Body!);
        var content = document.RootElement.GetProperty("content").GetString()!;
        Assert.True(content.Length <= 2000);
        Assert.False(char.IsHighSurrogate(content[^1]));
        Assert.EndsWith("…", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true, "discord_408")]
    [InlineData(HttpStatusCode.TooManyRequests, true, "discord_429")]
    [InlineData(HttpStatusCode.BadGateway, true, "discord_502")]
    [InlineData(HttpStatusCode.BadRequest, false, "discord_400")]
    [InlineData(HttpStatusCode.NotFound, false, "discord_404")]
    [InlineData(HttpStatusCode.Redirect, false, "discord_redirect")]
    public async Task ClassifiesDiscordStatusCodes(
        HttpStatusCode status,
        bool retryable,
        string errorCode)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(status));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal((int)status, result.StatusCode);
    }

    [Fact]
    public async Task RetriesNetworkFailureWithoutLeakingExceptionDetails()
    {
        var handler = new RecordingHandler(new HttpRequestException("contains-secret"));
        using var adapter = new DiscordChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Retryable);
        Assert.Equal("network_error", result.ErrorCode);
    }

    private const string WebhookToken = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuv";
    private const string WebhookUrl = "https://discord.com/api/webhooks/123456789012345678/" + WebhookToken;

    private static OutboundDelivery MakeDelivery(
        string config = "{}",
        IReadOnlyDictionary<string, string>? secrets = null,
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler *error* @everyone\",\"priority\":\"critical\",\"type\":\"error\",\"agent\":\"codex\",\"project\":\"agent-notify\"}") =>
        new(
            "delivery-discord-1",
            "notification-1",
            payload,
            new ProviderProfile
            {
                Id = "discord-1",
                Name = "Discord",
                Kind = "discord",
                Enabled = true,
                ConfigJson = config
            },
            secrets ?? new Dictionary<string, string> { ["webhook_url"] = WebhookUrl });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;

        public RecordingHandler(HttpResponseMessage response) => _response = response;
        public RecordingHandler(Exception exception) => _exception = exception;

        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_exception is not null)
                throw _exception;
            return _response!;
        }
    }
}
